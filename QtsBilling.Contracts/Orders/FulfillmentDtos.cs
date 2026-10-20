namespace QtsBilling.Contracts.Orders;

public sealed record ReserveSalesOrderRequest(long ExpectedOrderVersion, DateTimeOffset ExpiresAtUtc);

public sealed record ReleaseSalesOrderReservationRequest(long ExpectedOrderVersion, string Reason);

public sealed record DeliverSalesOrderLineRequest(Guid OrderLineId, decimal Quantity);

public sealed record DeliverSalesOrderRequest(
    long ExpectedOrderVersion,
    DateOnly DeliveryDate,
    string? Note,
    IReadOnlyList<DeliverSalesOrderLineRequest> Lines);

public sealed record InventoryReservationLineDto(
    Guid OrderLineId,
    Guid? VariantId,
    decimal QuantityReserved,
    decimal QuantityConsumed,
    decimal QuantityReleased,
    decimal QuantityRemaining);

public sealed record InventoryReservationDto(
    Guid Id,
    Guid OrderId,
    string Status,
    DateTimeOffset ExpiresAtUtc,
    long Version,
    IReadOnlyList<InventoryReservationLineDto> Lines);

public sealed record SalesOrderDeliveryLineDto(
    Guid Id,
    Guid OrderLineId,
    Guid? VariantId,
    decimal Quantity,
    decimal UnitCost,
    decimal CostValue,
    bool TrackStockSnapshot,
    decimal QuantityInvoiced,
    decimal CostValueInvoiced);

public sealed record SalesOrderDeliveryDto(
    Guid Id,
    Guid OrderId,
    string DeliveryNumber,
    DateOnly DeliveryDate,
    decimal TotalQuantity,
    decimal CostValue,
    string JournalEntryNumber,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SalesOrderDeliveryLineDto> Lines);

public sealed record CreateDeliveryInvoiceRequest(
    DateOnly IssueDate,
    DateOnly? DueDate,
    string? Note,
    IReadOnlyList<CreateDeliveryInvoiceLineRequest> Lines);

public sealed record CreateDeliveryInvoiceLineRequest(Guid DeliveryLineId, decimal Quantity);
