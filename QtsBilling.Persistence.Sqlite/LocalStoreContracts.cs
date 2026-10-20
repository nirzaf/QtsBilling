using System.Security.Cryptography;
using System.Text;
using QtsBilling.Persistence.Sqlite.Entities;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>Shape of a provisional cash sale captured atomically with its outbox row.</summary>
public sealed record ProvisionalSaleLineInput(
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice,
    decimal Tax,
    decimal Discount,
    long ExpectedCatalogRevision);

/// <summary>
/// Input for <see cref="SqliteLocalStore.CaptureProvisionalCashSaleAsync"/>. The caller (MAUI POS) generates
/// the stable GUID v7 operation id and the canonical payload; the store validates, scales and commits the
/// draft, lines, cash movement and outbox row in one SQLite transaction.
/// </summary>
public sealed record ProvisionalCashSaleInput
{
    public required Guid OperationId { get; init; }

    /// <summary>Optional; derived from the operation id when omitted. Never a final invoice number.</summary>
    public string? ProvisionalReference { get; init; }

    public required Guid DeviceId { get; init; }

    public required Guid BusinessId { get; init; }

    public required Guid LocationId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid RegisterId { get; init; }

    public required Guid RegisterSessionId { get; init; }

    /// <summary>Cash collected, scale 10,000.</summary>
    public required decimal Amount { get; init; }

    public required IReadOnlyList<ProvisionalSaleLineInput> Lines { get; init; }

    public string OperationType { get; init; } = "pos.sale.capture.v1";

    public int PayloadVersion { get; init; } = 1;

    public required string CanonicalPayload { get; init; }

    /// <summary>Optional expected fingerprint; when supplied it must match the payload.</summary>
    public string? Sha256Fingerprint { get; init; }
}

/// <summary>Local queue summary for the MAUI status bar/receipt marking.</summary>
public sealed record PendingSummary(int PendingOperations, int RejectedOperations, decimal ProvisionalCashAtRisk);

/// <summary>Counts of rows removed by a cache rebuild. Pending money/outbox tables are never included.</summary>
public sealed record CacheRebuildResult(
    int Products,
    int Customers,
    int Categories,
    int Units,
    int Stock,
    int Tombstones)
{
    public int Total => Products + Customers + Categories + Units + Stock + Tombstones;
}

/// <summary>A locally captured sale together with its fixed-price lines.</summary>
public sealed record LocalSaleDraftWithLines(LocalSaleDraft Draft, IReadOnlyList<LocalSaleLine> Lines);

/// <summary>Canonical SHA-256 fingerprint used for same-key/different-body detection.</summary>
public static class SqlitePayloadHash
{
    public static string Compute(string canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)))
            .ToLowerInvariant();
    }
}
