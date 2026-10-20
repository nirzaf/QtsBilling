namespace QtsBilling.Persistence.Postgres.Entities;

public enum InventoryReservationStatus
{
    Active,
    Fulfilled,
    Released,
    Expired,
}

/// <summary>A business-scoped reservation against a confirmed order.</summary>
public sealed class InventoryReservation : TenantEntity
{
    public Guid OrderId { get; set; }

    public Guid LocationId { get; set; }

    public InventoryReservationStatus Status { get; set; } = InventoryReservationStatus.Active;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public Guid CreatedByUserId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public string? CloseReason { get; set; }
}

/// <summary>Reserved quantities by immutable order line; consumption and release are monotonic.</summary>
public sealed class InventoryReservationLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid ReservationId { get; set; }

    public Guid OrderLineId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal QuantityReserved { get; set; }

    public decimal QuantityConsumed { get; set; }

    public decimal QuantityReleased { get; set; }
}

/// <summary>Immutable proof of one order delivery and its posted inventory valuation.</summary>
public sealed class SalesOrderDelivery : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid OrderId { get; set; }

    public Guid LocationId { get; set; }

    public string DeliveryNumber { get; set; } = string.Empty;

    public DateOnly DeliveryDate { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? JournalEntryId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public string? Note { get; set; }
}

/// <summary>Immutable delivered quantity and the exact stock value removed at dispatch.</summary>
public sealed class SalesOrderDeliveryLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid DeliveryId { get; set; }

    public Guid OrderLineId { get; set; }

    public Guid? ReservationLineId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal CostValue { get; set; }

    public bool TrackStockSnapshot { get; set; }
}

/// <summary>Append-only invoice allocation from a delivered line, including exact cost transfer.</summary>
public sealed class DeliveryInvoiceAllocation : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid DeliveryLineId { get; set; }

    public Guid InvoiceLineId { get; set; }

    public Guid JournalEntryId { get; set; }

    public decimal Quantity { get; set; }

    public decimal CostValue { get; set; }
}
