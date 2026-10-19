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
}
