namespace QtsBilling.Persistence.Postgres.Entities;

public enum SalesQuoteStatus
{
    Draft,
    Sent,
    Accepted,
    Rejected,
    Expired,
    Cancelled,
}

public enum SalesOrderStatus
{
    Draft,
    Confirmed,
    PartiallyFulfilled,
    Fulfilled,
    Closed,
}

/// <summary>A business-scoped document counter separated by location, document kind and year.</summary>
public sealed class SalesDocumentSequence : TenantEntity
{
    public Guid LocationId { get; set; }

    public string DocumentKind { get; set; } = string.Empty;

    public int BusinessYear { get; set; }

    public int NextNumber { get; set; } = 1;

    public string Prefix { get; set; } = string.Empty;

    public int Padding { get; set; } = 5;
}

/// <summary>Quote root. Commercial values are snapshots; sending creates an immutable revision.</summary>
public sealed class SalesQuote : TenantEntity
{
    public Guid LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public string QuoteNumber { get; set; } = string.Empty;

    public SalesQuoteStatus Status { get; set; } = SalesQuoteStatus.Draft;

    public DateOnly QuoteDate { get; set; }

    public DateOnly ExpiryDate { get; set; }

    public DateOnly? PromisedDeliveryDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal RoundingAmount { get; set; }

    public int CurrentRevision { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? SentByUserId { get; set; }

    public DateTimeOffset? SentAtUtc { get; set; }

    public Guid? AcceptedByUserId { get; set; }

    public DateTimeOffset? AcceptedAtUtc { get; set; }

    public string? Note { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Mutable quote lines are editable only while their parent remains a draft.</summary>
public sealed class SalesQuoteLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid QuoteId { get; set; }

    public Guid LineKey { get; set; } = Guid.CreateVersion7();

    public int LineNo { get; set; }

    public Guid? VariantId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal RevenueBaseAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LinePayableAmount { get; set; }

    public string TaxMode { get; set; } = "Exclusive";

    public string PricingPolicyVersion { get; set; } = "catalog-v1";
}

/// <summary>Immutable commercial snapshot created each time a quote is sent or amended.</summary>
public sealed class SalesQuoteRevision : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid QuoteId { get; set; }

    public int RevisionNumber { get; set; }

    public DateOnly QuoteDate { get; set; }

    public DateOnly ExpiryDate { get; set; }

    public DateOnly? PromisedDeliveryDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal RoundingAmount { get; set; }

    public string? Note { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid ChangedByUserId { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>Immutable quote line snapshot for one sent revision.</summary>
public sealed class SalesQuoteRevisionLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid QuoteRevisionId { get; set; }

    public Guid LineKey { get; set; }

    public int LineNo { get; set; }

    public Guid? VariantId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal RevenueBaseAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LinePayableAmount { get; set; }

    public string TaxMode { get; set; } = "Exclusive";

    public string PricingPolicyVersion { get; set; } = "catalog-v1";
}

/// <summary>Confirmed order commitment. Confirmation is non-financial and does not move stock.</summary>
public sealed class SalesOrder : TenantEntity
{
    public Guid LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? SourceQuoteId { get; set; }

    public Guid? SourceQuoteRevisionId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;

    public DateOnly OrderDate { get; set; }

    public DateOnly? PromisedDeliveryDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal RoundingAmount { get; set; }

    public int CurrentRevision { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? ConfirmedByUserId { get; set; }

    public DateTimeOffset? ConfirmedAtUtc { get; set; }

    public DateTimeOffset? RemainderCancelledAtUtc { get; set; }

    public Guid? RemainderCancelledByUserId { get; set; }

    public string? RemainderCancellationReason { get; set; }

    public string? Note { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Current fulfilment lines; amendment snapshots preserve every prior commercial revision.</summary>
public sealed class SalesOrderLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid OrderId { get; set; }

    public Guid LineKey { get; set; } = Guid.CreateVersion7();

    public int LineNo { get; set; }

    public Guid? VariantId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal QuantityOrdered { get; set; }

    public decimal QuantityFulfilled { get; set; }

    public decimal QuantityCancelled { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal RevenueBaseAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LinePayableAmount { get; set; }

    public string TaxMode { get; set; } = "Exclusive";

    public string PricingPolicyVersion { get; set; } = "catalog-v1";

    public Guid? SourceQuoteRevisionLineId { get; set; }
}

/// <summary>Immutable order revision including actor and amendment reason.</summary>
public sealed class SalesOrderRevision : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid OrderId { get; set; }

    public int RevisionNumber { get; set; }

    public DateOnly OrderDate { get; set; }

    public DateOnly? PromisedDeliveryDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal RoundingAmount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid ChangedByUserId { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>Immutable order line snapshot for one confirmed or amended revision.</summary>
public sealed class SalesOrderRevisionLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid OrderRevisionId { get; set; }

    public Guid LineKey { get; set; }

    public int LineNo { get; set; }

    public Guid? VariantId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal QuantityOrdered { get; set; }

    public decimal QuantityFulfilled { get; set; }

    public decimal QuantityCancelled { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal RevenueBaseAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LinePayableAmount { get; set; }

    public string TaxMode { get; set; } = "Exclusive";

    public string PricingPolicyVersion { get; set; } = "catalog-v1";
}
