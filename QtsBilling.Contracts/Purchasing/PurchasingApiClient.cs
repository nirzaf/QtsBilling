using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Purchasing;

/// <summary>
/// Typed access to the purchase-order, receiving and return contract (specification task 26). The routes already
/// exist server-side; this is the screen-facing shape of them, with nothing invented: a receipt or a return is
/// sent under a client-generated key because each one is a document with a journal behind it, while a draft edit
/// travels with no key at all because the server refuses a stale draft by version.
/// </summary>
public interface IPurchasingApiClient
{
    Task<PagedResult<PurchaseOrderSummaryDto>> ListOrdersAsync(
        PurchaseOrderState? status = null,
        Guid? supplierId = null,
        Guid? locationId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    Task<PurchaseOrderDetailDto> GetOrderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PurchaseOrderDetailDto> CreateOrderAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default);
    Task<PurchaseOrderDetailDto> UpdateOrderAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default);
    Task DeleteOrderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PurchaseOrderDetailDto> ConfirmOrderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PurchaseOrderDetailDto> CancelOrderAsync(Guid id, PurchaseOrderActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Goods received note against ordered lines. Partial deliveries are ordinary; over-receipt is refused.</summary>
    Task<PurchaseReceiptDto> ReceiveAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Linked return of quantities already received. Never a negative receipt, and never a stock edit.</summary>
    Task<PurchaseReceiptDto> ReturnAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>The receipt and return history of one order, oldest first.</summary>
    Task<IReadOnlyList<PurchaseReceiptDto>> ListReceiptsAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payable side (specification task 27): supplier bills with their credits and control links, the credit and
    /// settlement commands that change a payable only through new documents, and one supplier's account read as at a
    /// business date. Every write carries a client-generated key because each one posts a journal behind it.
    /// </summary>
    Task<IReadOnlyList<SupplierBillDto>> ListBillsAsync(
        Guid? supplierId = null,
        bool openOnly = false,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    Task<SupplierBillDto> GetBillAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SupplierBillDto> CreateBillAsync(CreateSupplierBillRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SupplierBillDto> IssueBillAsync(Guid id, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SupplierBillDto> CreateCreditAsync(Guid billId, CreateSupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SupplierBillDto> ApplyCreditAsync(Guid creditId, ApplySupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SupplierPaymentDto> PaySupplierAsync(CreateSupplierPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SupplierStatementDto> GetStatementAsync(Guid supplierId, DateOnly? asOf = null, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation of <see cref="IPurchasingApiClient"/>. Carries no persistence or caching concern.</summary>
public sealed class PurchasingApiClient(HttpClient http) : IPurchasingApiClient
{
    private const string Route = "/api/v1/purchase-orders";
    private const string BillRoute = "/api/v1/purchasing/bills";
    private const string CreditRoute = "/api/v1/purchasing/credits";
    private const string PaymentRoute = "/api/v1/purchasing/supplier-payments";

    public async Task<PagedResult<PurchaseOrderSummaryDto>> ListOrdersAsync(
        PurchaseOrderState? status = null,
        Guid? supplierId = null,
        Guid? locationId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var query = new List<string> { $"pageSize={size}" };
        if (status is { } state) query.Add($"status={Uri.EscapeDataString(state.ToString())}");
        if (supplierId is { } supplier) query.Add($"supplierId={supplier:D}");
        if (locationId is { } location) query.Add($"locationId={location:D}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        using var response = await http.GetAsync($"{Route}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<PurchaseOrderSummaryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<PurchaseOrderSummaryDto>.Empty(size);
    }

    public Task<PurchaseOrderDetailDto> GetOrderAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<PurchaseOrderDetailDto>(HttpMethod.Get, $"{Route}/{id:D}", null, null, cancellationToken);

    public Task<PurchaseOrderDetailDto> CreateOrderAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PurchaseOrderDetailDto>(HttpMethod.Post, Route, request, null, cancellationToken);

    public Task<PurchaseOrderDetailDto> UpdateOrderAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PurchaseOrderDetailDto>(HttpMethod.Put, $"{Route}/{id:D}", request, null, cancellationToken);

    public Task<PurchaseOrderDetailDto> ConfirmOrderAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<PurchaseOrderDetailDto>(HttpMethod.Post, $"{Route}/{id:D}/order", null, null, cancellationToken);

    public Task<PurchaseOrderDetailDto> CancelOrderAsync(Guid id, PurchaseOrderActionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PurchaseOrderDetailDto>(HttpMethod.Post, $"{Route}/{id:D}/cancel", request, null, cancellationToken);

    public Task<PurchaseReceiptDto> ReceiveAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<PurchaseReceiptDto>(HttpMethod.Post, $"{Route}/{orderId:D}/receipts", request, idempotencyKey, cancellationToken);
    }

    public Task<PurchaseReceiptDto> ReturnAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<PurchaseReceiptDto>(HttpMethod.Post, $"{Route}/{orderId:D}/returns", request, idempotencyKey, cancellationToken);
    }

    public async Task DeleteOrderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"{Route}/{id:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseReceiptDto>> ListReceiptsAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Route}/{orderId:D}/receipts", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<PurchaseReceiptDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object? body, string? idempotencyKey, CancellationToken cancellationToken)
        where T : class
    {
        using var message = new HttpRequestMessage(method, uri);
        if (body is not null) message.Content = JsonContent.Create(body, options: ContractJson.Options);
        if (idempotencyKey is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SupplierBillDto>> ListBillsAsync(
        Guid? supplierId = null,
        bool openOnly = false,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 200)}" };
        if (supplierId is { } supplier) query.Add($"supplierId={supplier:D}");
        if (openOnly) query.Add("openOnly=true");

        using var response = await http.GetAsync($"{BillRoute}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SupplierBillDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public Task<SupplierBillDto> GetBillAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<SupplierBillDto>(HttpMethod.Get, $"{BillRoute}/{id:D}", null, null, cancellationToken);

    public Task<SupplierBillDto> CreateBillAsync(CreateSupplierBillRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<SupplierBillDto>(HttpMethod.Post, BillRoute, request, idempotencyKey, cancellationToken);
    }

    public Task<SupplierBillDto> IssueBillAsync(Guid id, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<SupplierBillDto>(HttpMethod.Post, $"{BillRoute}/{id:D}/issue", null, idempotencyKey, cancellationToken);
    }

    public Task<SupplierBillDto> CreateCreditAsync(Guid billId, CreateSupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<SupplierBillDto>(HttpMethod.Post, $"{BillRoute}/{billId:D}/credits", request, idempotencyKey, cancellationToken);
    }

    public Task<SupplierBillDto> ApplyCreditAsync(Guid creditId, ApplySupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<SupplierBillDto>(HttpMethod.Post, $"{CreditRoute}/{creditId:D}/applications", request, idempotencyKey, cancellationToken);
    }

    public Task<SupplierPaymentDto> PaySupplierAsync(CreateSupplierPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<SupplierPaymentDto>(HttpMethod.Post, PaymentRoute, request, idempotencyKey, cancellationToken);
    }

    public async Task<SupplierStatementDto> GetStatementAsync(Guid supplierId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var uri = asOf is { } date
            ? $"/api/v1/suppliers/{supplierId:D}/statement?asOf={date:yyyy-MM-dd}"
            : $"/api/v1/suppliers/{supplierId:D}/statement";

        using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<SupplierStatementDto>(response, cancellationToken).ConfigureAwait(false);
    }
}
