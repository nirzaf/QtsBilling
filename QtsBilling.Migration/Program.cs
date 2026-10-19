using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Migration;
using QtsBilling.Persistence.Postgres;

var commands = new Dictionary<string, Func<string[], Task<int>>>
{
    ["inventory"] = InventoryAsync,
    ["transform"] = TransformAsync,
    ["import"] = ImportAsync,
    ["reconcile"] = ReconcileAsync,
};

if (args.Length == 0 || !commands.TryGetValue(args[0], out var command))
{
    Console.Error.WriteLine("usage: QtsBilling.Migration <inventory|transform|import|reconcile> [options]");
    Console.Error.WriteLine("  the source is an exported snapshot; no database of the legacy system is contacted.");
    return 2;
}

return await command(args.Skip(1).ToArray());

static string Option(string[] args, string name, string? fallback = null)
{
    var index = Array.IndexOf(args, name);

    if (index >= 0 && index + 1 < args.Length)
    {
        return args[index + 1];
    }

    if (fallback is not null)
    {
        return fallback;
    }

    throw new ArgumentException($"The option '{name}' is required.");
}

static async Task<int> InventoryAsync(string[] args)
{
    var snapshotDirectory = Option(args, "--snapshot");
    var mapping = LoadMapping(Option(args, "--mapping"));
    var source = new SnapshotSource(snapshotDirectory);

    var problems = await source.VerifyAsync();

    if (problems.Count > 0)
    {
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"snapshot problem: {problem}");
        }

        return 1;
    }

    var inventory = await source.InventoryAsync(mapping);

    var report = new JsonObject
    {
        ["snapshot"] = snapshotDirectory,
        ["database"] = source.ReadManifest().Database,
        ["exportedAtUtc"] = source.ReadManifest().ExportedAtUtc.ToString("O"),
        ["collections"] = JsonSerializer.SerializeToNode(inventory.Select(entry => new JsonObject
        {
            ["collection"] = entry.Collection,
            ["count"] = entry.Count,
            ["duplicateKeys"] = entry.DuplicateKeyCount,
            ["containsCredentialFields"] = entry.ContainsCredentials,
            ["fieldTypes"] = JsonSerializer.SerializeToNode(entry.FieldTypes),
        })),
    };

    var output = Option(args, "--out", Path.Combine(snapshotDirectory, "inventory.json"));
    await File.WriteAllTextAsync(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    foreach (var entry in inventory)
    {
        Console.WriteLine($"{entry.Collection,-32} {entry.Count,8} documents  duplicates={entry.DuplicateKeyCount}  credentials={entry.ContainsCredentials}");
    }

    Console.WriteLine($"inventory written to {output}");
    return 0;
}

static async Task<int> TransformAsync(string[] args)
{
    var snapshotDirectory = Option(args, "--snapshot");
    var mapping = LoadMapping(Option(args, "--mapping"));
    var outputDirectory = Option(args, "--out");
    Directory.CreateDirectory(outputDirectory);

    var source = new SnapshotSource(snapshotDirectory);
    var problems = await source.VerifyAsync();

    if (problems.Count > 0)
    {
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"snapshot problem: {problem}");
        }

        return 1;
    }

    var transform = new DeterministicTransform(mapping);
    var quarantined = 0;
    var reconciliationCount = 0;

    foreach (var file in source.ReadManifest().Files)
    {
        var documents = await SnapshotSource.ReadDocumentsAsync(file.Path).ConfigureAwait(false);
        var result = transform.Transform(file.Collection, documents);
        quarantined += result.Quarantine.Count;
        reconciliationCount += result.Reconciliation.Count;

        var rowsPath = Path.Combine(outputDirectory, $"{result.TargetTable}.ndjson");
        await File.WriteAllLinesAsync(
            rowsPath,
            result.Rows.Select(row => row.ToJsonString(new JsonSerializerOptions { WriteIndented = false })))
            .ConfigureAwait(false);

        if (result.Quarantine.Count > 0)
        {
            var quarantinePath = Path.Combine(outputDirectory, $"{result.TargetTable}.quarantine.ndjson");
            await File.WriteAllLinesAsync(
                quarantinePath,
                result.Quarantine.Select(record => JsonSerializer.Serialize(record)))
                .ConfigureAwait(false);
        }

        if (result.Reconciliation.Count > 0)
        {
            var reconciliationPath = Path.Combine(outputDirectory, $"{result.TargetTable}.reconciliation.ndjson");
            await File.WriteAllLinesAsync(
                reconciliationPath,
                result.Reconciliation.Select(record => JsonSerializer.Serialize(record)))
                .ConfigureAwait(false);
        }

        Console.WriteLine($"{file.Collection,-32} -> {result.TargetTable,-24} rows={result.Rows.Count} quarantined={result.Quarantine.Count} reconciliation={result.Reconciliation.Count}");
    }

    Console.WriteLine($"transformed into {outputDirectory} ({quarantined} quarantined, {reconciliationCount} reconciliation exceptions)");
    return 0;
}

