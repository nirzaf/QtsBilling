using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Reports;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Reporting;

/// <summary>
/// The manager's report surface (specification task 33, recipe steps 1 and 3). One statement is chosen by name, over
/// a window or at a date, for the branches this membership may read; the figures come from the server's posted facts
/// and are shown with the control-account comparison beside them, so a difference is visible instead of smoothed
/// over. The export writes the same statement to a file on this machine and nothing else: no BI service, no upload,
/// and no figure rounded to fit a column.
/// </summary>
public partial class Reports : IAsyncDisposable
{
    [Inject] private IReportApiClient ReportsApi { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;
    [Inject] private IBusinessSetupApiClient BusinessApi { get; set; } = default!;
    [Inject] private IAccountingApiClient Ledger { get; set; } = default!;
    [Inject] private ILocalFileOutput Files { get; set; } = default!;

    private static readonly IReadOnlyList<ReferenceItem> AllBranches = [new ReferenceItem(Guid.Empty, "Every branch this account may read", null)];

    private ReportKind _kind = ReportKind.ReceivableAging;

    // The pickers hold the day the operator chose; a DateOnly is what the statement is asked for, so the conversion
    // happens here and nowhere else - no date is ever derived from this machine's clock (specification F21).
    private DateTime? _asOf;
    private DateTime? _from;
    private DateTime? _to;

    private DateOnly? AsOfDate => _asOf is { } value ? DateOnly.FromDateTime(value) : null;

    private DateOnly? FromDate => _from is { } value ? DateOnly.FromDateTime(value) : null;

    private DateOnly? ToDate => _to is { } value ? DateOnly.FromDateTime(value) : null;
    private ReferenceItem? _branch;
    private object? _report;
    private string? _reportCurrency;
    private int _scale = 2;
    private string? _scaleNote;
    private bool _reconciled = true;
    private bool _balanced = true;
    private bool _includesProvisional;
    private string? _reconciliation;
    private IReadOnlyList<string> _headers = [];
    private IReadOnlyList<IReadOnlyList<string>> _rows = [];
    private IReadOnlyList<(string Caption, string Figure)> _headlines = [];

    private string? _error;
    private string? _notice;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _mayRead;
    private bool _started;
    private bool _canChooseBranch;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private bool CanRead => _mayRead && !_busy;

    private bool CanExport => _report is not null && Files.CanSaveFiles && !_busy;

    private string KindLabel => ReportRules.Label(_kind);

    private string WindowWords => ReportRules.WindowWords(_kind, AsOfDate, FromDate, ToDate);

    protected override void OnInitialized() => _state = ShellStateKind.Loading;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await PrepareAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    /// <summary>
    /// Who is asking, which branches they may choose, and at what scale this business names money. Nothing here reads
    /// a figure: the statements are read only when the operator asks for one, so a page that is merely opened does
    /// not cost a report.
    /// </summary>
    private async Task PrepareAsync()
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
                _message = "Sign in to read the reports.";
                return;
            }

