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

    /// <summary>Adds an account to the chart. A code already in use is refused rather than reused.</summary>
    Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renames, re-parents or deactivates an account. Posted history is never rewritten by this call.</summary>
    Task<AccountDto> UpdateAccountAsync(Guid accountId, UpdateAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every accounting period the business owns, oldest first.</summary>
    Task<IReadOnlyList<AccountingPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a period. Overlapping windows are refused by the database, not warned about.</summary>
    Task<AccountingPeriodDto> CreatePeriodAsync(SaveAccountingPeriodRequest request, CancellationToken cancellationToken = default);

    /// <summary>What closing this period would leave behind, and how ready the year is.</summary>
    Task<PeriodCloseEvidenceDto> GetPeriodCloseEvidenceAsync(Guid periodId, CancellationToken cancellationToken = default);

    /// <summary>Closes a period under the version the reviewer read, with a reason, under one stable key.</summary>
    Task<AccountingPeriodDto> ClosePeriodAsync(
        Guid periodId, CloseAccountingPeriodRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>The year-end position: readiness, coverage, the net income a close would move, and the closing entries.</summary>
    Task<FiscalYearEvidenceDto> GetYearCloseEvidenceAsync(int fiscalYear, CancellationToken cancellationToken = default);

    /// <summary>Runs the year-end transfer once per key. A retry answers the same entry.</summary>
    Task<FiscalYearCloseDto> CloseYearAsync(int fiscalYear, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Journal entries newest first, filtered by status, origin and effective date.</summary>
    Task<PagedResult<JournalEntrySummaryDto>> ListJournalsAsync(
        string? cursor = null,
        int? pageSize = null,
        string? status = null,
        string? origin = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>One entry with its lines and the entry it reversed, if it is a reversal.</summary>
    Task<JournalEntryDto> GetJournalAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a manual journal. A stable key makes the posting exactly-once across a lost response; the server, not
    /// the caller, decides the period and refuses a closed or missing one.
    /// </summary>
    Task<JournalEntryDto> PostJournalAsync(
        PostJournalEntryRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default);

    /// <summary>Reverses a posted entry, optionally on a correction date inside an open period, with a reason.</summary>
    Task<JournalEntryDto> ReverseJournalAsync(
        Guid entryId, ReverseJournalRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Trial balance for a window, derived from posted lines only.</summary>
    Task<TrialBalanceDto> GetTrialBalanceAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountingApiClient" />
public sealed class AccountingApiClient : IAccountingApiClient
{
    public const string AccountsRoute = "/api/v1/accounts";
    public const string SettingsRoute = "/api/v1/accounting/settings";
    public const string PeriodsRoute = "/api/v1/accounting-periods";
    public const string JournalRoute = "/api/v1/journal-entries";
    public const string TrialBalanceRoute = "/api/v1/reports/trial-balance";
    public const string YearCloseRoutePrefix = "/api/v1/accounting/periods";

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

    public async Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _http.PostAsJsonAsync(AccountsRoute, request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<AccountDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid accountId, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _http.PutAsJsonAsync($"{AccountsRoute}/{accountId:D}", request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<AccountDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountingPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(PeriodsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<List<AccountingPeriodDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountingPeriodDto> CreatePeriodAsync(SaveAccountingPeriodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _http.PostAsJsonAsync(PeriodsRoute, request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<AccountingPeriodDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeriodCloseEvidenceDto> GetPeriodCloseEvidenceAsync(Guid periodId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{PeriodsRoute}/{periodId:D}/close-evidence", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<PeriodCloseEvidenceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountingPeriodDto> ClosePeriodAsync(
        Guid periodId, CloseAccountingPeriodRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostWithKeyAsync<CloseAccountingPeriodRequest, AccountingPeriodDto>(
            $"{PeriodsRoute}/{periodId:D}/close", request, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FiscalYearEvidenceDto> GetYearCloseEvidenceAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{YearCloseRoutePrefix}/{fiscalYear.ToString(System.Globalization.CultureInfo.InvariantCulture)}/close-year-evidence", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<FiscalYearEvidenceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FiscalYearCloseDto> CloseYearAsync(int fiscalYear, string idempotencyKey, CancellationToken cancellationToken = default)
        => await PostNoBodyAsync<FiscalYearCloseDto>(
            $"{YearCloseRoutePrefix}/{fiscalYear.ToString(System.Globalization.CultureInfo.InvariantCulture)}/close-year", idempotencyKey, cancellationToken).ConfigureAwait(false);

    public async Task<PagedResult<JournalEntrySummaryDto>> ListJournalsAsync(
        string? cursor = null,
        int? pageSize = null,
        string? status = null,
        string? origin = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor.Trim())}");
        if (pageSize is { } size) query.Add($"pageSize={size.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status.Trim())}");
        if (!string.IsNullOrWhiteSpace(origin)) query.Add($"origin={Uri.EscapeDataString(origin.Trim())}");
        if (from is { } start) query.Add($"from={start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
        if (to is { } end) query.Add($"to={end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");

        var route = query.Count == 0 ? JournalRoute : $"{JournalRoute}?{string.Join('&', query)}";
        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<PagedResult<JournalEntrySummaryDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalEntryDto> GetJournalAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{JournalRoute}/{entryId:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<JournalEntryDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalEntryDto> PostJournalAsync(
        PostJournalEntryRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, JournalRoute)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Trim());
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<JournalEntryDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalEntryDto> ReverseJournalAsync(
        Guid entryId, ReverseJournalRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostWithKeyAsync<ReverseJournalRequest, JournalEntryDto>(
            $"{JournalRoute}/{entryId:D}/reverse", request, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrialBalanceDto> GetTrialBalanceAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var route = $"{TrialBalanceRoute}?from={from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&to={to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<TrialBalanceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An accounting command carries one stable key, so a lost answer is recovered by asking about the same act
    /// rather than by posting a second journal or closing a period twice (specification F04, R05).
    /// </summary>
    private async Task<TResponse> PostWithKeyAsync<TRequest, TResponse>(
        string path, TRequest body, string idempotencyKey, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostNoBodyAsync<TResponse>(string path, string idempotencyKey, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }
}
