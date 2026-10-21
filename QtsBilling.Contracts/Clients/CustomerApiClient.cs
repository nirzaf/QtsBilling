using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Clients;

/// <summary>
/// Customer API contract consumed by the browser client and (later) the MAUI app. Implementations
/// must never touch a database directly (specification §2.1/QB-15).
/// </summary>
public interface ICustomerApiClient
{
    /// <summary>
    /// One page of this business's customers. <paramref name="includeInactive"/> is what keeps a deactivated party
    /// reachable for the history it belongs to: the default answer hides them, because they may not be chosen for
    /// anything new (specification task 15, recipe step 3).
    /// </summary>
    Task<PagedResult<CustomerDto>> GetCustomersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default);

    Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        SaveCustomerRequest request,
        long version,
        CancellationToken cancellationToken = default);

    Task DeactivateCustomerAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation shared by the browser client, server prerender and MAUI.</summary>
public sealed class CustomerApiClient : ICustomerApiClient
{
    public const string RouteBase = "/api/v1/customers";

    private readonly HttpClient _http;

    public CustomerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PagedResult<CustomerDto>> GetCustomersAsync(
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
            .ReadFromJsonAsync<PagedResult<CustomerDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);

        return page ?? PagedResult<CustomerDto>.Empty(pageSize);
    }

    public async Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RouteBase, request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<CustomerDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        SaveCustomerRequest request,
        long version,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{RouteBase}/{id}")
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("If-Match", version.ToString());

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<CustomerDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateCustomerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"{RouteBase}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