            _mayRead = session.Permissions.Contains(Permissions.ReportsView, StringComparer.Ordinal);
            if (!_mayRead)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading reports needs the '{Permissions.ReportsView}' authority, which this account does not hold here.";
                return;
            }

            var setup = await BusinessApi.GetBusinessSettingsAsync(_pending.Token).ConfigureAwait(false);
            var visible = setup.Locations.Where(row => row.IsActive).ToList();
            var allowed = session.AllowedLocationIds;
            IReadOnlyList<ReferenceItem> branches = [.. AllBranches.Concat(visible
                .Where(location => allowed.Count == 0 || allowed.Contains(location.Id))
                .Select(location => ReferenceItem.From(location.Id, location.Name, null)))];
            _canChooseBranch = branches.Count > 1;
            _branchChoices = branches;

            if (_branch is null || !_branchChoices.Any(item => item.Id == _branch.Id))
            {
                _branch = _branchChoices[0];
            }

            try
            {
                var settings = await Ledger.GetSettingsAsync(_pending.Token).ConfigureAwait(false);
                _scale = settings.SettlementScale;
                _scaleNote = null;
            }
            catch (ApiException exception)
            {
                // The money is still readable; it is named at two places with that stated, never silently.
                _scale = 2;
                _scaleNote = $"The settlement scale could not be read ({exception.Message}), so money is named at two decimal places and said so.";
            }

            _state = ShellStateKind.Empty;
            _message = "Choose the statement and the window, then read it. Nothing is fetched until you ask.";
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = ReportRules.RefusalSentence(exception, "the report setup");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so no statement was read.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }
    }

    private IReadOnlyList<ReferenceItem> _branchChoices = AllBranches;

    private void Choose(ReportKind kind)
    {
        _kind = kind;
        _report = null;
        _headers = [];
        _rows = [];
        _headlines = [];
        _reconciliation = null;
        _notice = null;
        _error = null;
        _state = ShellStateKind.Empty;
        _message = $"{ReportRules.Label(kind)} is ready to read.";
    }

    private async Task ReadAsync()
    {
        if (!_mayRead)
        {
            _error = $"Reading reports needs the '{Permissions.ReportsView}' authority.";
            return;
        }

        if (ReportRules.IsWindowed(_kind))
        {
            if (FromDate is not { } start || ToDate is not { } end)
            {
                _error = "A window is asked for over a first and a last day: choose both.";
                return;
            }

            if (!ReportRules.WindowIsSane(start, end))
            {
                _error = $"That window is too wide for a report to state honestly (the server holds {ReportRules.MaxWindowDays} days at most). Narrow it and read again.";
                return;
            }
        }

        var branch = _branch is { } chosen && chosen.Id != Guid.Empty ? chosen.Id : (Guid?)null;
        _busy = true;
        _error = null;
        _notice = null;
        _state = ShellStateKind.Loading;

        try
        {
            var (report, currency, reconciled, balanced, provisional, reconciliation) = _kind switch
            {
                ReportKind.ReceivableAging => await ReadAgingAsync(AsOfDate, branch, default).ConfigureAwait(false),
                ReportKind.PayableAging => await ReadPayableAgingAsync(AsOfDate, branch).ConfigureAwait(false),
                ReportKind.ProfitAndLoss => await ReadProfitAndLossAsync(FromDate, ToDate, branch).ConfigureAwait(false),
                ReportKind.BalanceSheet => await ReadBalanceSheetAsync(AsOfDate, branch).ConfigureAwait(false),
                ReportKind.CashSummary => await ReadCashSummaryAsync(FromDate, ToDate, branch).ConfigureAwait(false),
                ReportKind.StockSummary => await ReadStockAsync(AsOfDate, branch).ConfigureAwait(false),
                ReportKind.SalesSummary => await ReadSalesAsync(FromDate, ToDate, branch).ConfigureAwait(false),
                ReportKind.TaxSummary => await ReadTaxAsync(FromDate, ToDate, branch).ConfigureAwait(false),
                _ => (null, null, true, true, false, null),
            };

            if (report is null)
            {
                _state = ShellStateKind.Failure;
                _message = "No statement came back, so nothing is shown.";
                return;
            }

            _report = report;
            _reportCurrency = currency;
            _reconciled = reconciled;
            _balanced = balanced;
            _includesProvisional = provisional;
            _reconciliation = reconciliation;
            (_headers, _rows) = ReportRules.Table(report, _scale);
            _headlines = ReportRules.Headlines(report, _scale);
            _state = ShellStateKind.Content;
            _message = $"{ReportRules.Label(_kind)} · {ReportRules.WindowWords(_kind, AsOfDate, FromDate, ToDate)} · {currency ?? "the business's currency"}";
        }
        catch (ApiException exception)
        {
            _state = ShellStateKind.Failure;
            _message = ReportRules.RefusalSentence(exception, KindLabel);
            _error = _message;
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _error = "The business server could not be reached, so the statement was not read and nothing was estimated.";
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    /// <summary>The branches this membership may read, chosen by name - the "every branch" line is the server's own scope.</summary>
    private Task<IReadOnlyList<ReferenceItem>> SearchBranchesAsync(string term, CancellationToken cancellationToken)
    {
        var wanted = term?.Trim() ?? string.Empty;
        IReadOnlyList<ReferenceItem> matches = wanted.Length == 0
            ? _branchChoices
            : [.. _branchChoices.Where(item => item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        return Task.FromResult(matches);
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadAgingAsync(DateOnly? asOf, Guid? branch, CancellationToken _)
    {
        var aging = await ReportsApi.GetReceivableAgingAsync(asOf).ConfigureAwait(false);
        return (aging, aging.Currency, aging.Reconciled, true, aging.IncludesProvisional,
            ReportRules.ReconciliationSentence(aging.TotalOpenReceivables, aging.LedgerReceivableBalance, aging.ReconciliationVariance, aging.Reconciled));
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadPayableAgingAsync(DateOnly? asOf, Guid? branch)
    {
        var aging = await ReportsApi.GetPayableAgingAsync(asOf).ConfigureAwait(false);
        return (aging, aging.Currency, aging.Reconciled, true, aging.IncludesProvisional,
            ReportRules.ReconciliationSentence(aging.TotalSupplierLiabilityExposure, aging.LedgerSupplierLiabilityExposure, aging.ReconciliationVariance, aging.Reconciled));
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadProfitAndLossAsync(DateOnly? from, DateOnly? to, Guid? branch)
    {
        var profit = await ReportsApi.GetProfitAndLossAsync(from, to).ConfigureAwait(false);
        return (profit, profit.Currency, true, true, profit.IncludesProvisional,
            $"The result of {ReportRules.WindowWords(ReportKind.ProfitAndLoss, null, profit.From, profit.To)} is {ReportRules.Money(profit.NetProfit, _scale)} - revenue of {ReportRules.Money(profit.TotalRevenue, _scale)} against expense of {ReportRules.Money(profit.TotalExpenses, _scale)}.");
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadBalanceSheetAsync(DateOnly? asOf, Guid? branch)
    {
        var sheet = await ReportsApi.GetBalanceSheetAsync(asOf).ConfigureAwait(false);
        return (sheet, sheet.Currency, sheet.Balanced, sheet.Balanced, sheet.IncludesProvisional,
            sheet.Balanced
                ? $"Assets of {ReportRules.Money(sheet.TotalAssets, _scale)} equal liabilities, equity and the year's earnings - the statement balances as at {sheet.AsOf:yyyy-MM-dd}."
                : $"Assets of {ReportRules.Money(sheet.TotalAssets, _scale)} do not equal liabilities, equity and earnings of {ReportRules.Money(sheet.TotalLiabilitiesAndEquity, _scale)}. That is shown, not adjusted.");
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadCashSummaryAsync(DateOnly? from, DateOnly? to, Guid? branch)
    {
        var cash = await ReportsApi.GetCashSummaryAsync(from, to).ConfigureAwait(false);
        return (cash, cash.Currency, true, true, cash.IncludesProvisional,
            $"Cash moved from {ReportRules.Money(cash.TotalOpening, _scale)} to {ReportRules.Money(cash.TotalClosing, _scale)} over the window. This is a cash summary - it is not a balance sheet and not a result of the year.");
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadStockAsync(DateOnly? asOf, Guid? branch)
    {
        var stock = await ReportsApi.GetStockSummaryAsync(asOf, branch).ConfigureAwait(false);
        return (stock, stock.Currency, true, true, stock.IncludesProvisional,
            $"Stock is valued from the movements that put it there, at {ReportRules.WindowWords(ReportKind.StockSummary, stock.AsOf, null, null)}. Quantities keep six decimals; only money is named at the settlement scale.");
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadSalesAsync(DateOnly? from, DateOnly? to, Guid? branch)
    {
        var sales = await ReportsApi.GetSalesSummaryAsync(from, to).ConfigureAwait(false);
        return (sales, sales.Currency, true, true, sales.IncludesProvisional,
            $"{sales.SalesCount} posted tickets, {ReportRules.Money(sales.Total, _scale)} including {ReportRules.Money(sales.Tax, _scale)} of tax. Only confirmed tenders are counted here.");
    }

    private async Task<(object, string?, bool, bool, bool, string?)> ReadTaxAsync(DateOnly? from, DateOnly? to, Guid? branch)
    {
        var tax = await ReportsApi.GetTaxSummaryAsync(from, to).ConfigureAwait(false);
        return (tax, tax.Currency, true, true, tax.IncludesProvisional,
            $"Collected {ReportRules.Money(tax.TotalOutputTax, _scale)} and paid {ReportRules.Money(tax.TotalInputTax, _scale)}; the net figure to answer for is {ReportRules.Money(tax.NetTaxPayable, _scale)}.");
    }

    private async Task ExportAsync()
    {
        if (_report is null)
        {
            _error = "Read the statement before exporting it; a file of figures nobody read is not evidence.";
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var branch = _branch is { } chosen && chosen.Id != Guid.Empty ? chosen.Id : (Guid?)null;
            var windowed = ReportRules.IsWindowed(_kind);
            var export = await ReportsApi.ExportAsync(new ReportRequest(
                _kind, AsOfDate, windowed ? FromDate : null, windowed ? ToDate : null, branch)).ConfigureAwait(false);

            var file = new StoredFile(export.FileName, export.Content);
            if (await Files.SaveAsync(file).ConfigureAwait(false))
            {
                _notice = $"{file.FileName} was handed to this machine as a local file ({ReportRules.Money(file.Bytes / 1024m, 1)} KB). It is the same statement the screen shows, written by the same server code.";
            }
            else
            {
                _error = $"This device would not take {file.FileName}. The statement stays on screen; no file was claimed.";
            }
        }
        catch (ApiException exception)
        {
            _error = ReportRules.RefusalSentence(exception, "the export");
        }
        catch (ArgumentException exception)
        {
            _error = $"The export was refused before it was written: {exception.Message}";
        }
        catch (HttpRequestException)
        {
            _error = "The server could not be reached, so no file was written.";
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    /// <summary>Where an operator goes next from a figure that needs answering (bounded drill-down).</summary>
    private string? DrillHref => _kind switch
    {
        ReportKind.ReceivableAging => "finance/receivables",
        ReportKind.PayableAging => "finance/payables",
        ReportKind.StockSummary => "inventory/stock",
        ReportKind.SalesSummary => "sales",
        ReportKind.CashSummary or ReportKind.ProfitAndLoss or ReportKind.BalanceSheet or ReportKind.TaxSummary => "accounting/journals",
        _ => null,
    };

    private string DrillLabel => _kind switch
    {
        ReportKind.ReceivableAging => "Open the receivables workbench",
        ReportKind.PayableAging => "Open the payables workbench",
        ReportKind.StockSummary => "Read the stock at each branch",
        ReportKind.SalesSummary => "Read the sales behind these totals",
        _ => "Read the journal behind these figures",
    };
}
