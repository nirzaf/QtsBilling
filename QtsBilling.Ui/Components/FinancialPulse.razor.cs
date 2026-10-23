using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Reports;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Reporting;

namespace QtsBilling.Ui.Components;

/// <summary>
/// The manager's dashboard on the home shell (specification task 33, recipe step 4): a few figures a person can act
/// on this morning, each one read from a report and therefore tied to the posted facts behind it. Nothing here
/// counts rows and calls it a balance - if a report cannot be read, that card says so instead of showing zero, and
/// every figure carries the date it answers for. A role without the reporting authority sees the cards say that too.
/// </summary>
public partial class FinancialPulse : IAsyncDisposable
{
    [Inject] private IReportApiClient ReportsApi { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;

    private const int AlertLimit = 4;

    private readonly List<PulseFigure> _figures = [];
    private readonly List<PulseAlert> _alerts = [];
    private string? _asOfLabel;
    private string? _statedAt;
    private bool _loading = true;
    private bool _mayRead;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    /// <summary>One number on the dashboard: what it is, whose it is, and whether it can be trusted right now.</summary>
    public sealed record PulseFigure(string Caption, string Value, string Detail, bool Missing);

    /// <summary>Something that needs an act today, named with where to go and the authority that gets you there.</summary>
    public sealed record PulseAlert(string Sentence, string Href, string Label);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await ReadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    private async Task ReadAsync()
    {
        var version = ++_loadVersion;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _loading = true;
        _figures.Clear();
        _alerts.Clear();

        try
        {
            var session = await AccountsApi.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _mayRead = false;
                return;
            }

            _mayRead = session.Permissions.Contains(Permissions.ReportsView, StringComparer.Ordinal);
            if (!_mayRead)
            {
                return;
            }

            // Each card is read on its own: a statement that fails is missing, not zero, and never stops the others.
            var receivables = await ReadOneAsync(async () => await ReportsApi.GetReceivableAgingAsync(cancellationToken: _pending.Token).ConfigureAwait(false));
            var payables = await ReadOneAsync(async () => await ReportsApi.GetPayableAgingAsync(cancellationToken: _pending.Token).ConfigureAwait(false));
            var stock = await ReadOneAsync(async () => await ReportsApi.GetStockSummaryAsync(cancellationToken: _pending.Token).ConfigureAwait(false));
            var cash = await ReadOneAsync(async () => await ReportsApi.GetCashSummaryAsync(cancellationToken: _pending.Token).ConfigureAwait(false));
            var result = await ReadOneAsync(async () => await ReportsApi.GetProfitAndLossAsync(cancellationToken: _pending.Token).ConfigureAwait(false));

            if (receivables.Item1 is ReceivableAgingDto aging)
            {
                _asOfLabel = aging.AsOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                _figures.Add(new PulseFigure(
                    "Owed to the business", ReportRules.Money(aging.TotalOpenReceivables, 2),
                    $"{aging.Customers.Count(row => row.Balance > 0m)} customer(s) · ledger says {ReportRules.Money(aging.LedgerReceivableBalance, 2)}",
                    false));
                AddVariance("receivables", aging.TotalOpenReceivables, aging.LedgerReceivableBalance, aging.ReconciliationVariance, aging.Reconciled, "finance/receivables", "Answer the receivables");
                AddOverdue("customer", aging.Customers.Select(row => (row.CustomerName, Late: row.Days31To60 + row.Days61To90 + row.DaysOver90)), "finance/receivables", "Answer the receivables");
            }
            else
            {
                _figures.Add(Missing("Owed to the business", receivables.Item2));
            }

            if (payables.Item1 is PayableAgingDto owed)
            {
                _figures.Add(new PulseFigure(
                    "Owed to suppliers", ReportRules.Money(owed.TotalOpenPayables, 2),
                    $"supplier exposure {ReportRules.Money(owed.TotalSupplierLiabilityExposure, 2)} including goods received not invoiced",
                    false));
                AddVariance("payables", owed.TotalSupplierLiabilityExposure, owed.LedgerSupplierLiabilityExposure, owed.ReconciliationVariance, owed.Reconciled, "finance/payables", "Answer the payables");
                AddOverdue("supplier", owed.Suppliers.Select(row => (row.SupplierName, Late: row.Days31To60 + row.Days61To90 + row.DaysOver90)), "finance/payables", "Answer the payables");
            }
            else
            {
                _figures.Add(Missing("Owed to suppliers", payables.Item2));
            }

            if (result.Item1 is ProfitAndLossDto profit)
            {
                _figures.Add(new PulseFigure(
                    "Result of the window", ReportRules.Money(profit.NetProfit, 2),
                    $"revenue {ReportRules.Money(profit.TotalRevenue, 2)} over expense {ReportRules.Money(profit.TotalExpenses, 2)}, {profit.From:yyyy-MM-dd} to {profit.To:yyyy-MM-dd}",
                    false));
            }
            else
            {
                _figures.Add(Missing("Result of the window", result.Item2));
            }

            if (cash.Item1 is CashSummaryDto cashSummary)
            {
                _figures.Add(new PulseFigure(
                    "Cash per the ledger", ReportRules.Money(cashSummary.TotalClosing, 2),
                    $"opening {ReportRules.Money(cashSummary.TotalOpening, 2)}, in {ReportRules.Money(cashSummary.TotalDebits, 2)}, out {ReportRules.Money(cashSummary.TotalCredits, 2)}",
                    false));
                _statedAt = $"{cashSummary.From:yyyy-MM-dd} to {cashSummary.To:yyyy-MM-dd}";
            }
            else
            {
                _figures.Add(Missing("Cash per the ledger", cash.Item2));
            }

            if (stock.Item1 is StockSummaryDto stockSummary)
            {
                _figures.Add(new PulseFigure(
                    "Stock on hand", ReportRules.Money(stockSummary.TotalValue, 2),
                    $"{stockSummary.Rows.Count} line(s) across the branches this account may read",
                    false));
                var unvalued = stockSummary.Rows.Count(row => row.OnHand > 0m && row.StockValue == 0m);
                if (unvalued > 0)
                {
                    _alerts.Add(new PulseAlert(
                        $"{unvalued} stock line(s) hold quantity the ledger values at nothing - a costing to answer, not a rounding.",
                        "inventory/stock", "Read the stock"));
                }
            }
            else
            {
                _figures.Add(Missing("Stock on hand", stock.Item2));
            }
        }
        catch (ApiException exception)
        {
            _mayRead = false;
            _statedAt = ReportRules.RefusalSentence(exception, "the dashboard");
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
        }
        catch (HttpRequestException)
        {
            _statedAt = "The business server could not be reached, so no figure on this page is current.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
            }
        }
    }

    private static PulseFigure Missing(string caption, string? reason)
        => new(caption, "-", ReportRules.StaleSentence(caption, reason), true);

    private async Task<(object?, string?)> ReadOneAsync(Func<Task<object>> read)
    {
        try
        {
            return (await read().ConfigureAwait(false), null);
        }
        catch (ApiException exception)
        {
            return (null, exception.Message);
        }
    }

    private void AddVariance(string what, decimal openItems, decimal ledger, decimal variance, bool reconciled, string href, string label)
    {
        if (reconciled || variance == 0m)
        {
            return;
        }

        _alerts.Add(new PulseAlert(
            $"{char.ToUpperInvariant(what[0])}{what[1..]} do not tie: the documents say {ReportRules.Money(openItems, 2)} and the control account says {ReportRules.Money(ledger, 2)}, a difference of {ReportRules.Money(variance, 2)}.",
            href,
            label));
    }

    private void AddOverdue(string noun, IEnumerable<(string Name, decimal Late)> rows, string href, string label)
    {
        var late = rows.Where(row => row.Late > 0m).OrderByDescending(row => row.Late).Take(AlertLimit).ToList();
        if (late.Count == 0)
        {
            return;
        }

        var named = string.Join(", ", late.Select(row => $"{row.Name} ({ReportRules.Money(row.Late, 2)})"));
        var rest = rows.Count(row => row.Late > 0m) - late.Count;
        _alerts.Add(new PulseAlert(
            $"Over 30 days late, {noun}: {named}{(rest > 0 ? $" and {rest} more" : string.Empty)}.",
            href,
            label));
    }
}
