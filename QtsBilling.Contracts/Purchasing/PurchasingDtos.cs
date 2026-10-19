namespace QtsBilling.Contracts.Purchasing;

/// <summary>Supplier master data (purchasing counterparty).</summary>
public sealed record SupplierDto(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    bool IsActive,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveSupplierRequest(string Name, string? Email, string? Phone, string? Address);

/// <summary>Purchase order lifecycle as seen over the wire (stored as readable text - D-31).</summary>
public enum PurchaseOrderState
{
    Draft,
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled,
}

/// <summary>Ordered line. Stock changes only through receipts, never through the order itself (QB-12).</summary>
public sealed record PurchaseOrderLineRequest(
    Guid VariantId,
    decimal Quantity,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal TaxRate);

public sealed record SavePurchaseOrderRequest(
    Guid SupplierId,
    Guid LocationId,
    DateOnly? OrderDate,
    DateOnly? ExpectedDate,
    string? Note,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string OrderNumber,
    PurchaseOrderState Status,
    Guid SupplierId,
    string? SupplierName,
    Guid LocationId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Note,
    DateTimeOffset CreatedAtUtc);

public sealed record PurchaseOrderLineDto(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string ProductName,
    string? Sku,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal);

public sealed record PurchaseOrderDetailDto(
    PurchaseOrderSummaryDto Order,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

/// <summary>Receipt (goods received note) or return against purchase order lines.</summary>
public sealed record PurchaseReceiptLineRequest(Guid PurchaseOrderLineId, decimal Quantity);

public sealed record PurchaseReceiptRequest(string? Note, IReadOnlyList<PurchaseReceiptLineRequest> Lines);

public sealed record PurchaseReceiptLineDto(
    Guid Id,
    int LineNo,
    Guid PurchaseOrderLineId,
    Guid VariantId,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitCost);

public sealed record PurchaseReceiptDto(
    Guid Id,
    string ReceiptNumber,
    string Kind,
    Guid PurchaseOrderId,
    string OrderNumber,
    Guid LocationId,
    DateTimeOffset ReceivedAtUtc,
    string? Note,
    IReadOnlyList<PurchaseReceiptLineDto> Lines);

public sealed record PurchaseOrderActionRequest(string Reason);
