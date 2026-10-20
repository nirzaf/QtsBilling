using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Orders;

public sealed class QuoteApiClient(HttpClient http) : IQuoteApiClient
{
    private const string Route = "/api/v1/quotes";

    public async Task<PagedResult<SalesQuoteSummaryDto>> ListAsync(string? status = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status.Trim())}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await http.GetAsync($"{Route}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<SalesQuoteSummaryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<SalesQuoteSummaryDto>.Empty(Math.Clamp(pageSize, 1, 200));
    }

    public Task<SalesQuoteDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<SalesQuoteDto>(new HttpRequestMessage(HttpMethod.Get, $"{Route}/{id}"), cancellationToken);

    public Task<SalesQuoteDto> CreateAsync(CreateQuoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendCreateAsync<SalesQuoteDto>(Route, request, idempotencyKey, cancellationToken);

    public Task<SalesQuoteDto> UpdateAsync(Guid id, UpdateQuoteRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Put, $"{Route}/{id}", request, cancellationToken);

    public Task<SalesQuoteDto> SendAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Post, $"{Route}/{id}/send", request, cancellationToken);

    public Task<SalesQuoteDto> AcceptAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Post, $"{Route}/{id}/accept", request, cancellationToken);

    public Task<SalesQuoteDto> RejectAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Post, $"{Route}/{id}/reject", request, cancellationToken);

    public Task<SalesQuoteDto> AmendAsync(Guid id, QuoteAmendRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Post, $"{Route}/{id}/amend", request, cancellationToken);

    public Task<SalesQuoteDto> CancelAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesQuoteDto>(HttpMethod.Post, $"{Route}/{id}/cancel", request, cancellationToken);

    public async Task<QuoteConversionResultDto> ConvertAsync(Guid id, ConvertQuoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        return await SendCreateAsync<QuoteConversionResultDto>($"{Route}/{id}/convert", request, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendCreateAsync<T>(string uri, object body, string idempotencyKey, CancellationToken cancellationToken) where T : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await SendAsync<T>(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string uri, object body, CancellationToken cancellationToken) where T : class
    {
        using var message = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        return await SendAsync<T>(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken) where T : class
    {
        using (message)
        using (var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false))
        {
            await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
            return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class SalesOrderApiClient(HttpClient http) : ISalesOrderApiClient
{
    private const string Route = "/api/v1/sales-orders";

    public async Task<PagedResult<SalesOrderSummaryDto>> ListAsync(string? status = null, string? search = null, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status.Trim())}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await http.GetAsync($"{Route}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<SalesOrderSummaryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<SalesOrderSummaryDto>.Empty(Math.Clamp(pageSize, 1, 200));
    }

    public Task<SalesOrderDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<SalesOrderDto>(new HttpRequestMessage(HttpMethod.Get, $"{Route}/{id}"), cancellationToken);

    public Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendCreateAsync<SalesOrderDto>(Route, request, idempotencyKey, cancellationToken);

    public Task<SalesOrderDto> UpdateAsync(Guid id, UpdateSalesOrderRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesOrderDto>(HttpMethod.Put, $"{Route}/{id}", request, cancellationToken);

    public Task<SalesOrderDto> ConfirmAsync(Guid id, ConfirmSalesOrderRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesOrderDto>(HttpMethod.Post, $"{Route}/{id}/confirm", request, cancellationToken);

    public Task<SalesOrderDto> AmendAsync(Guid id, AmendSalesOrderRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesOrderDto>(HttpMethod.Post, $"{Route}/{id}/amend", request, cancellationToken);

    public Task<SalesOrderDto> CancelRemainderAsync(Guid id, CancelOrderRemainderRequest request, CancellationToken cancellationToken = default)
        => SendJsonAsync<SalesOrderDto>(HttpMethod.Post, $"{Route}/{id}/cancel-remainder", request, cancellationToken);

    private async Task<T> SendCreateAsync<T>(string uri, object body, string idempotencyKey, CancellationToken cancellationToken) where T : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await SendAsync<T>(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string uri, object body, CancellationToken cancellationToken) where T : class
    {
        using var message = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        return await SendAsync<T>(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken) where T : class
    {
        using (message)
        using (var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false))
        {
            await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
            return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }
}
