using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Purchasing;

/// <summary>
/// The supplier side of the party workbench (specification task 15). Suppliers were API-only until now, so an
/// operator had no way to add, correct, or retire a supplier without a request tool: this client is the same set
/// of routes the purchasing endpoints already publish, with the version and inactive-record behaviour the screens
/// need. Nothing here is a second authority - the server still decides validation, uniqueness and deactivation.
/// </summary>
public interface ISupplierApiClient
{
    /// <summary>
    /// One page of suppliers. The search and the cursor are the server's: a workbench never loads the whole file
    /// and never shows only the first hundred. <paramref name="includeInactive"/> keeps a retired supplier
    /// reachable for the bills and receipts that still name it.
    /// </summary>
    Task<PagedResult<SupplierDto>> GetSuppliersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default);

    Task<SupplierDto> GetSupplierAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SupplierDto> CreateSupplierAsync(SaveSupplierRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the correction. <paramref name="version"/> is the one the caller read and travels as
    /// <c>If-Match</c>, so two people editing the same supplier cannot silently overwrite one another.
    /// </summary>
    Task<SupplierDto> UpdateSupplierAsync(
        Guid id,
        SaveSupplierRequest request,
        long version,
        CancellationToken cancellationToken = default);

    /// <summary>Takes the supplier out of service. History keeps resolving it; new orders may not select it.</summary>
    Task DeactivateSupplierAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISupplierApiClient" />
public sealed class SupplierApiClient : ISupplierApiClient
{
    public const string RouteBase = "/api/v1/suppliers";

    private readonly HttpClient _http;

    public SupplierApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PagedResult<SupplierDto>> GetSuppliersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool? includeInactive = null,
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

        if (includeInactive == true)
        {
            query += "&includeInactive=true";
        }

        using var response = await _http.GetAsync(RouteBase + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        var page = await response.Content
            .ReadFromJsonAsync<PagedResult<SupplierDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);

        return page ?? PagedResult<SupplierDto>.Empty(pageSize);
    }

    public async Task<SupplierDto> GetSupplierAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SupplierDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupplierDto> CreateSupplierAsync(SaveSupplierRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync(RouteBase, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SupplierDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupplierDto> UpdateSupplierAsync(
        Guid id,
        SaveSupplierRequest request,
        long version,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{RouteBase}/{id}")
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("If-Match", version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SupplierDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateSupplierAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"{RouteBase}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
