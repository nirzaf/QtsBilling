using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QtsBilling.Migration;

/// <summary>The per-collection comparison between the snapshot, the transform and the database.</summary>
public sealed record ReconciliationEntry(
    string Collection,
    string TargetTable,
    long SourceCount,
    int TransformedCount,
    int ImportedCount,
    int QuarantinedCount,
    IReadOnlyDictionary<string, int> QuarantineReasons,
    IReadOnlyList<TotalComparison> Totals);

/// <summary>One aggregated total: the snapshot against the database.</summary>
public sealed record TotalComparison(
    string Column,
    decimal SourceTotal,
    decimal TargetTotal,
    decimal Tolerance,
    bool Matches);

/// <summary>
/// The signed reconciliation report (spec §6.1, step 7 and QB-25). It states, per collection, the
/// source count, the imported count and every quarantined record with its reason, compares the money and
/// stock totals within the documented tolerance, and is signed so that it cannot be edited unnoticed.
/// </summary>
public sealed record ReconciliationReport(
    string SourceEnvironment,
    int TransformationVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ReconciliationEntry> Entries,
    IReadOnlyList<string> Exceptions,
    string ReportSha256,
    string Signature);

public static class Reconciliation
{
    /// <summary>The documented precision policy: money is compared at 4 decimal places.</summary>
    public const decimal MoneyTolerance = 0.0001m;

    /// <summary>Builds the report body (without the signature) and its hash.</summary>
    public static (JsonObject Body, string Sha256) BuildBody(
        string sourceEnvironment,
        int transformationVersion,
        DateTimeOffset generatedAtUtc,
        IReadOnlyList<ReconciliationEntry> entries,
        IReadOnlyList<string> exceptions)
    {
        var body = new JsonObject
        {
            ["sourceEnvironment"] = sourceEnvironment,
            ["transformationVersion"] = transformationVersion,
            ["generatedAtUtc"] = generatedAtUtc.ToString("O"),
            ["entries"] = JsonSerializer.SerializeToNode(entries),
            ["exceptions"] = JsonSerializer.SerializeToNode(exceptions),
        };

        var canonical = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return (body, hash);
    }

    /// <summary>
    /// Signs the report body: an HMAC-SHA256 over the canonical body with the key from the environment.
    /// Without the key the report is produced unsigned and says so - it is never silently "signed".
    /// </summary>
    public static string Sign(JsonObject body, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "unsigned";
        }

        var canonical = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(signature);
    }

    /// <summary>Compares two totals within the documented tolerance.</summary>
    public static TotalComparison Compare(string column, decimal source, decimal target, decimal tolerance)
        => new(column, source, target, tolerance, Math.Abs(source - target) <= tolerance);
}
