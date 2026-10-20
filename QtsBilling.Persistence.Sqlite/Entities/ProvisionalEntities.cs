namespace QtsBilling.Persistence.Sqlite.Entities;

/// <summary>Local lifecycle of a provisional capture. Never relabel Posted without a persisted receipt.</summary>
public enum SaleDraftState
{
    Pending = 0,
    Sending = 1,
    Posted = 2,
    Rejected = 3,
}

/// <summary>
/// A locally durable, provisional cash sale. The row and its lines are never deleted automatically on
/// logout, upgrade or network failure; only a terminal review/operator decision may remove them.
/// </summary>
public sealed class LocalSaleDraft
{
    public Guid OperationId { get; set; }

    /// <summary>Device-scoped provisional reference. It is never presented as a final sale/invoice number.</summary>
    public string ProvisionalReference { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid RegisterId { get; set; }

    /// <summary>Cash drawer session that authorized this provisional capture.</summary>
    public Guid? RegisterSessionId { get; set; }

    /// <summary>Optional whole-payload snapshot; normalized lines are always written to <see cref="Lines"/>.</summary>
    public string? LinesJson { get; set; }

    /// <summary>First cutover allows cash only. Enforced by a database check constraint.</summary>
    public string PaymentMethod { get; set; } = "cash";

    /// <summary>Cash collected, scale 10,000.</summary>
    public decimal Amount { get; set; }

    public SaleDraftState State { get; set; } = SaleDraftState.Pending;

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<LocalSaleLine> Lines { get; set; } = [];
}

/// <summary>Immutable fixed-price snapshot of a provisional sale line.</summary>
public sealed class LocalSaleLine
{
    public Guid OperationId { get; set; }

    public int Ordinal { get; set; }

    public Guid VariantId { get; set; }

    /// <summary>Scale 1,000,000.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Scale 10,000.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Scale 10,000.</summary>
    public decimal Tax { get; set; }

    /// <summary>Scale 10,000.</summary>
    public decimal Discount { get; set; }

    public long ExpectedCatalogRevision { get; set; }

    public LocalSaleDraft? Draft { get; set; }
}

/// <summary>Provisional cash collected for an operation; retained for review even when rejected.</summary>
public sealed class LocalCashMovement
{
    public Guid OperationId { get; set; }

    public Guid RegisterId { get; set; }

    /// <summary>Cash drawer session that authorized this provisional movement.</summary>
    public Guid? RegisterSessionId { get; set; }

    /// <summary>Scale 10,000.</summary>
    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public SaleDraftState State { get; set; } = SaleDraftState.Pending;
}
