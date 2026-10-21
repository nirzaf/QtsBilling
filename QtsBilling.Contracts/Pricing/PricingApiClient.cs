using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Pricing;

/// <summary>
/// The tax and discount definitions a business prices with (specification task 16). The rate travels as the exact
/// decimal fraction the server stores; a percentage is a display convention, never the stored value, so a rate can
/// not drift between the screen and a posted line's snapshot.
/// </summary>
public interface IPricingApiClient
{
    Task<IReadOnlyList<TaxRateDto>> ListTaxRatesAsync(bool? includeInactive = null, CancellationToken cancellationToken = default);

    Task<TaxRateDto> GetTaxRateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TaxRateDto> CreateTaxRateAsync(SaveTaxRateRequest request, CancellationToken cancellationToken = default);

    Task<TaxRateDto> UpdateTaxRateAsync(Guid id, SaveTaxRateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes a rate out of service and clears its default flag. Documents that used it stay legible.</summary>
    Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscountDto>> ListDiscountsAsync(
        bool? includeInactive = null,
        string? scope = null,
        CancellationToken cancellationToken = default);

    Task<DiscountDto> GetDiscountAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DiscountDto> CreateDiscountAsync(SaveDiscountRequest request, CancellationToken cancellationToken = default);

    Task<DiscountDto> UpdateDiscountAsync(Guid id, SaveDiscountRequest request, CancellationToken cancellationToken = default);

    Task DeactivateDiscountAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPricingApiClient" />
public sealed class PricingApiClient : IPricingApiClient
{
    public const string TaxRoute = "/api/v1/tax-rates";
    public const string DiscountRoute = "/api/v1/discounts";

    private readonly HttpClient _http;

    public PricingApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<TaxRateDto>> ListTaxRatesAsync(bool? includeInactive = null, CancellationToken cancellationToken = default)
        => await GetListAsync<TaxRateDto>(TaxRoute, includeInactive, scope: null, cancellationToken).ConfigureAwait(false);

    public async Task<TaxRateDto> GetTaxRateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{TaxRoute}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<TaxRateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxRateDto> CreateTaxRateAsync(SaveTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PostAsJsonAsync(TaxRoute, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<TaxRateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxRateDto> UpdateTaxRateAsync(Guid id, SaveTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{TaxRoute}/{id}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<TaxRateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"{TaxRoute}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscountDto>> ListDiscountsAsync(
        bool? includeInactive = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(2);

        if (includeInactive == true)
        {
            query.Add("includeInactive=true");
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            query.Add($"scope={Uri.EscapeDataString(scope.Trim())}");
        }

        var route = query.Count == 0 ? DiscountRoute : $"{DiscountRoute}?{string.Join('&', query)}";

        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<DiscountDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscountDto> GetDiscountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{DiscountRoute}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DiscountDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscountDto> CreateDiscountAsync(SaveDiscountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PostAsJsonAsync(DiscountRoute, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DiscountDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscountDto> UpdateDiscountAsync(Guid id, SaveDiscountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{DiscountRoute}/{id}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DiscountDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateDiscountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"{DiscountRoute}/{id}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string route, bool? includeInactive, string? scope, CancellationToken cancellationToken)
    {
        var query = includeInactive == true ? "?includeInactive=true" : string.Empty;

        using var response = await _http.GetAsync(route + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<T>>(response, cancellationToken).ConfigureAwait(false);
    }
}
