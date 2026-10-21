using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Catalog;

/// <summary>
/// The four small, closed reference lists a catalog is built from. The name is the server's own resource word, so
/// a client cannot ask for a fifth list that nothing validates (specification task 16, D13).
/// </summary>
public enum MasterDataResource
{
    Units,
    Categories,
    Brands,
    ProductGroups,
}

/// <summary>
/// Units, categories, brands and product groups for the current business. These are maintained here rather than
/// inside the product screen because a product selects them; the server still owns name uniqueness, the category
/// hierarchy and the deactivation rules.
/// </summary>
public interface IMasterDataApiClient
{
    /// <summary>The whole list for one resource. These sets are small and closed, so the screen shows them complete.</summary>
    Task<IReadOnlyList<MasterDataDto>> ListAsync(
        MasterDataResource resource,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default);

    Task<MasterDataDto> CreateAsync(MasterDataResource resource, SaveMasterDataRequest request, CancellationToken cancellationToken = default);

    Task<MasterDataDto> UpdateAsync(MasterDataResource resource, Guid id, SaveMasterDataRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes the record out of service. Products that already use it keep resolving it.</summary>
    Task DeactivateAsync(MasterDataResource resource, Guid id, CancellationToken cancellationToken = default);

    /// <summary>The server's own path segment for a resource, exposed so a screen can name what it is saving.</summary>
    public static string RouteOf(MasterDataResource resource) => resource switch
    {
        MasterDataResource.Units => "units",
        MasterDataResource.Categories => "categories",
        MasterDataResource.Brands => "brands",
        MasterDataResource.ProductGroups => "product-groups",
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown catalog reference list."),
    };
}

/// <inheritdoc cref="IMasterDataApiClient" />
public sealed class MasterDataApiClient : IMasterDataApiClient
{
    private const string RouteBase = "/api/v1";

    private readonly HttpClient _http;

    public MasterDataApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<MasterDataDto>> ListAsync(
        MasterDataResource resource,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default)
    {
        var route = includeInactive == true
            ? $"{RouteBase}/{IMasterDataApiClient.RouteOf(resource)}?includeInactive=true"
            : $"{RouteBase}/{IMasterDataApiClient.RouteOf(resource)}";

        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<MasterDataDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MasterDataDto> CreateAsync(
        MasterDataResource resource,
        SaveMasterDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/{IMasterDataApiClient.RouteOf(resource)}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<MasterDataDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MasterDataDto> UpdateAsync(
        MasterDataResource resource,
        Guid id,
        SaveMasterDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{RouteBase}/{IMasterDataApiClient.RouteOf(resource)}/{id}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<MasterDataDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateAsync(MasterDataResource resource, Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .DeleteAsync($"{RouteBase}/{IMasterDataApiClient.RouteOf(resource)}/{id}", cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
