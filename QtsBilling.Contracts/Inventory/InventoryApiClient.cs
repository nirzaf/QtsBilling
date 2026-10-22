using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Reports;
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
        Guid? variantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The movement history behind one balance - how it got to what it holds (specification task 29).</summary>
    Task<PagedResult<InventoryMovementDto>> GetMovementsAsync(
        Guid? variantId = null,
        Guid? locationId = null,
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Balances that do not tie to their movement history, offered as exceptions to investigate (task 29).</summary>
    Task<InventoryReconciliationDto> GetReconciliationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The authoritative valued stock at a date, computed from posted movements on the reports route - the one
    /// answer to "what is it worth" that includes no provisional device estimate (specification task 29).
    /// </summary>
    Task<StockSummaryDto> GetStockSummaryAsync(
        DateOnly? asOf = null,
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

    Task<IReadOnlyList<StockCountSummaryDto>> GetStockCountsAsync(CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> GetStockCountAsync(Guid countId, CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> CreateStockCountAsync(
        CreateStockCountRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> RecordStockCountLinesAsync(
        Guid countId,
        RecordStockCountLinesRequest request,
        CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> ApproveStockCountAsync(Guid countId, CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> CancelStockCountAsync(Guid countId, CancellationToken cancellationToken = default);
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
        Guid? variantId = null,
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

        if (variantId is { } variant)
        {
            query += $"&variantId={variant:D}";
        }

        using var response = await _http.GetAsync(RouteBase + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<InventoryBalanceDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? PagedResult<InventoryBalanceDto>.Empty(pageSize);
    }

    public async Task<PagedResult<InventoryMovementDto>> GetMovementsAsync(
        Guid? variantId = null,
        Guid? locationId = null,
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = $"?pageSize={Math.Clamp(pageSize, 1, 200)}";

        if (variantId is { } variant)
        {
            query += $"&variantId={variant:D}";
        }

        if (locationId is { } location)
        {
            query += $"&locationId={location:D}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await _http.GetAsync("/api/v1/inventory/movements" + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<InventoryMovementDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? PagedResult<InventoryMovementDto>.Empty(pageSize);
    }

    public async Task<InventoryReconciliationDto> GetReconciliationAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/v1/inventory/reconciliation", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<InventoryReconciliationDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The reconciliation response was empty.");
    }

    public async Task<StockSummaryDto> GetStockSummaryAsync(
        DateOnly? asOf = null,
        Guid? locationId = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();

        if (asOf is { } date)
        {
            query.Add($"asOf={date:yyyy-MM-dd}");
        }

        if (locationId is { } location)
        {
            query.Add($"locationId={location:D}");
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);

        using var response = await _http.GetAsync("/api/v1/reports/stock-summary" + suffix, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<StockSummaryDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The stock-summary response was empty.");
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

    public async Task<IReadOnlyList<StockCountSummaryDto>> GetStockCountsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/v1/inventory/stock-counts", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<StockCountSummaryDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public Task<StockCountDetailDto> GetStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => SendStockCountCommandAsync<StockCountDetailDto>(HttpMethod.Get, $"/api/v1/inventory/stock-counts/{countId:D}", null, null, cancellationToken);

    public Task<StockCountDetailDto> CreateStockCountAsync(
        CreateStockCountRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => SendStockCountCommandAsync<StockCountDetailDto>(HttpMethod.Post, "/api/v1/inventory/stock-counts", request, idempotencyKey, cancellationToken);

    public Task<StockCountDetailDto> RecordStockCountLinesAsync(
        Guid countId,
        RecordStockCountLinesRequest request,
        CancellationToken cancellationToken = default)
        => SendStockCountCommandAsync<StockCountDetailDto>(HttpMethod.Put, $"/api/v1/inventory/stock-counts/{countId:D}/lines", request, null, cancellationToken);

    public Task<StockCountDetailDto> ApproveStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => SendStockCountCommandAsync<StockCountDetailDto>(HttpMethod.Post, $"/api/v1/inventory/stock-counts/{countId:D}/approve", null, null, cancellationToken);

    public Task<StockCountDetailDto> CancelStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => SendStockCountCommandAsync<StockCountDetailDto>(HttpMethod.Post, $"/api/v1/inventory/stock-counts/{countId:D}/cancel", null, null, cancellationToken);

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

    private async Task<T> SendStockCountCommandAsync<T>(
        HttpMethod method,
        string route,
        object? payload,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: ContractJson.Options);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The stock-count response was empty.");
    }
}