static async Task<int> ImportAsync(string[] args)
{
    var rowsDirectory = Option(args, "--rows");
    var mapping = LoadMapping(Option(args, "--mapping"));
    var connection = Environment.GetEnvironmentVariable("QTS_POSTGRES_CONNECTION")
        ?? throw new InvalidOperationException("QTS_POSTGRES_CONNECTION is not set; the tool never carries a credential.");

    var tables = new List<TransformResult>();

    foreach (var collection in mapping.Collections)
    {
        var path = Path.Combine(rowsDirectory, $"{collection.TargetTable}.ndjson");

        if (!File.Exists(path))
        {
            continue;
        }

        var rows = new List<JsonObject>();
        var quarantine = new List<QuarantineRecord>();

        foreach (var line in await File.ReadAllLinesAsync(path).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (JsonNode.Parse(line) is JsonObject row)
            {
                rows.Add(row);
            }
        }

        var quarantinePath = Path.Combine(rowsDirectory, $"{collection.TargetTable}.quarantine.ndjson");

        if (File.Exists(quarantinePath))
        {
            foreach (var line in await File.ReadAllLinesAsync(quarantinePath).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    quarantine.Add(JsonSerializer.Deserialize<QuarantineRecord>(line)!);
                }
            }
        }

        tables.Add(new TransformResult(collection.TargetTable, rows, quarantine, []));
    }

    var options = new DbContextOptionsBuilder<BillingDbContext>()
        .UseNpgsql(connection)
        .Options;

    await using var db = new BillingDbContext(options);

    var transform = new DeterministicTransform(mapping);
    var importer = new PostgresImporter(transform);
    var report = await importer.ImportAsync(db, mapping, tables, DateTimeOffset.UtcNow).ConfigureAwait(false);

    foreach (var table in report.Tables)
    {
        Console.WriteLine($"{table.Table,-24} rows={table.Rows} updated={table.Updated}");
    }

    Console.WriteLine($"identifier map rows: {report.IdentifierMapRows}; quarantined: {report.Quarantined}");
    return 0;
}

