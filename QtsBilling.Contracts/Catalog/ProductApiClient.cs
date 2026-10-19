using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Catalog;

/// <summary>
/// Catalog contract consumed by the browser client and the MAUI offline cache. Implementations must
/// never touch a database directly (specification §2.1/QB-15).
/// </summary>
public interface IProductApiClient
{
    Task<PagedResult<ProductDto>> GetProductsAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<ProductDetailDto> GetProductAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation shared by the browser client, server prerender and MAUI.</summary>
public sealed class ProductApiClient : IProductApiClient
{
    public const string RouteBase = "/api/v1/products";

    private readonly HttpClient _http;

    public ProductApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PagedResult<ProductDto>> GetProductsAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = $"?pageSize={pageSize}&includeInactive={(includeInactive ? "true" : "false")}";

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search)}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await _http.GetAsync(RouteBase + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<ProductDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? PagedResult<ProductDto>.Empty(pageSize);
    }

    public async Task<ProductDetailDto> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<ProductDetailDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The catalog response was empty.");
    }
}
