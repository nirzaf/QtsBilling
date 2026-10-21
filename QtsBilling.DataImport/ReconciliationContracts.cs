using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QtsBilling.DataImport;

/// <summary>
/// The canonical reconciliation report (specification task 20). The body is built once with one
/// timestamp; the digest and signature are computed over exactly those canonical bytes. Comparisons
/// are per resource, identity, aggregate, party, account and control total, not just row counts.
/// </summary>
public sealed record ReconciliationReportBody
{
    public const string CurrentSchema = "qts-native-reconciliation-v1";

    [JsonPropertyName("reportSchema")]
    public string ReportSchema { get; init; } = CurrentSchema;

    [JsonPropertyName("toolSha256")]
    public string ToolSha256 { get; init; } = string.Empty;

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; init; } = string.Empty;

    [JsonPropertyName("targetSchema")]
    public string TargetSchema { get; init; } = string.Empty;

    [JsonPropertyName("schemaHead")]
    public string? SchemaHead { get; init; }

    [JsonPropertyName("batchId")]
    public Guid BatchId { get; init; }

    [JsonPropertyName("targetBusinessId")]
    public Guid TargetBusinessId { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("importCommitted")]
    public bool ImportCommitted { get; init; }

    [JsonPropertyName("resources")]
    public IReadOnlyList<ReconciliationResourceComparison> Resources { get; init; } = [];

    [JsonPropertyName("aggregates")]
    public IReadOnlyList<ReconciliationAggregateComparison> Aggregates { get; init; } = [];

    [JsonPropertyName("parties")]
    public IReadOnlyList<ReconciliationPartyComparison> Parties { get; init; } = [];

    [JsonPropertyName("ledger")]
    public IReadOnlyList<ReconciliationLedgerComparison> Ledger { get; init; } = [];

    [JsonPropertyName("controls")]
    public IReadOnlyList<ReconciliationControlComparison> Controls { get; init; } = [];

    [JsonPropertyName("exceptions")]
    public IReadOnlyList<ReconciliationException> Exceptions { get; init; } = [];

    public byte[] CanonicalBytes() => ReconciliationCanonical.Body(this);
}

/// <summary>
/// One resource comparison. <see cref="ExpectedIdentitySha256"/> covers the exact accepted package
/// rows and their payload fingerprints; <see cref="ActualIdentitySha256"/> covers the immutable
/// source links. A count-only match cannot pass.
/// </summary>
public sealed record ReconciliationResourceComparison
{
    [JsonPropertyName("resource")]
    public string Resource { get; init; } = string.Empty;

    [JsonPropertyName("sourceCount")]
    public long SourceCount { get; init; }

    [JsonPropertyName("acceptedCount")]
    public long AcceptedCount { get; init; }

    [JsonPropertyName("quarantinedCount")]
    public long QuarantinedCount { get; init; }

    [JsonPropertyName("persistedCount")]
    public long PersistedCount { get; init; }

    [JsonPropertyName("unexpectedCount")]
    public long UnexpectedCount { get; init; }

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; init; } = string.Empty;

    [JsonPropertyName("expectedIdentitySha256")]
    public string ExpectedIdentitySha256 { get; init; } = string.Empty;

    [JsonPropertyName("actualIdentitySha256")]
    public string ActualIdentitySha256 { get; init; } = string.Empty;

    [JsonPropertyName("expectedBusinessId")]
    public Guid ExpectedBusinessId { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<ReconciliationTargetReference> Targets { get; init; } = [];
}

public sealed record ReconciliationTargetReference
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("targetId")]
    public Guid TargetId { get; init; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; init; }

    [JsonPropertyName("exists")]
    public bool Exists { get; init; }
}

/// <summary>A quantity/value comparison for inventory, a party or another persisted aggregate.</summary>
public sealed record ReconciliationAggregateComparison
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("partyId")]
    public Guid? PartyId { get; init; }

    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; init; }

    [JsonPropertyName("locationId")]
    public Guid? LocationId { get; init; }

    [JsonPropertyName("expectedBusinessId")]
    public Guid ExpectedBusinessId { get; init; }

    [JsonPropertyName("actualBusinessId")]
    public Guid ActualBusinessId { get; init; }

    [JsonPropertyName("expectedQuantity")]
    public decimal ExpectedQuantity { get; init; }

    [JsonPropertyName("actualQuantity")]
    public decimal ActualQuantity { get; init; }

    [JsonPropertyName("expectedValue")]
    public decimal ExpectedValue { get; init; }

    [JsonPropertyName("actualValue")]
    public decimal ActualValue { get; init; }
}

/// <summary>An AR or AP detail comparison for one customer/supplier.</summary>
public sealed record ReconciliationPartyComparison
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("partyId")]
    public Guid PartyId { get; init; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; init; }

    [JsonPropertyName("expectedAmount")]
    public decimal ExpectedAmount { get; init; }

    [JsonPropertyName("actualAmount")]
    public decimal ActualAmount { get; init; }
}

