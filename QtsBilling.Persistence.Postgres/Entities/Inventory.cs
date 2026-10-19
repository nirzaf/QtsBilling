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

    /// <summary>Originating posting unit (e.g. <c>sale_line</c>); part of the idempotency key.</summary>
    public string ReferenceType { get; set; } = string.Empty;

    public Guid ReferenceId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? Memo { get; set; }
}
