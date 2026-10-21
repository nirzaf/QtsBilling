using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Administration;

/// <summary>
/// The owner-facing setup surface: the business identity, its numbering and its branches. The financial account
/// mappings stay on <c>/api/v1/accounting/settings</c> and the user and terminal screens reuse the administration
/// routes, so no second authority for the same fact is invented here (specification D13).
/// </summary>
public interface IBusinessSetupApiClient
{
    /// <summary>The business identity, its branches, and what still blocks posting.</summary>
    Task<BusinessSettingsDto> GetBusinessSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the identity. A stale <c>ExpectedVersion</c> raises <see cref="ApiException"/> with the conflict code.</summary>
    Task<BusinessSettingsDto> SaveBusinessSettingsAsync(SaveBusinessSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>The branches of the current business, as the setup screen lists them.</summary>
    Task<IReadOnlyList<BusinessLocationDto>> ListLocationsAsync(CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> CreateLocationAsync(CreateLocationRequest request, CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> UpdateLocationAsync(Guid locationId, UpdateLocationRequest request, CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> DeactivateLocationAsync(Guid locationId, DeactivateLocationRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBusinessSetupApiClient" />
public sealed class BusinessSetupApiClient : IBusinessSetupApiClient
{
    public const string SettingsRoute = "/api/v1/business-settings";
    public const string LocationsRoute = "/api/v1/locations";

    private readonly HttpClient _http;

    public BusinessSetupApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<BusinessSettingsDto> GetBusinessSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(SettingsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessSettingsDto> SaveBusinessSettingsAsync(SaveBusinessSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync(SettingsRoute, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BusinessLocationDto>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(LocationsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<BusinessLocationDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessLocationDto> CreateLocationAsync(CreateLocationRequest request, CancellationToken cancellationToken = default)
        => await PostAsync(LocationsRoute, request, cancellationToken).ConfigureAwait(false);

    public async Task<BusinessLocationDto> UpdateLocationAsync(Guid locationId, UpdateLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{LocationsRoute}/{locationId}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessLocationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessLocationDto> DeactivateLocationAsync(Guid locationId, DeactivateLocationRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{LocationsRoute}/{locationId}/deactivate", request, cancellationToken).ConfigureAwait(false);

    private async Task<BusinessLocationDto> PostAsync(string route, object request, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync(route, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessLocationDto>(response, cancellationToken).ConfigureAwait(false);
    }
}
