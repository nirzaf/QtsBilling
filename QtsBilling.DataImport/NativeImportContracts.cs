using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.DataImport;

public sealed record NativeManifestApproval
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = NativeManifestApproval.HmacSha256;

    [JsonPropertyName("keyId")]
    public string KeyId { get; init; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    public const string HmacSha256 = "HMAC-SHA256";
}

public sealed record NativeManifestResource
{
    [JsonPropertyName("resource")]
    public string Resource { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("byteLength")]
    public long ByteLength { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record NativeManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = NativeManifest.CurrentSchemaVersion;

    [JsonPropertyName("mappingVersion")]
    public int MappingVersion { get; init; } = 1;

    [JsonPropertyName("targetSchema")]
    public string TargetSchema { get; init; } = NativeManifest.CurrentTargetSchema;

    [JsonPropertyName("batchId")]
    public Guid BatchId { get; init; }

    [JsonPropertyName("targetBusinessId")]
    public Guid TargetBusinessId { get; init; }

    [JsonPropertyName("effectiveBusinessDate")]
    public DateOnly EffectiveBusinessDate { get; init; }

    [JsonPropertyName("capturedAtUtc")]
    public DateTimeOffset CapturedAtUtc { get; init; }

    [JsonPropertyName("resources")]
    public IReadOnlyList<NativeManifestResource> Resources { get; init; } = [];

    [JsonPropertyName("approval")]
    public NativeManifestApproval? Approval { get; init; }

    public const int CurrentSchemaVersion = 1;

    public const string CurrentTargetSchema = "qts-native-v1";

    public byte[] CanonicalBody()
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteNumber("mappingVersion", MappingVersion);
            writer.WriteString("targetSchema", TargetSchema);
            writer.WriteString("batchId", BatchId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("targetBusinessId", TargetBusinessId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString(
                "effectiveBusinessDate",
                EffectiveBusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString(
                "capturedAtUtc",
                CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartArray("resources");

            foreach (var resource in Resources)
            {
                writer.WriteStartObject();
                writer.WriteString("resource", resource.Resource);
                writer.WriteString("path", resource.Path);
                writer.WriteNumber("count", resource.Count);
                writer.WriteNumber("byteLength", resource.ByteLength);
                writer.WriteString("sha256", resource.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

public static class NativeResources
{
    public const string Customers = "customers";
    public const string Suppliers = "suppliers";
    public const string Categories = "categories";
    public const string Products = "products";
    public const string Variants = "variants";
    public const string Openings = "openings";

    public static readonly IReadOnlyList<string> All =
        [Customers, Suppliers, Categories, Products, Variants, Openings];

    public static bool IsKnown(string resource) => All.Contains(resource, StringComparer.Ordinal);
}

public sealed record NativePackageLimits
{
    public long MaxFileBytes { get; init; } = 32 * 1024 * 1024;

    public long MaxTotalBytes { get; init; } = 128 * 1024 * 1024;

    public int MaxRowsPerResource { get; init; } = 100_000;

    public int MaxLineBytes { get; init; } = 1024 * 1024;

    public int MaxJsonDepth { get; init; } = 16;

    public int MaxResources { get; init; } = 10;

    public int MaxExternalIdLength { get; init; } = 128;

    public static NativePackageLimits Default { get; } = new();
}

public sealed record NativeCustomerRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

public sealed record NativeSupplierRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

public sealed record NativeCategoryRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("parentExternalId")]
    public string? ParentExternalId { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

public sealed record NativeProductRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("sku")]
    public string? Sku { get; init; }

    [JsonPropertyName("categoryExternalId")]
    public string? CategoryExternalId { get; init; }

    [JsonPropertyName("taxType")]
    public string? TaxType { get; init; }

    [JsonPropertyName("trackStock")]
    public bool TrackStock { get; init; } = true;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

public sealed record NativeVariantRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("productExternalId")]
    public required string ProductExternalId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("sku")]
    public string? Sku { get; init; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; init; }

    [JsonPropertyName("sellingPrice")]
    public decimal SellingPrice { get; init; }

    [JsonPropertyName("purchasePrice")]
    public decimal? PurchasePrice { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

public sealed record NativeOpeningLineRow
{
    [JsonPropertyName("accountCode")]
    public required string AccountCode { get; init; }

    [JsonPropertyName("debit")]
    public decimal Debit { get; init; }

    [JsonPropertyName("credit")]
    public decimal Credit { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record NativeStockOpeningRow
{
    [JsonPropertyName("variantExternalId")]
    public required string VariantExternalId { get; init; }

    [JsonPropertyName("locationCode")]
    public required string LocationCode { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("unitCost")]
    public decimal UnitCost { get; init; }
}

public sealed record NativePartyOpeningRow
{
    [JsonPropertyName("partyExternalId")]
    public required string PartyExternalId { get; init; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("reference")]
    public string? Reference { get; init; }
}

public sealed record NativeOpeningRow
{
    [JsonPropertyName("externalId")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("entryDate")]
    public required string EntryDate { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("memo")]
    public string? Memo { get; init; }

    [JsonPropertyName("inventoryAccountCode")]
    public string? InventoryAccountCode { get; init; }

    [JsonPropertyName("receivableAccountCode")]
    public string? ReceivableAccountCode { get; init; }

    [JsonPropertyName("payableAccountCode")]
    public string? PayableAccountCode { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<NativeOpeningLineRow> Lines { get; init; } = [];

    [JsonPropertyName("stock")]
    public IReadOnlyList<NativeStockOpeningRow> Stock { get; init; } = [];

    [JsonPropertyName("receivables")]
    public IReadOnlyList<NativePartyOpeningRow> Receivables { get; init; } = [];

    [JsonPropertyName("payables")]
    public IReadOnlyList<NativePartyOpeningRow> Payables { get; init; } = [];
}

public sealed record NativeRejection(
    string Resource,
    long LineNumber,
    string? ExternalId,
    string Reason,
    string? Field);

public sealed record NativeResourceSummary(
    string Resource,
    string Path,
    long Count,
    long ByteLength,
    string Sha256);

public sealed record NativePackagePreview(
    Guid BatchId,
    Guid TargetBusinessId,
    DateOnly EffectiveBusinessDate,
    string ApprovalKeyId,
    IReadOnlyList<NativeResourceSummary> Resources)
{
    public long TotalRows => Resources.Sum(resource => resource.Count);
}

public sealed record NativeValidationReport(
    string PackageRoot,
    Guid? BatchId,
    Guid? TargetBusinessId,
    DateOnly? EffectiveBusinessDate,
    string? ApprovalKeyId,
    IReadOnlyList<NativeResourceSummary> Resources,
    IReadOnlyList<NativeRejection> Rejections)
{
    public bool IsValid => Rejections.Count == 0;
}

public sealed record ResourceSnapshot(
    string Resource,
    string RelativePath,
    string FullPath,
    byte[] Bytes,
    string Sha256);

public sealed class ValidatedPackage
{
    public required NativeManifest Manifest { get; init; }

    public required string Root { get; init; }

    public required string ManifestSha256 { get; init; }

    public required IReadOnlyDictionary<string, ResourceSnapshot> Resources { get; init; }

    public required IReadOnlyList<NativeCustomerRow> Customers { get; init; }

    public required IReadOnlyList<NativeSupplierRow> Suppliers { get; init; }

    public required IReadOnlyList<NativeCategoryRow> Categories { get; init; }

    public required IReadOnlyList<NativeProductRow> Products { get; init; }

    public required IReadOnlyList<NativeVariantRow> Variants { get; init; }

    public required IReadOnlyList<NativeOpeningRow> Openings { get; init; }

    public required IReadOnlyDictionary<string, string> RowFingerprints { get; init; }

    public string Fingerprint(string resource, string externalId) =>
        RowFingerprints.TryGetValue(RowKey(resource, externalId), out var value) ? value : string.Empty;

    public static string RowKey(string resource, string externalId) => resource + "\0" + externalId;
}

public static class NativePackageSigner
{
    public static NativeManifest Sign(NativeManifest manifest, string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (key.Length < 16)
        {
            throw new ArgumentException("The native package approval key must be at least 16 bytes.", nameof(key));
        }

        var signature = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(key, manifest.CanonicalBody())).ToLowerInvariant();

        return manifest with
        {
            Approval = new NativeManifestApproval
            {
                Algorithm = NativeManifestApproval.HmacSha256,
                KeyId = keyId,
                Signature = signature,
            },
        };
    }
}

/// <summary>Caller-approved target of a native import. The tenant comes from the signed manifest; this
/// value must agree with it, so a caller cannot substitute another business.</summary>
public sealed record NativeImportOptions
{
    public required Guid TargetBusinessId { get; init; }

    /// <summary>The acting user recorded on posted entries, or null for an operator import.</summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Test-only fault-injection seam. A production caller leaves this null. It runs after the domain
    /// rows and source links are written but before COMMIT, so a failure proves both sides roll back.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeCommit { get; init; }
}

/// <summary>A bounded, non-sensitive rejected aggregate or row. Values are never echoed.</summary>
public sealed record NativeImportQuarantine(
    string Resource,
    long LineNumber,
    string? ExternalId,
    string Reason,
    string? Field);

public sealed record NativePartyOpeningEvidence(
    OpenItemKind Kind,
    Guid PartyId,
    decimal Amount,
    string Currency,
    Guid OpenItemId,
    string? Reference);

public sealed record NativeOpeningEvidence(
    string ExternalId,
    Guid JournalEntryId,
    decimal InventoryValue,
    decimal ReceivableTotal,
    decimal PayableTotal,
    IReadOnlyList<NativePartyOpeningEvidence> Receivables,
    IReadOnlyList<NativePartyOpeningEvidence> Payables);

public sealed record NativeImportResult(
    Guid BatchId,
    Guid BusinessId,
    bool Committed,
    int ImportedRecords,
    int ReusedRecords,
    IReadOnlyList<NativeImportQuarantine> Quarantined,
    IReadOnlyList<NativeOpeningEvidence> Openings,
    string? Failure)
{
    public static NativeImportResult FailureResult(
        Guid batchId,
        Guid businessId,
        IReadOnlyList<NativeImportQuarantine> quarantined,
        string failure) =>
        new(batchId, businessId, false, 0, 0, quarantined, [], failure);
}

/// <summary>
/// Stable target identity from an approved namespace plus the complete external identity
/// (specification D13/D17). The same identity always derives the same Guid on every run and machine.
/// </summary>
public static class NativeStableIdentity
{
    public const string DatasetNamespace = "native";

    public static Guid Derive(Guid businessId, string kind, string externalIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalIdentity);

        var input = $"qts-native-v1|{businessId:N}|{kind}|{externalIdentity}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);

        // RFC 9562 version 8 (custom) and variant bits, so the value is a valid modern UUID.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
