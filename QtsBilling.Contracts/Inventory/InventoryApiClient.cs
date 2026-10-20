using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Inventory;

/// <summary>
/// Authoritative stock projection consumed by the browser client and the MAUI offline cache. Stock is
/// read-only here: a device may look at it offline, but the server always re-checks it when an offline
/// sale is pushed (specification §3.5/F-12).
/// </summary>
public interface IInventoryApiClient
{
    Task<PagedResult<InventoryBalanceDto>> GetBalancesAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 50,
        Guid? locationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The business locations the device may be assigned to.</summary>
    Task<IReadOnlyList<BusinessLocationDto>> GetLocationsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockTransferSummaryDto>> GetTransfersAsync(CancellationToken cancellationToken = default);

    Task<StockTransferDetailDto> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);

    Task<StockTransferDetailDto> CreateTransferAsync(
        CreateStockTransferRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<StockTransferDetailDto> DispatchTransferAsync(Guid transferId, CancellationToken cancellationToken = default);

    Task<StockTransferDetailDto> ReceiveTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation shared by the browser client, server prerender and MAUI.</summary>
public sealed class InventoryApiClient : IInventoryApiClient
{
    public const string RouteBase = "/api/v1/inventory/balances";

    private readonly HttpClient _http;

    public InventoryApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PagedResult<InventoryBalanceDto>> GetBalancesAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 50,
        Guid? locationId = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"?pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search)}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        if (locationId is { } location)
        {
            query += $"&locationId={location:D}";
        }

        using var response = await _http.GetAsync(RouteBase + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<InventoryBalanceDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? PagedResult<InventoryBalanceDto>.Empty(pageSize);
    }

    public async Task<IReadOnlyList<BusinessLocationDto>> GetLocationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/v1/locations", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<List<BusinessLocationDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<StockTransferSummaryDto>> GetTransfersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/v1/inventory/transfers", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<StockTransferSummaryDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task<StockTransferDetailDto> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"/api/v1/inventory/transfers/{transferId:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<StockTransferDetailDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The stock-transfer response was empty.");
    }

    public Task<StockTransferDetailDto> CreateTransferAsync(
        CreateStockTransferRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => SendTransferCommandAsync(HttpMethod.Post, "/api/v1/inventory/transfers", request, idempotencyKey, cancellationToken);

    public Task<StockTransferDetailDto> DispatchTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => SendTransferCommandAsync(HttpMethod.Post, $"/api/v1/inventory/transfers/{transferId:D}/dispatch", null, null, cancellationToken);

    public Task<StockTransferDetailDto> ReceiveTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => SendTransferCommandAsync(HttpMethod.Post, $"/api/v1/inventory/transfers/{transferId:D}/receive", null, null, cancellationToken);

    private async Task<StockTransferDetailDto> SendTransferCommandAsync(
        HttpMethod method,
        string route,
        object? payload,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: ContractJson.Options);
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<StockTransferDetailDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The stock-transfer response was empty.");
    }
}
