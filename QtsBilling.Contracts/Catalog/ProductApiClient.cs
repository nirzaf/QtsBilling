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

    /// <summary>Creates a product with its default variant. The response is the same detail the screen then edits.</summary>
    Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request, CancellationToken cancellationToken = default);

    /// <summary>Corrects the description and references. The version is the one the caller read.</summary>
    Task<ProductDetailDto> UpdateProductAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds a variant. Its name, SKU and barcode are what the server matches against, so an ambiguous retry cannot produce a second copy.</summary>
    Task<ProductDetailDto> CreateVariantAsync(Guid id, CreateProductVariantRequest request, CancellationToken cancellationToken = default);

    Task<ProductVariantDto> UpdateVariantAsync(Guid id, Guid variantId, UpdateVariantRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retires one variant, naming the active sibling that becomes the default when it was the default.</summary>
    Task<ProductDetailDto> ArchiveVariantAsync(Guid id, Guid variantId, ArchiveVariantRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes the product and its variants out of service, with the reason the audit trail keeps.</summary>
    Task<ProductDetailDto> ArchiveProductAsync(Guid id, ArchiveProductRequest request, CancellationToken cancellationToken = default);

    /// <summary>Brings the product back. Variants keep the state they were left in.</summary>
    Task<ProductDetailDto> RestoreProductAsync(Guid id, RestoreProductRequest request, CancellationToken cancellationToken = default);
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

    public async Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Post, RouteBase, request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductDetailDto> UpdateProductAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Put, $"{RouteBase}/{id}", request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductDetailDto> CreateVariantAsync(Guid id, CreateProductVariantRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Post, $"{RouteBase}/{id}/variants", request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductVariantDto> UpdateVariantAsync(Guid id, Guid variantId, UpdateVariantRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductVariantDto>(HttpMethod.Put, $"{RouteBase}/{id}/variants/{variantId}", request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductDetailDto> ArchiveVariantAsync(Guid id, Guid variantId, ArchiveVariantRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Post, $"{RouteBase}/{id}/variants/{variantId}/archive", request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductDetailDto> ArchiveProductAsync(Guid id, ArchiveProductRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Post, $"{RouteBase}/{id}/archive", request, cancellationToken).ConfigureAwait(false);

    public async Task<ProductDetailDto> RestoreProductAsync(Guid id, RestoreProductRequest request, CancellationToken cancellationToken = default)
        => await SendAsync<ProductDetailDto>(HttpMethod.Post, $"{RouteBase}/{id}/restore", request, cancellationToken).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object request, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _http
            .SendAsync(new HttpRequestMessage(method, route)
            {
                Content = JsonContent.Create(request, options: ContractJson.Options),
            }, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
