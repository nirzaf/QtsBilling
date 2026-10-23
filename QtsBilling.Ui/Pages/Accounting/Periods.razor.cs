using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Accounting;

/// <summary>
/// Accounting periods and the fiscal-year close (specification task 32, recipe step 3). A period is opened, and then
/// closed only after its evidence has been read: the balances it carries across the boundary, and the unfinished
/// acts inside it - a draft journal, an unconfirmed tender, a till still holding cash - that must be answered first.
/// The year transfer moves revenue and expense into retained earnings and locks the year; it is not a shutdown and it
/// is never undone by editing rows. Every gate is the server's: confirming on screen changes no rule.
/// </summary>
public partial class Periods : IAsyncDisposable
{
    [Inject] private IAccountingApiClient Ledger { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;

    private readonly HeldIntention<SaveAccountingPeriodRequest> _create = new("the new period");
    private readonly HeldIntention<CloseAccountingPeriodRequest> _close = new("the close");
    private readonly HeldIntention<string> _year = new("the year close");

    private IReadOnlyList<AccountingPeriodDto> _periods = [];
    private AccountingPeriodDto? _selected;
    private PeriodCloseEvidenceDto? _evidence;
    private FiscalYearEvidenceDto? _yearEvidence;
    private string _name = string.Empty;
    private DateTime? _start;
    private DateTime? _end;
    private string? _reason;
    private int _yearNumber;

    private int YearCandidate => _yearNumber != 0
        ? _yearNumber
        : _evidence?.Period.EndDate.Year ?? _periods.FirstOrDefault()?.EndDate.Year ?? 0;
    private string? _error;
    private string? _notice;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _canClose;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private int Scale => _evidence?.SettlementScale ?? _yearEvidence?.SettlementScale ?? 2;

    private bool CanCloseNow
        => AccountingRules.CanClose(_evidence, _canClose) && !string.IsNullOrWhiteSpace(_reason) && !_close.Outstanding;

    private bool CanCloseYearNow => AccountingRules.CanCloseYear(_yearEvidence, _canClose) && !_year.Outstanding;

    protected override void OnInitialized() => _state = ShellStateKind.Loading;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _busy = true;
        _error = null;
        _state = ShellStateKind.Loading;
        _message = null;

        try
        {
            var session = await AccountsApi.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = "Sign in to read the accounting periods.";
                return;
            }

            _canClose = session.Permissions.Contains(Permissions.AccountingClose, StringComparer.Ordinal);

            _periods = await Ledger.ListPeriodsAsync(_pending.Token).ConfigureAwait(false);
            _state = _periods.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            if (_periods.Count == 0)
            {
                _message = "This business has no accounting periods, so nothing can be posted. Open the first window below.";
                return;
            }

            await ReadAsync(_selected?.Id ?? _periods[0].Id, _pending.Token).ConfigureAwait(false);
            _selected = _periods.FirstOrDefault(period => period.Id == _selected?.Id) ?? _periods[0];
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = _state == ShellStateKind.Forbidden
                ? $"Reading the accounting periods needs the '{Permissions.ReportsView}' authority, which this account does not hold here."
                : AccountingRules.RefusalSentence(exception, "the periods");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so nothing was read and no period was closed.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }
    }

    private async Task ReadAsync(Guid periodId, CancellationToken cancellationToken = default)
    {
        try
        {
            _evidence = await Ledger.GetPeriodCloseEvidenceAsync(periodId, cancellationToken).ConfigureAwait(false);
            _selected = _evidence.Period;
            _reason = null;
            _close.Release();
            if (_yearNumber == 0)
            {
                // The year offered first is the one the selected period belongs to - a period on screen, not a clock
                // this screen read for itself (specification F21).
                _yearNumber = _evidence.Period.EndDate.Year;
            }
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "that period's close evidence");
        }
    }

    private async Task ReadYearAsync()
    {
        if (YearCandidate == 0)
        {
            _error = "Choose the year whose close you want to read.";
            return;
        }

        var fiscalYear = YearCandidate;
        _busy = true;
        _error = null;
        try
        {
            _yearEvidence = await Ledger.GetYearCloseEvidenceAsync(fiscalYear).ConfigureAwait(false);
            _year.Release();
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, $"the {FormatYear(fiscalYear)} year-end");
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task CreateAsync()
    {
        if (_start is not { } start || _end is not { } end)
        {
            _error = "A period is a window: it needs a first and a last day.";
            return;
        }

        var body = new SaveAccountingPeriodRequest(_name.Trim(), DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
        if (body.Name.Length == 0)
        {
            _error = "Name the period the way the ledger will read it, such as the month it covers.";
            return;
        }

        var key = _create.Key ??= AccountingRules.NewKey($"period-{body.StartDate:yyyyMM}", Guid.CreateVersion7());
        _create.Body = body;

        await SendAsync(_create, async () =>
        {
            var created = await Ledger.CreatePeriodAsync(body).ConfigureAwait(false);
            _name = string.Empty;
            _start = null;
            _end = null;
            await ReadAsync(created.Id).ConfigureAwait(false);
        }, $"the period {body.Name}").ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        if (_evidence is not { } evidence || string.IsNullOrWhiteSpace(_reason))
        {
            _error = "Closing a period records the reason an auditor will read.";
            return;
        }

        if (!AccountingRules.CanClose(evidence, _canClose))
        {
            _error = AccountingRules.CloseBlocker(evidence);
            return;
        }

        var periodId = evidence.Period.Id;
        var body = new CloseAccountingPeriodRequest(evidence.Period.Version, _reason.Trim());
        var key = _close.Key ??= AccountingRules.NewKey($"period-close-{periodId:N}", periodId);
        _close.Body = body;

        await SendAsync(_close, async () =>
        {
            await Ledger.ClosePeriodAsync(periodId, body, key).ConfigureAwait(false);
            await ReadAsync(periodId).ConfigureAwait(false);
        }, $"the close of {evidence.Period.Name}").ConfigureAwait(false);
    }

    private async Task CloseYearAsync()
    {
        if (_yearEvidence is null)
        {
            _error = "Read the year-end before transferring anything.";
            return;
        }

        var year = _yearEvidence;
        if (!AccountingRules.CanCloseYear(year, _canClose))
        {
            _error = AccountingRules.YearSentence(year);
            return;
        }

        var fiscalYear = year.FiscalYear;
        var key = _year.Key ??= AccountingRules.NewYearKey("fiscal-year-close", fiscalYear);

        await SendAsync(_year, async () =>
        {
            await Ledger.CloseYearAsync(fiscalYear, key).ConfigureAwait(false);
            _yearNumber = fiscalYear;
            await ReadYearAsync().ConfigureAwait(false);
            await ReloadPeriodsAsync().ConfigureAwait(false);
        }, $"the {FormatYear(fiscalYear)} year close").ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one act under its held key. A lost answer keeps key and body, so the button asks the server about the
    /// same act instead of closing a period twice or transferring a year's result twice (specification F04).
    /// </summary>
    private async Task SendAsync<TBody>(HeldIntention<TBody> intention, Func<Task> send, string what)
    {
        _busy = true;
        _error = null;
        _notice = null;
        try
        {
            await send().ConfigureAwait(false);
            _notice = $"{char.ToUpperInvariant(what[0])}{what[1..]} answered.";
            intention.Release();
        }
        catch (ApiException exception)
        {
            _error = exception.Errors.Count > 0
                ? $"{AccountingRules.RefusalSentence(exception, what)} {string.Join(' ', exception.Errors.Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}"))}"
                : AccountingRules.RefusalSentence(exception, what);
            if (intention.Key is not null && AccountingRules.CommittedNothing(exception.Code))
            {
                intention.Release();
            }
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task ReloadPeriodsAsync()
    {
        try
        {
            _periods = await Ledger.ListPeriodsAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "the periods re-read");
        }
    }

    private static string FormatYear(int year) => year.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string Money(decimal amount) => AccountingRules.Money(amount, Scale);

    private string EvidenceHeading
        => _evidence is not { } evidence
            ? "Read a period to see what its close would leave behind."
            : $"{evidence.Period.Name} · {evidence.Period.StartDate:yyyy-MM-dd} to {evidence.Period.EndDate:yyyy-MM-dd} · {AccountingRules.StatusText(evidence.Period.Status)}";
}
