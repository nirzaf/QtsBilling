using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Orders;

public sealed class FulfillmentApiClient(HttpClient http) : IFulfillmentApiClient
{
    private const string Route = "/api/v1/sales-orders";

    public Task<InventoryReservationDto> ReserveAsync(Guid orderId, ReserveSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendCreateAsync<InventoryReservationDto>($"{Route}/{orderId}/reservations", request, idempotencyKey, cancellationToken);

    public async Task ReleaseReservationsAsync(Guid orderId, ReleaseSalesOrderReservationRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Route}/{orderId}/reservations/release") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<SalesOrderDeliveryDto> DeliverAsync(Guid orderId, DeliverSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendCreateAsync<SalesOrderDeliveryDto>($"{Route}/{orderId}/deliveries", request, idempotencyKey, cancellationToken);

    public async Task<PagedResult<SalesOrderDeliveryDto>> ListDeliveriesAsync(Guid orderId, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await http.GetAsync($"{Route}/{orderId}/deliveries?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<SalesOrderDeliveryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<SalesOrderDeliveryDto>.Empty(Math.Clamp(pageSize, 1, 200));
    }

    public Task<InvoiceDetailDto> CreateInvoiceAsync(Guid orderId, Guid deliveryId, CreateDeliveryInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => SendCreateAsync<InvoiceDetailDto>($"{Route}/{orderId}/deliveries/{deliveryId}/invoices", request, idempotencyKey, cancellationToken);

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
