using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QtsBilling.Migration;

/// <summary>What one legacy collection of the snapshot contains.</summary>
public sealed record CollectionInventory(
    string Collection,
    long Count,
    IReadOnlyDictionary<string, string> FieldTypes,
    long DuplicateKeyCount,
    bool ContainsCredentials);

/// <summary>
/// The read-only source of the migration: an **exported snapshot**, not a live database (spec §6.1, steps 2
/// and 5). The repository has no MongoDB dependency at all: the snapshot is produced once with the
/// operator's own tooling, verified by its manifest hashes, and the tool only ever reads that file set.
/// </summary>
public sealed class SnapshotSource
{
    private readonly string _directory;

    public SnapshotSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The manifest of the snapshot: the file hashes make the export verifiable.</summary>
    public ExportManifest ReadManifest()
    {
        var path = Path.Combine(_directory, "manifest.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The snapshot has no manifest.json; the export must be verified before it is imported.", path);
        }

        return JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("The snapshot manifest is empty.");
    }

    /// <summary>Verifies that every file of the snapshot still matches its manifest hash.</summary>
    public IReadOnlyList<string> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        foreach (var file in ReadManifest().Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.Path))
            {
                problems.Add($"{file.Collection}: the file is missing.");
                continue;
            }

            var hash = await HashFileAsync(file.Path, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file.Collection}: the file no longer matches its manifest hash.");
            }
        }

        return problems;
    }

    /// <summary>Classifies every collection of the snapshot: the counts, the field types and the duplicates.</summary>
    public async Task<IReadOnlyList<CollectionInventory>> InventoryAsync(
        MigrationMapping mapping,
        CancellationToken cancellationToken = default)
    {
        var inventory = new List<CollectionInventory>();

        foreach (var file in ReadManifest().Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documents = await ReadDocumentsAsync(file.Path, cancellationToken).ConfigureAwait(false);
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = 0L;
            var containsCredentials = false;

            foreach (var document in documents)
            {
                foreach (var (name, value) in document)
                {
                    var type = value?.GetValueKind().ToString() ?? "null";

                    if (IsSensitive(name))
                    {
                        containsCredentials = true;
                    }

                    types[name] = types.TryGetValue(name, out var known) && known != type ? "mixed" : type;
                }

                var key = document[mapping.Collections
                    .FirstOrDefault(candidate => string.Equals(candidate.Collection, file.Collection, StringComparison.OrdinalIgnoreCase))
                    ?.SourceKey ?? "_id"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(key) && !keys.Add(key))
                {
                    duplicates++;
                }
            }

            inventory.Add(new CollectionInventory(file.Collection, documents.Count, types, duplicates, containsCredentials));
        }

        return inventory;
    }

    /// <summary>Reads one collection file into documents (the redacted values are already removed).</summary>
    public static async Task<List<JsonObject>> ReadDocumentsAsync(string path, CancellationToken cancellationToken = default)
    {
        var documents = new List<JsonObject>();

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (JsonNode.Parse(line) is JsonObject document)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    private static bool IsSensitive(string fieldName)
    {
        var name = fieldName.ToLowerInvariant();

        return name.Contains("password", StringComparison.Ordinal)
            || name.Contains("passhash", StringComparison.Ordinal)
            || name.Contains("salt", StringComparison.Ordinal)
            || name.Contains("cvv", StringComparison.Ordinal)
            || name.Contains("cvc", StringComparison.Ordinal)
            || name.Contains("cardnumber", StringComparison.Ordinal)
            || name.Contains("pan", StringComparison.Ordinal)
            || name.Contains("token", StringComparison.Ordinal)
            || name.Contains("secret", StringComparison.Ordinal);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>The export manifest: the snapshot is only trustworthy together with its hashes.</summary>
public sealed record ExportManifest(
    string Database,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<ManifestFile> Files);

/// <summary>One exported collection file with its hash.</summary>
public sealed record ManifestFile(string Collection, string Path, long Documents, string Sha256);
