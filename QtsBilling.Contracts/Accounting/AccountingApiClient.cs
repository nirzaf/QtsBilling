using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Accounting;

/// <summary>
/// The chart of accounts and the business's control-account mapping. This is the single authority for both
/// (specification D05/D13): the setup screen reaches the same route the posting path reads, so a mapping can
/// never look different to an operator than it does to a journal.
/// </summary>
public interface IAccountingApiClient
{
    /// <summary>The accounts this business may map, optionally filtered by code or name.</summary>
    Task<IReadOnlyList<AccountDto>> ListAccountsAsync(string? search = null, bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Which account each posting rule uses today.</summary>
    Task<BusinessAccountingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a new mapping. The server validates type and tenant for every account it is given.</summary>
    Task<BusinessAccountingSettingsDto> SaveSettingsAsync(SaveBusinessAccountingSettingsRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountingApiClient" />
public sealed class AccountingApiClient : IAccountingApiClient
{
    public const string AccountsRoute = "/api/v1/accounts";
    public const string SettingsRoute = "/api/v1/accounting/settings";

    private readonly HttpClient _http;

    public AccountingApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(
        string? search = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (includeInactive)
        {
            query.Add("includeInactive=true");
        }

        var route = query.Count == 0 ? AccountsRoute : $"{AccountsRoute}?{string.Join('&', query)}";

        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<AccountDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessAccountingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(SettingsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessAccountingSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessAccountingSettingsDto> SaveSettingsAsync(
        SaveBusinessAccountingSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync(SettingsRoute, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessAccountingSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }
}
