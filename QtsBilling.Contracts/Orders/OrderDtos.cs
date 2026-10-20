using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Contracts.Orders;

public sealed record QuoteLineRequest(
    Guid? VariantId,
    string Description,
    decimal Quantity,
    decimal? UnitPrice = null,
    decimal DiscountAmount = 0m,
    Guid? TaxRateId = null,
    string? TaxMode = null);

public sealed record CreateQuoteRequest(
    Guid LocationId,
    Guid? CustomerId,
    DateOnly? QuoteDate,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string? Note,
    IReadOnlyList<QuoteLineRequest> Lines);

public sealed record UpdateQuoteRequest(
    long ExpectedVersion,
    Guid? CustomerId,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string? Note,
    IReadOnlyList<QuoteLineRequest> Lines);

public sealed record QuoteCommandRequest(long ExpectedVersion, string? Reason = null);

public sealed record QuoteAmendRequest(
    long ExpectedVersion,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string Reason,
    IReadOnlyList<QuoteLineRequest> Lines);

public sealed record ConvertQuoteRequest(long ExpectedVersion, string? Note = null);

public sealed record SalesOrderLineRequest(
    Guid? VariantId,
    string Description,
    decimal Quantity,
    decimal? UnitPrice = null,
    decimal DiscountAmount = 0m,
    Guid? TaxRateId = null,
    string? TaxMode = null);

public sealed record SalesOrderAmendmentLineRequest(
    Guid? SupersedesLineKey,
    Guid? VariantId,
    string Description,
    decimal QuantityOutstanding,
    decimal? UnitPrice = null,
    decimal DiscountAmount = 0m,
    Guid? TaxRateId = null,
    string? TaxMode = null);

public sealed record CreateSalesOrderRequest(
    Guid LocationId,
    Guid? CustomerId,
    DateOnly? OrderDate,
    DateOnly? PromisedDeliveryDate,
    string? Note,
    IReadOnlyList<SalesOrderLineRequest> Lines);

public sealed record UpdateSalesOrderRequest(
    long ExpectedVersion,
    Guid? CustomerId,
    DateOnly? PromisedDeliveryDate,
    string? Note,
    IReadOnlyList<SalesOrderLineRequest> Lines);

public sealed record ConfirmSalesOrderRequest(long ExpectedVersion, string? Reason = null);

public sealed record AmendSalesOrderRequest(
    long ExpectedVersion,
    DateOnly? PromisedDeliveryDate,
    string Reason,
    IReadOnlyList<SalesOrderAmendmentLineRequest> Lines);

public sealed record CancelOrderRemainderRequest(long ExpectedVersion, string Reason);

public sealed record OrderLineDto(
    Guid Id,
    Guid LineKey,
    int LineNo,
    Guid? VariantId,
    string Description,
    string? SkuSnapshot,
    decimal Quantity,
    decimal? QuantityFulfilled,
    decimal? QuantityCancelled,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal RevenueBaseAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LinePayableAmount,
    string TaxMode,
    string PricingPolicyVersion);

public sealed record QuoteRevisionDto(
    Guid Id,
    int RevisionNumber,
    DateOnly QuoteDate,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal RoundingAmount,
    string? Note,
    string Reason,
    Guid ChangedByUserId,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record SalesQuoteDto(
    Guid Id,
    Guid LocationId,
    Guid? CustomerId,
    string QuoteNumber,
    string Status,
    DateOnly QuoteDate,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal RoundingAmount,
    int CurrentRevision,
    long Version,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    string? Note,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<QuoteRevisionDto> Revisions);

public sealed record SalesQuoteSummaryDto(
    Guid Id,
    Guid LocationId,
    Guid? CustomerId,
    string QuoteNumber,
    string Status,
    DateOnly QuoteDate,
    DateOnly ExpiryDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal TotalAmount,
    int CurrentRevision,
    long Version);

public sealed record SalesOrderRevisionDto(
    Guid Id,
    int RevisionNumber,
    DateOnly OrderDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal RoundingAmount,
    string Reason,
    Guid ChangedByUserId,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record SalesOrderDto(
    Guid Id,
    Guid LocationId,
    Guid? CustomerId,
    Guid? SourceQuoteId,
    Guid? SourceQuoteRevisionId,
    string OrderNumber,
    string Status,
    DateOnly OrderDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal RoundingAmount,
    int CurrentRevision,
    long Version,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? RemainderCancelledAtUtc,
    string? RemainderCancellationReason,
    string? Note,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<SalesOrderRevisionDto> Revisions);

public sealed record SalesOrderSummaryDto(
    Guid Id,
    Guid LocationId,
    Guid? CustomerId,
    Guid? SourceQuoteId,
    string OrderNumber,
    string Status,
    DateOnly OrderDate,
    DateOnly? PromisedDeliveryDate,
    string Currency,
    decimal TotalAmount,
    int CurrentRevision,
    long Version);

public sealed record QuoteConversionResultDto(SalesQuoteDto Quote, SalesOrderDto Order);

public interface IQuoteApiClient
{
    Task<PagedResult<SalesQuoteSummaryDto>> ListAsync(string? status = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> CreateAsync(CreateQuoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> UpdateAsync(Guid id, UpdateQuoteRequest request, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> SendAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> AcceptAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> RejectAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> AmendAsync(Guid id, QuoteAmendRequest request, CancellationToken cancellationToken = default);
    Task<SalesQuoteDto> CancelAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default);
    Task<QuoteConversionResultDto> ConvertAsync(Guid id, ConvertQuoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}

public interface ISalesOrderApiClient
{
    Task<PagedResult<SalesOrderSummaryDto>> ListAsync(string? status = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> UpdateAsync(Guid id, UpdateSalesOrderRequest request, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> ConfirmAsync(Guid id, ConfirmSalesOrderRequest request, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> AmendAsync(Guid id, AmendSalesOrderRequest request, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> CancelRemainderAsync(Guid id, CancelOrderRemainderRequest request, CancellationToken cancellationToken = default);
}

public interface IFulfillmentApiClient
{
    Task<InventoryReservationDto> ReserveAsync(Guid orderId, ReserveSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<InventoryReservationDto> ReleaseReservationsAsync(Guid orderId, ReleaseSalesOrderReservationRequest request, CancellationToken cancellationToken = default);
    Task<SalesOrderDeliveryDto> DeliverAsync(Guid orderId, DeliverSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<PagedResult<SalesOrderDeliveryDto>> ListDeliveriesAsync(Guid orderId, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<QtsBilling.Contracts.Invoices.InvoiceDetailDto> CreateInvoiceAsync(Guid orderId, Guid deliveryId, CreateDeliveryInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}