static async Task<int> ReconcileAsync(string[] args)
{
    var snapshotDirectory = Option(args, "--snapshot");
    var rowsDirectory = Option(args, "--rows");
    var mapping = LoadMapping(Option(args, "--mapping"));
    var output = Option(args, "--out");
    var key = Environment.GetEnvironmentVariable(Option(args, "--key-env", "QTS_RECONCILIATION_KEY"));
    var connection = Environment.GetEnvironmentVariable("QTS_POSTGRES_CONNECTION")
        ?? throw new InvalidOperationException("QTS_POSTGRES_CONNECTION is not set; the tool never carries a credential.");

    var source = new SnapshotSource(snapshotDirectory);
    var manifest = source.ReadManifest();
    var entries = new List<ReconciliationEntry>();
    var exceptions = new List<string>();

    var options = new DbContextOptionsBuilder<BillingDbContext>()
        .UseNpgsql(connection)
        .Options;

    await using var db = new BillingDbContext(options);

    foreach (var collection in mapping.Collections)
    {
        var file = manifest.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.Collection, collection.Collection, StringComparison.OrdinalIgnoreCase));

        var sourceCount = file?.Documents ?? 0;
        var rowsPath = Path.Combine(rowsDirectory, $"{collection.TargetTable}.ndjson");
        var transformed = File.Exists(rowsPath) ? (await File.ReadAllLinesAsync(rowsPath).ConfigureAwait(false)).Count(line => !string.IsNullOrWhiteSpace(line)) : 0;

        var quarantinePath = Path.Combine(rowsDirectory, $"{collection.TargetTable}.quarantine.ndjson");
        var quarantine = new List<QuarantineRecord>();

        if (File.Exists(quarantinePath))
        {
            foreach (var line in await File.ReadAllLinesAsync(quarantinePath).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    quarantine.Add(JsonSerializer.Deserialize<QuarantineRecord>(line)!);
                }
            }
        }

        // A legacy value without a counterpart is an exception that needs a decision - it blocks the
        // go-live until it is resolved (spec §3.4/§6.1 step 7).
        var reconciliationPath = Path.Combine(rowsDirectory, $"{collection.TargetTable}.reconciliation.ndjson");

        if (File.Exists(reconciliationPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(reconciliationPath).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var record = JsonSerializer.Deserialize<QuarantineRecord>(line)!;
                    exceptions.Add($"{record.Collection}/{record.SourceId}: {record.Reason} - {record.Detail}");
                }
            }
        }

        // The imported count is the number of rows this import owns (the ones in the identifier map),
        // not the size of the target table.
        var collectionName = collection.Collection.Replace("\"", "\"\"");
        var imported = await db.Database
            .SqlQueryRaw<int>($"""
                SELECT COUNT(*) AS "Value"
                FROM {collection.TargetTable} t
                WHERE t.id IN (SELECT target_id FROM legacy_identifier_map WHERE source_collection = '{collectionName}');
                """)
            .ToListAsync()
            .ConfigureAwait(false);

        var reasons = quarantine
            .GroupBy(record => record.Reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var record in quarantine)
        {
            exceptions.Add($"{record.Collection}/{record.SourceId}: {record.Reason} - {record.Detail}");
        }

        entries.Add(new ReconciliationEntry(
            collection.Collection,
            collection.TargetTable,
            sourceCount,
            transformed,
            imported.Sum(),
            quarantine.Count,
            reasons,
            []));
    }

    var (body, hash) = Reconciliation.BuildBody(mapping.SourceEnvironment, mapping.TransformationVersion, DateTimeOffset.UtcNow, entries, exceptions);
    var signature = Reconciliation.Sign(body, key);

    var report = new JsonObject
    {
        ["sourceEnvironment"] = mapping.SourceEnvironment,
        ["transformationVersion"] = mapping.TransformationVersion,
        ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["entries"] = JsonSerializer.SerializeToNode(entries),
        ["exceptions"] = JsonSerializer.SerializeToNode(exceptions),
        ["reportSha256"] = hash,
        ["signature"] = signature,
    };

    await File.WriteAllTextAsync(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

    Console.WriteLine($"reconciliation: {entries.Count} collection(s), {exceptions.Count} exception(s), hash {hash}, signature {signature}");
    Console.WriteLine($"report written to {output}");
    return exceptions.Count == 0 ? 0 : 1;
}

static MigrationMapping LoadMapping(string path)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    var mapping = JsonSerializer.Deserialize<MigrationMapping>(File.ReadAllText(path), options)
        ?? throw new InvalidOperationException($"The mapping '{path}' is empty.");

    if (mapping.Collections.Count == 0)
    {
        throw new InvalidOperationException($"The mapping '{path}' has no collections.");
    }

    return mapping;
}