/// <summary>A general-ledger account comparison for the opening journal lines.</summary>
public sealed record ReconciliationLedgerComparison
{
    [JsonPropertyName("accountCode")]
    public string AccountCode { get; init; } = string.Empty;

    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; init; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; init; }

    [JsonPropertyName("expectedDebit")]
    public decimal ExpectedDebit { get; init; }

    [JsonPropertyName("expectedCredit")]
    public decimal ExpectedCredit { get; init; }

    [JsonPropertyName("actualDebit")]
    public decimal ActualDebit { get; init; }

    [JsonPropertyName("actualCredit")]
    public decimal ActualCredit { get; init; }
}

/// <summary>
/// A subledger-to-control-account comparison. Inventory detail must equal the inventory control,
/// AR open items must equal the AR control and AP open items must equal the AP control.
/// </summary>
public sealed record ReconciliationControlComparison
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; init; }

    [JsonPropertyName("detailAmount")]
    public decimal DetailAmount { get; init; }

    [JsonPropertyName("ledgerAmount")]
    public decimal LedgerAmount { get; init; }
}

/// <summary>
/// An immutable exception identity. Financial exceptions can never be approved away; only
/// enumerated non-financial exceptions can be covered by an independently signed approval set.
/// </summary>
public sealed record ReconciliationException
{
    [JsonPropertyName("exceptionId")]
    public string ExceptionId { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("financial")]
    public bool Financial { get; init; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; init; }
}

/// <summary>The signed report envelope. The body digest and signature cover the canonical body only.</summary>
public sealed record ReconciliationReportDocument(
    ReconciliationReportBody Body,
    string BodySha256,
    NativeManifestApproval? Approval);

/// <summary>
/// An owner-controlled approval of specific, non-financial exception identities. It binds the exact
/// report digest and cannot cover an exception that was added after the report was signed.
/// </summary>
public sealed record ReconciliationApprovalSetBody
{
    [JsonPropertyName("reportSha256")]
    public string ReportSha256 { get; init; } = string.Empty;

    [JsonPropertyName("approvals")]
    public IReadOnlyList<ReconciliationExceptionApproval> Approvals { get; init; } = [];

    public byte[] CanonicalBytes() => ReconciliationCanonical.ApprovalSet(this);
}

public sealed record ReconciliationExceptionApproval
{
    [JsonPropertyName("exceptionId")]
    public string ExceptionId { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("approver")]
    public string Approver { get; init; } = string.Empty;

    [JsonPropertyName("approvedAtUtc")]
    public DateTimeOffset ApprovedAtUtc { get; init; }
}

public sealed record ReconciliationApprovalSetDocument(
    ReconciliationApprovalSetBody Body,
    string BodySha256,
    NativeManifestApproval? Approval);

public sealed record ReconciliationVerification(
    bool Authenticated,
    bool Passed,
    bool CanAdmitProduction,
    string? Failure,
    IReadOnlyList<string> Findings)
{
    public static ReconciliationVerification Reject(string failure, params string[] findings) =>
        new(false, false, false, failure, findings);
}

