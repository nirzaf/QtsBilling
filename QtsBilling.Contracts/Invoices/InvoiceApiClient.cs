using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Invoices;

public interface IInvoiceApiClient
{
    Task<PagedResult<InvoiceSummaryDto>> ListAsync(InvoiceState? status = null, Guid? customerId = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> CreateAsync(SaveInvoiceRequest request, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> UpdateAsync(Guid id, SaveInvoiceRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> IssueAsync(Guid id, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> VoidAsync(Guid id, InvoiceVoidRequest request, CancellationToken cancellationToken = default);
    Task<InvoiceDetailDto> CreditAsync(Guid id, InvoiceCreditNoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<InvoicePaymentResult> AddPaymentAsync(Guid id, InvoicePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<PaymentTransitionDto> ClearPaymentAsync(Guid paymentId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<PaymentTransitionDto> BouncePaymentAsync(Guid paymentId, PaymentBounceRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<string> GetPrintHtmlAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the printable snapshot through this authenticated client and returns it as a document that a host
    /// may print. Nothing navigates and no token travels in a location: the response is read here, with the
    /// session this client already carries, and only a complete HTML document is handed on (task 24, recipe 3).
    /// A missing, foreign or expired document throws and yields no request at all, so the caller has nothing to
    /// print (T24.04).
    /// </summary>
    Task<DocumentOutputRequest> GetPrintDocumentAsync(Guid id, string title, CancellationToken cancellationToken = default);
}

public interface IPaymentApiClient
{
    Task<CustomerPaymentDto> CreateCustomerPaymentAsync(Guid customerId, CreateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<CustomerPaymentDto> AllocateAsync(Guid paymentId, AllocateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer's account as at a business date (task 25, recipe step 1): the posted events in order with the
    /// balance after each, the money still unapplied on deposit and the documents still open. <paramref name="asOf"/>
    /// is the customer's own trading date; an earlier date replays the account as it stood then, so a later receipt
    /// cannot appear in a statement printed before it arrived.
    /// </summary>
    Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Documents this customer has not fully settled - issued invoices and opening receivables together.</summary>
    Task<IReadOnlyList<CustomerOpenItemDto>> GetOpenItemsAsync(Guid customerId, DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Hands unapplied deposit money back to the customer under a stable key, with its reason and settlement route.</summary>
    Task<DepositRefundDto> RefundUnallocatedAsync(Guid paymentId, RefundUnallocatedRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Typed access to the native invoice lifecycle. This client contains no persistence concerns.</summary>
public sealed class InvoiceApiClient(HttpClient http) : IInvoiceApiClient
{
    private const string Route = "/api/v1/invoices";

    public async Task<PagedResult<InvoiceSummaryDto>> ListAsync(InvoiceState? status = null, Guid? customerId = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var query = new List<string> { $"pageSize={size}" };
        if (status is { } state) query.Add($"status={Uri.EscapeDataString(state.ToString())}");
        if (customerId is { } customer) query.Add($"customerId={customer:D}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await http.GetAsync($"{Route}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<InvoiceSummaryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<InvoiceSummaryDto>.Empty(size);
    }

    public Task<InvoiceDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => SendJsonAsync<InvoiceDetailDto>(HttpMethod.Get, $"{Route}/{id:D}", null, cancellationToken);
    public Task<InvoiceDetailDto> CreateAsync(SaveInvoiceRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<InvoiceDetailDto>(HttpMethod.Post, Route, request, cancellationToken);
    public Task<InvoiceDetailDto> UpdateAsync(Guid id, SaveInvoiceRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<InvoiceDetailDto>(HttpMethod.Put, $"{Route}/{id:D}", request, cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"{Route}/{id:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<InvoiceDetailDto> IssueAsync(Guid id, CancellationToken cancellationToken = default)
        => SendJsonAsync<InvoiceDetailDto>(HttpMethod.Post, $"{Route}/{id:D}/issue", null, cancellationToken);
    public Task<InvoiceDetailDto> VoidAsync(Guid id, InvoiceVoidRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<InvoiceDetailDto>(HttpMethod.Post, $"{Route}/{id:D}/void", request, cancellationToken);
    public Task<InvoiceDetailDto> CreditAsync(Guid id, InvoiceCreditNoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendIdempotentAsync<InvoiceDetailDto>(HttpMethod.Post, $"{Route}/{id:D}/credit-notes", request, idempotencyKey, cancellationToken);
    public Task<InvoicePaymentResult> AddPaymentAsync(Guid id, InvoicePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendIdempotentAsync<InvoicePaymentResult>(HttpMethod.Post, $"{Route}/{id:D}/payments", request, idempotencyKey, cancellationToken);
    public Task<PaymentTransitionDto> ClearPaymentAsync(Guid paymentId, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendIdempotentAsync<PaymentTransitionDto>(HttpMethod.Post, $"/api/v1/payments/{paymentId:D}/clear", null, idempotencyKey, cancellationToken);
    public Task<PaymentTransitionDto> BouncePaymentAsync(Guid paymentId, PaymentBounceRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendIdempotentAsync<PaymentTransitionDto>(HttpMethod.Post, $"/api/v1/payments/{paymentId:D}/bounce", request, idempotencyKey, cancellationToken);

    public async Task<string> GetPrintHtmlAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Route}/{id:D}/print", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentOutputRequest> GetPrintDocumentAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Route}/{id:D}/print", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        // A document is a document only if the server said so: an empty body, an error page or a redirect that a
        // proxy turned into a 200 must never reach a printer or a file.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                (int)response.StatusCode,
                ApiErrorCodes.Conflict,
                "The invoice document did not arrive as a printable page.",
                new Dictionary<string, string[]> { ["print"] = [$"Expected an HTML document but received '{mediaType}' from the server."] });
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // The constructor is the guard: it refuses a location, a non-document body and an unusable name.
        return new DocumentOutputRequest(content, title);
    }

    private Task<T> SendJsonAsync<T>(HttpMethod method, string uri, object? body, CancellationToken cancellationToken) where T : class
        => SendAsync<T>(method, uri, body, null, cancellationToken);

    private Task<T> SendIdempotentAsync<T>(HttpMethod method, string uri, object? body, string idempotencyKey, CancellationToken cancellationToken) where T : class
    {
        ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<T>(method, uri, body, idempotencyKey, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object? body, string? idempotencyKey, CancellationToken cancellationToken) where T : class
    {
        using var message = new HttpRequestMessage(method, uri);
        if (body is not null) message.Content = JsonContent.Create(body, options: ContractJson.Options);
        if (idempotencyKey is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
            throw new ArgumentException("An idempotency key of 1-64 characters is required.", nameof(idempotencyKey));
    }
}

/// <summary>Typed customer receipt and multi-invoice allocation operations.</summary>
public sealed class PaymentApiClient(HttpClient http) : IPaymentApiClient
{
    public Task<CustomerPaymentDto> CreateCustomerPaymentAsync(Guid customerId, CreateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendAsync<CustomerPaymentDto>($"/api/v1/customers/{customerId:D}/payments", request, idempotencyKey, cancellationToken);

    public Task<CustomerPaymentDto> AllocateAsync(Guid paymentId, AllocateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendAsync<CustomerPaymentDto>($"/api/v1/payments/{paymentId:D}/allocate", request, idempotencyKey, cancellationToken);

    public async Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var query = asOf is { } date ? $"?asOf={date:yyyy-MM-dd}" : string.Empty;
        using var response = await http.GetAsync($"/api/v1/customers/{customerId:D}/statement{query}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<CustomerStatementDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CustomerOpenItemDto>> GetOpenItemsAsync(Guid customerId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var query = asOf is { } date ? $"?asOf={date:yyyy-MM-dd}" : string.Empty;
        using var response = await http.GetAsync($"/api/v1/customers/{customerId:D}/open-items{query}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<IReadOnlyList<CustomerOpenItemDto>>(response, cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public Task<DepositRefundDto> RefundUnallocatedAsync(Guid paymentId, RefundUnallocatedRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        return SendAsync<DepositRefundDto>($"/api/v1/payments/{paymentId:D}/refund-unallocated", request, idempotencyKey, cancellationToken);
    }

    private async Task<T> SendAsync<T>(string route, object body, string idempotencyKey, CancellationToken cancellationToken) where T : class
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
