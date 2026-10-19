namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Posted sale (POS). Financial values are historical snapshots; nothing is recomputed from mutable
/// product data (spec §3.1). Posting happens inside one database transaction together with stock
/// movements and journal entries (QB-09).
/// </summary>
public sealed class Sale : TenantEntity
{
    public Guid LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public string SaleNumber { get; set; } = string.Empty;

    public SaleStatus Status { get; set; } = SaleStatus.Posted;

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;

    public string? Note { get; set; }

    public DateTimeOffset PostedAtUtc { get; set; }

    public Guid? PostedByUserId { get; set; }

    public Guid? ReversalOfSaleId { get; set; }

    /// <summary>Client idempotency key of the posting request; unique per business when present.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Canonical fingerprint of the original payload: a changed payload under the same key is rejected (QB-09).</summary>
    public string? PayloadHash { get; set; }
}

/// <summary>Sale line with full product/price/tax snapshot at posting time (spec §3.1).</summary>
public sealed class SaleLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid SaleId { get; set; }

    public int LineNo { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    /// <summary>Cost of goods snapshot used for margin/COGS reporting.</summary>
    public decimal UnitCostSnapshot { get; set; }
}

/// <summary>
/// Payment record. Card PAN/CVV are structurally absent (F-07); card payments may only store a
/// PSP reference and non-sensitive brand/last-four metadata after a verified PSP confirmation.
/// </summary>
public sealed class Payment : TenantEntity
{
    public Guid? SaleId { get; set; }

    public Guid? InvoiceId { get; set; }

    public PaymentMethod Method { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal? CashReceived { get; set; }

    public string? Reference { get; set; }

    public string? PspReference { get; set; }

    public string? CardBrand { get; set; }

    public string? CardLast4 { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Drawer session that captured this payment; null for sales without register tracking (QB-10).</summary>
    public Guid? CashRegisterSessionId { get; set; }
}

/// <summary>Cash drawer session register for a location.</summary>
public sealed class CashRegister : TenantEntity
{
    public Guid LocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>Append-only cash movement; the drawer balance is always derived from these rows.</summary>
public sealed class CashRegisterMovement : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid CashRegisterId { get; set; }

    public CashMovementType MovementType { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    public string? Reason { get; set; }

    public Guid? SaleId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Drawer session the movement belongs to; null only for legacy/uncoupled movements (QB-10).</summary>
    public Guid? CashRegisterSessionId { get; set; }
}

/// <summary>
/// Cash drawer session (open/close, opening float, counted vs expected). Expected cash is always derived
/// from the append-only movements, never stored as mutable truth except at close (QB-10).
/// </summary>
public sealed class CashRegisterSession : TenantEntity
{
    public Guid CashRegisterId { get; set; }

    public string Currency { get; set; } = "QAR";

    public CashRegisterSessionStatus Status { get; set; } = CashRegisterSessionStatus.Open;

    public decimal OpeningFloat { get; set; }

    public Guid OpenedByUserId { get; set; }

    public DateTimeOffset OpenedAtUtc { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public decimal? CountedCash { get; set; }

    public decimal? ExpectedCash { get; set; }

    public decimal? Variance { get; set; }

    public string? ClosingNote { get; set; }
}

/// <summary>
/// Full refund of a posted sale (QB-10): cash returned, stock returned, reversal entry posted. Refunds are
/// immutable facts tied to the original sale; partial refunds/credit notes arrive with QB-11.
/// </summary>
public sealed class Refund : TenantEntity
{
    public Guid SaleId { get; set; }

    public Guid LocationId { get; set; }

    public string RefundNumber { get; set; } = string.Empty;

    public string Currency { get; set; } = "QAR";

    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    public Guid? CashRegisterSessionId { get; set; }

    /// <summary>The posted reversal entry that neutralizes the original sale entry.</summary>
    public Guid? JournalEntryId { get; set; }

    public DateTimeOffset PostedAtUtc { get; set; }

    public Guid PostedByUserId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Refund line mirroring the original sale line snapshot (spec §3.6 refunds).</summary>
public sealed class RefundLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid RefundId { get; set; }

    public Guid SaleLineId { get; set; }

    public int LineNo { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    public decimal UnitCostSnapshot { get; set; }
}
