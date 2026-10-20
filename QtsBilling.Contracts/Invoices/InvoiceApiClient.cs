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
}

public interface IPaymentApiClient
{
    Task<CustomerPaymentDto> CreateCustomerPaymentAsync(Guid customerId, CreateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<CustomerPaymentDto> AllocateAsync(Guid paymentId, AllocateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
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
        => SendAsync($"/api/v1/customers/{customerId:D}/payments", request, idempotencyKey, cancellationToken);

    public Task<CustomerPaymentDto> AllocateAsync(Guid paymentId, AllocateCustomerPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendAsync($"/api/v1/payments/{paymentId:D}/allocate", request, idempotencyKey, cancellationToken);

    private async Task<CustomerPaymentDto> SendAsync(string route, object body, string idempotencyKey, CancellationToken cancellationToken)
    {
        InvoiceApiClient.ValidateIdempotencyKey(idempotencyKey);
        using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<CustomerPaymentDto>(response, cancellationToken).ConfigureAwait(false);
    }
}