/// <summary>
/// Deterministic canonical JSON. Property order is fixed and lists are sorted by their immutable
/// identity, so an independent verifier reproduces the exact signed bytes.
/// </summary>
public static class ReconciliationCanonical
{
    public static byte[] Body(ReconciliationReportBody body)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("reportSchema", body.ReportSchema);
            writer.WriteString("toolSha256", body.ToolSha256);
            writer.WriteString("manifestSha256", body.ManifestSha256);
            writer.WriteString("targetSchema", body.TargetSchema);
            WriteNullableString(writer, "schemaHead", body.SchemaHead);
            writer.WriteString("batchId", body.BatchId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("targetBusinessId", body.TargetBusinessId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString(
                "generatedAtUtc",
                body.GeneratedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteBoolean("importCommitted", body.ImportCommitted);

            writer.WriteStartArray("resources");

            foreach (var resource in body.Resources.OrderBy(item => item.Resource, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("resource", resource.Resource);
                writer.WriteNumber("sourceCount", resource.SourceCount);
                writer.WriteNumber("acceptedCount", resource.AcceptedCount);
                writer.WriteNumber("quarantinedCount", resource.QuarantinedCount);
                writer.WriteNumber("persistedCount", resource.PersistedCount);
                writer.WriteNumber("unexpectedCount", resource.UnexpectedCount);
                writer.WriteString("sourceSha256", resource.SourceSha256);
                writer.WriteString("expectedIdentitySha256", resource.ExpectedIdentitySha256);
                writer.WriteString("actualIdentitySha256", resource.ActualIdentitySha256);
                writer.WriteString(
                    "expectedBusinessId",
                    resource.ExpectedBusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteStartArray("targets");

                foreach (var target in resource.Targets.OrderBy(item => item.ExternalId, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("externalId", target.ExternalId);
                    writer.WriteString("targetId", target.TargetId.ToString("D", CultureInfo.InvariantCulture));
                    writer.WriteString("businessId", target.BusinessId.ToString("D", CultureInfo.InvariantCulture));
                    writer.WriteBoolean("exists", target.Exists);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("aggregates");

            foreach (var aggregate in body.Aggregates
                         .OrderBy(item => item.Kind, StringComparer.Ordinal)
                         .ThenBy(item => item.Name, StringComparer.Ordinal)
                         .ThenBy(item => item.PartyId)
                         .ThenBy(item => item.LocationId))
            {
                writer.WriteStartObject();
                writer.WriteString("name", aggregate.Name);
                writer.WriteString("kind", aggregate.Kind);
                WriteNullableGuid(writer, "partyId", aggregate.PartyId);
                WriteNullableGuid(writer, "accountId", aggregate.AccountId);
                WriteNullableGuid(writer, "locationId", aggregate.LocationId);
                writer.WriteString(
                    "expectedBusinessId",
                    aggregate.ExpectedBusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteString(
                    "actualBusinessId",
                    aggregate.ActualBusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteNumber("expectedQuantity", aggregate.ExpectedQuantity);
                writer.WriteNumber("actualQuantity", aggregate.ActualQuantity);
                writer.WriteNumber("expectedValue", aggregate.ExpectedValue);
                writer.WriteNumber("actualValue", aggregate.ActualValue);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("parties");

            foreach (var party in body.Parties
                         .OrderBy(item => item.Kind, StringComparer.Ordinal)
                         .ThenBy(item => item.ExternalId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", party.Kind);
                writer.WriteString("externalId", party.ExternalId);
                writer.WriteString("partyId", party.PartyId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteString("businessId", party.BusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteNumber("expectedAmount", party.ExpectedAmount);
                writer.WriteNumber("actualAmount", party.ActualAmount);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("ledger");

            foreach (var line in body.Ledger.OrderBy(item => item.AccountCode, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("accountCode", line.AccountCode);
                WriteNullableGuid(writer, "accountId", line.AccountId);
                writer.WriteString("businessId", line.BusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteNumber("expectedDebit", line.ExpectedDebit);
                writer.WriteNumber("expectedCredit", line.ExpectedCredit);
                writer.WriteNumber("actualDebit", line.ActualDebit);
                writer.WriteNumber("actualCredit", line.ActualCredit);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("controls");

            foreach (var control in body.Controls.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", control.Name);
                writer.WriteString("businessId", control.BusinessId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteNumber("detailAmount", control.DetailAmount);
                writer.WriteNumber("ledgerAmount", control.LedgerAmount);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("exceptions");

            foreach (var exception in body.Exceptions.OrderBy(item => item.ExceptionId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("exceptionId", exception.ExceptionId);
                writer.WriteString("scope", exception.Scope);
                writer.WriteString("reason", exception.Reason);
                writer.WriteBoolean("financial", exception.Financial);
                WriteNullableString(writer, "externalId", exception.ExternalId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] ApprovalSet(ReconciliationApprovalSetBody body)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("reportSha256", body.ReportSha256);
            writer.WriteStartArray("approvals");

            foreach (var approval in body.Approvals.OrderBy(item => item.ExceptionId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("exceptionId", approval.ExceptionId);
                writer.WriteString("reason", approval.Reason);
                writer.WriteString("approver", approval.Approver);
                writer.WriteString(
                    "approvedAtUtc",
                    approval.ApprovedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is null || value == Guid.Empty)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Value.ToString("D", CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Report and approval-set signing. The HMAC covers the canonical body bytes.</summary>
public static class ReconciliationSigner
{
    public static ReconciliationReportDocument Sign(
        ReconciliationReportBody body,
        string keyId,
        ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (key.Length < 16)
        {
            throw new ArgumentException("The reconciliation signing key must be at least 16 bytes.", nameof(key));
        }

        var bytes = body.CanonicalBytes();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(key, bytes)).ToLowerInvariant();

        return new ReconciliationReportDocument(
            body,
            digest,
            new NativeManifestApproval
            {
                Algorithm = NativeManifestApproval.HmacSha256,
                KeyId = keyId,
                Signature = signature,
            });
    }

    public static ReconciliationApprovalSetDocument SignApprovals(
        ReconciliationApprovalSetBody body,
        string keyId,
        ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (key.Length < 16)
        {
            throw new ArgumentException("The approval signing key must be at least 16 bytes.", nameof(key));
        }

        var bytes = body.CanonicalBytes();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(key, bytes)).ToLowerInvariant();

        return new ReconciliationApprovalSetDocument(
            body,
            digest,
            new NativeManifestApproval
            {
                Algorithm = NativeManifestApproval.HmacSha256,
                KeyId = keyId,
                Signature = signature,
            });
    }
}

/// <summary>Stable hashes used by the report for accepted package identities and source links.</summary>
public static class ReconciliationIdentityHash
{
    public static string Of(IEnumerable<(string ExternalId, string Fingerprint)> rows)
    {
        var canonical = string.Join(
            "\n",
            rows.OrderBy(row => row.ExternalId, StringComparer.Ordinal)
                .Select(row => $"{row.ExternalId}\0{row.Fingerprint}"));

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
