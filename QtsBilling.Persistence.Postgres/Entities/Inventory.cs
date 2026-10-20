namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Authoritative on-hand quantity for a variant at a location. Only inventory movements may change it
/// (spec §3.5, F-02/F-12). The database enforces <c>on_hand &gt;= 0</c> plus a movement-linked check.
/// </summary>
public sealed class InventoryBalance : TenantEntity
{
    public Guid LocationId { get; set; }

    public Guid VariantId { get; set; }

    public decimal OnHand { get; set; }

    /// <summary>
    /// The authoritative value of the stock on hand (four decimals). The value is derived from the
    /// immutable movements, exactly like the quantity (specification D06).
    /// </summary>
    public decimal ValueOnHand { get; set; }

    /// <summary>
    /// The moving weighted average cost (eight decimals). It is informative: the movement value is the
    /// authority, and this is the cost a later issue consumes.
    /// </summary>
    public decimal AverageUnitCost { get; set; }

    public DateTimeOffset LastMovementAtUtc { get; set; }
}

/// <summary>
/// Append-only stock movement. Immutable after insert; corrections are new movements (spec §3.5).
/// The unique key <c>(business, reference_type, reference_id, movement_type)</c> makes re-posting an
/// already-applied business effect impossible (exactly-once effect on top of at-least-once delivery).
/// </summary>
public sealed class InventoryMovement : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public Guid VariantId { get; set; }

    public MovementType MovementType { get; set; }

    /// <summary>Signed quantity change: negative for outflows, positive for inflows.</summary>
    public decimal QuantityDelta { get; set; }

    public decimal UnitCost { get; set; }

    /// <summary>
    /// The authoritative value of this movement (four decimals): positive when the stock grows, negative
    /// when it is consumed. The cost of goods sold is this value, never the catalog price
    /// (specification D06, finding F15).
    /// </summary>
    public decimal ValueDelta { get; set; }

    /// <summary>Originating posting unit (e.g. <c>sale_line</c>); part of the idempotency key.</summary>
    public string ReferenceType { get; set; } = string.Empty;

    public Guid ReferenceId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? Memo { get; set; }
}

/// <summary>Inter-location stock transfer with dispatch and receipt posting checkpoints.</summary>
public sealed class StockTransfer : TenantEntity
{
    public string TransferNumber { get; set; } = string.Empty;

    public Guid SourceLocationId { get; set; }

    public Guid DestinationLocationId { get; set; }

    public StockTransferStatus Status { get; set; } = StockTransferStatus.Draft;

    public string Currency { get; set; } = "QAR";

    public string? Note { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }

    public DateTimeOffset? DispatchedAtUtc { get; set; }

    public Guid? DispatchedByUserId { get; set; }

    public Guid? DispatchJournalEntryId { get; set; }

    public DateTimeOffset? ReceivedAtUtc { get; set; }

    public Guid? ReceivedByUserId { get; set; }

    public Guid? ReceiveJournalEntryId { get; set; }
}

/// <summary>Transfer line, valued from the source movement at dispatch and reused at receipt.</summary>
public sealed class StockTransferLine : TenantEntity
{
    public Guid StockTransferId { get; set; }

    public int LineNo { get; set; }

    public Guid VariantId { get; set; }

    public decimal Quantity { get; set; }

    public decimal? UnitCostAtDispatch { get; set; }

    public decimal? ValueAtDispatch { get; set; }
}
