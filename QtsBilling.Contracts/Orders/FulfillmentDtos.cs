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
    string? JournalEntryNumber,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SalesOrderDeliveryLineDto> Lines);

public sealed record CreateDeliveryInvoiceRequest(
    DateOnly IssueDate,
    DateOnly? DueDate,
    string? Note,
    IReadOnlyList<CreateDeliveryInvoiceLineRequest> Lines);

public sealed record CreateDeliveryInvoiceLineRequest(Guid DeliveryLineId, decimal Quantity);

/// <summary>
/// One order line as the fulfillment workbench must see it (specification task 23, recipe step 1): what was
/// committed, what a reservation still holds, what has physically gone out, what was released or cancelled, and
/// what stock the branch actually has. Reservation, dispatch and financial recognition are three separate
/// quantities on purpose - adding them together would hide which one has moved.
/// </summary>
public sealed record FulfillmentLineDto(
    Guid OrderLineId,
    Guid LineKey,
    int LineNo,
    string Description,
    string? Sku,
    Guid? VariantId,
    bool TrackStock,
    decimal Ordered,
    decimal Delivered,
    decimal Cancelled,
    decimal Outstanding,
    decimal ReservedOutstanding,
    decimal Released,
    decimal Invoiced,
    decimal? OnHand,
    bool Deliverable,
    string? BlockReason);

/// <summary>
/// The whole fulfillment position of one sales order: the commitment, its active reservation if any, the per-line
/// quantities above, and the deliveries already made with how much of each has been invoiced (task 23, recipe
/// steps 1 and 3). Every number is the server's; a screen never recomputes an outstanding quantity from a memory.
/// </summary>
public sealed record OrderFulfillmentDto(
    Guid OrderId,
    string OrderNumber,
    string Status,
    long Version,
    Guid LocationId,
    string LocationName,
    string? CustomerName,
    string Currency,
    decimal TotalAmount,
    DateOnly OrderDate,
    DateOnly? PromisedDeliveryDate,
    string? RemainderCancellationReason,
    InventoryReservationDto? Reservation,
    IReadOnlyList<FulfillmentLineDto> Lines,
    IReadOnlyList<SalesOrderDeliveryDto> Deliveries);
