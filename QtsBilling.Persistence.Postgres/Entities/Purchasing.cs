namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>Purchase order header. Ordered quantities are separate from received quantities (QB-12).</summary>
public sealed class PurchaseOrder : TenantEntity
{
    public Guid SupplierId { get; set; }

    public Guid LocationId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public DateOnly OrderDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Note { get; set; }
}

public sealed class PurchaseOrderLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid PurchaseOrderId { get; set; }

    public int LineNo { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal QuantityOrdered { get; set; }

    public decimal QuantityReceived { get; set; }

    public decimal UnitCost { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }
}

/// <summary>Business expense; posts to the ledger through a controlled service path (QB-12).</summary>
public sealed class Expense : TenantEntity
{
    public Guid? ExpenseCategoryId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? SupplierId { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    public DateOnly OccurredOn { get; set; }

    public PaymentMethod PaidVia { get; set; } = PaymentMethod.Cash;

    public string? Reference { get; set; }

    public Guid? CreatedByUserId { get; set; }
}
