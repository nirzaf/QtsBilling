using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Reports;

namespace QtsBilling.Ui.Pages.Reporting;

/// <summary>
/// What the report screen and the home dashboard are allowed to say (specification task 33). The figures are the
/// server's; this type names them, states the rule that makes them answerable, and turns a refusal into the next act.
/// It never adds, rounds away or re-derives a total: a variance is shown as a variance, provisional offline money is
/// announced rather than folded in, and a number that could not be read is said to be missing instead of zero.
/// </summary>
public static class ReportRules
{
    public const int MaxWindowDays = 3_660;

    public static readonly string AsOfRule =
        "A report answers for the date it names, not for today: only the events that had taken effect by that date are counted, measured in this business's own accounting zone.";

    public static readonly string TieOutRule =
        "Every statement is read from posted facts. Where the open-item list and the control account differ, the difference is shown and left for someone to answer - it is never adjusted away to make a total look clean.";

    public static readonly string ProvisionalRule =
        "Money captured on a device but not yet posted through the ledger is not in these figures. A report says plainly whether it includes provisional amounts, and these ones never do.";

    public static readonly string ExportRule =
        "The export is the same statement the screen shows, written to a file on this machine by the browser or the device - no BI service, no upload, no spreadsheet product, and no number rounded to fit a column.";

    public static readonly string ScopeRule =
        "A report covers one business and the branches this membership may read. A branch outside that scope is refused without saying whether it exists.";

    /// <summary>The statements a manager can ask for, in the words a manager uses.</summary>
    public static IReadOnlyList<ReportKind> Statements { get; } =
    [
        ReportKind.ReceivableAging,
        ReportKind.PayableAging,
        ReportKind.ProfitAndLoss,
        ReportKind.BalanceSheet,
        ReportKind.CashSummary,
        ReportKind.StockSummary,
        ReportKind.SalesSummary,
        ReportKind.TaxSummary,
    ];

    public static string Label(ReportKind kind) => kind switch
    {
        ReportKind.ReceivableAging => "Who owes the business, and how late",
        ReportKind.PayableAging => "What the business owes its suppliers",
        ReportKind.ProfitAndLoss => "Result of the window",
        ReportKind.BalanceSheet => "Position at the date",
        ReportKind.CashSummary => "Cash accounts over the window",
        ReportKind.StockSummary => "Stock on hand and what it is worth",
        ReportKind.SalesSummary => "Trade taken in the window",
        ReportKind.TaxSummary => "Tax collected and paid in the window",
        ReportKind.ZReport => "A register session's close",
        _ => kind.ToString(),
    };

    /// <summary>Whether a statement is asked over a single date or a window; the form offers one or the other.</summary>
    public static bool IsWindowed(ReportKind kind) => kind is ReportKind.ProfitAndLoss
        or ReportKind.CashSummary
        or ReportKind.SalesSummary
        or ReportKind.TaxSummary;

    public static bool TakesBranch(ReportKind kind) => kind == ReportKind.StockSummary;

    /// <summary>The window a form starts with: the trading month to date, on the business's own date.</summary>
    public static (DateOnly From, DateOnly To) DefaultWindow(DateOnly businessToday)
        => (new DateOnly(businessToday.Year, businessToday.Month, 1), businessToday);

    /// <summary>True when the window is short enough for the server to state honestly.</summary>
    public static bool WindowIsSane(DateOnly from, DateOnly to)
        => to >= from && to.DayNumber - from.DayNumber <= MaxWindowDays;

    public static string WindowWords(ReportKind kind, DateOnly? asOf, DateOnly? from, DateOnly? to)
        => IsWindowed(kind)
            ? from is { } start && to is { } end
                ? $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}"
                : "the server's own default window"
            : asOf is { } date
                ? $"as at {date:yyyy-MM-dd}"
                : "as at the business's own today";

    /// <summary>How late the oldest money owed is, in the words an overdue alert uses.</summary>
    public static string OverdueWords(decimal current, decimal days1To30, decimal days31To60, decimal days61To90, decimal daysOver90)
    {
        var overdue = days1To30 + days31To60 + days61To90 + daysOver90;
        if (overdue == 0m)
        {
            return current > 0m ? "Nothing is past its due date." : "Nothing is owed.";
        }

        var oldest = daysOver90 > 0m ? "over 90 days"
            : days61To90 > 0m ? "61 to 90 days"
            : days31To60 > 0m ? "31 to 60 days"
            : "1 to 30 days";
        return $"Some of it is as late as {oldest}.";
    }

    /// <summary>The reconciliation line: the list, the ledger, and the gap between them, or the fact there is none.</summary>
    public static string ReconciliationSentence(decimal openItems, decimal ledgerBalance, decimal variance, bool reconciled)
        => reconciled
            ? $"The list of open documents and the control account both say {Money(ledgerBalance)} - they were read from the same posted facts."
            : $"The open documents total {Money(openItems)} while the control account holds {Money(ledgerBalance)}. The difference of {Money(variance)} is shown as it stands: an unresolved difference is a task, not a rounding.";

    /// <summary>True when a balance sheet or a statement of the window no longer balances - a fact to act on.</summary>
    public static bool NeedsAttention(bool reconciled, bool balanced) => !reconciled || !balanced;

    public static string ProvisionalSentence(bool includesProvisional)
        => includesProvisional
            ? "This statement includes money captured on a device that the ledger has not posted yet."
            : "No provisional device money is counted here; only what the ledger has posted.";

    /// <summary>Money at the scale the business settles in, never at a scale this screen invented (F21).</summary>
    public static string Money(decimal amount) => Money(amount, 2);

    public static string Money(decimal amount, int scale)
        => amount.ToString("N" + Math.Clamp(scale, 0, 4).ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A stale figure is announced. A screen that lost its read says so instead of showing zeroes.</summary>
    public static string StaleSentence(string what, string? reason)
        => $"{what} could not be read just now{(string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}")} Nothing is shown in its place.";

    /// <summary>A refusal names the rule and the next act, not a status code.</summary>
    public static string RefusalSentence(ApiException exception, string what) => exception.Code switch
    {
        ApiErrorCodes.ValidationFailed =>
            $"'{what}' was not accepted as it stands: {exception.Message}",
        ApiErrorCodes.Conflict =>
            $"'{what}' needs the control accounts to be configured first: {exception.Message}",
        ApiErrorCodes.Forbidden =>
            $"'{what}' needs the reporting authority, which this account does not hold here.",
        ApiErrorCodes.NotFound =>
            $"'{what}' named something this business does not hold, so no figure was read and none was invented.",
        _ => $"'{what}' could not be confirmed: {exception.Message}. Nothing was assumed about the outcome.",
    };

    /// <summary>
    /// The export's own file name: the statement, the window it answers for, and the extension. A caller never types
    /// it, so a saved file says which figures it holds and no path can ride inside a name.
    /// </summary>
    public static string ExportName(ReportKind kind, DateOnly? asOf, DateOnly? from, DateOnly? to, Guid? locationId)
    {
        var window = IsWindowed(kind) && from is { } start && to is { } end
            ? $"{start:yyyyMMdd}-{end:yyyyMMdd}"
            : asOf is { } date
                ? date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                : "current";
        var branch = locationId is { } location ? $"-{location:N}"[..8] : string.Empty;
        var name = $"{Slug(kind)}-{window}{branch}.csv";
        return name.Length <= 120 ? name : name[^120..];
    }

    private static string Slug(ReportKind kind) => kind switch
    {
        ReportKind.ReceivableAging => "ar-aging",
        ReportKind.PayableAging => "ap-aging",
        ReportKind.TaxSummary => "tax-summary",
        ReportKind.BalanceSheet => "balance-sheet",
        ReportKind.ProfitAndLoss => "profit-and-loss",
        ReportKind.StockSummary => "stock-summary",
        ReportKind.CashSummary => "cash-summary",
        ReportKind.SalesSummary => "sales-summary",
        ReportKind.ZReport => "z-report",
        _ => "report",
    };

    /// <summary>
    /// The columns one statement is worth reading in, and the values beside them. Money is named at the business's
    /// settlement scale here rather than in the markup, so a screen and a CSV cannot round differently.
    /// </summary>
    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) Table(object report, int scale)
        => report switch
        {
            ReceivableAgingDto aging => (
                ["Customer", "Not yet due", "1 to 30", "31 to 60", "61 to 90", "Over 90", "Owed"],
                [.. aging.Customers.Select(row => new[]
                {
                    row.CustomerName, Money(row.Current, scale), Money(row.Days1To30, scale), Money(row.Days31To60, scale),
                    Money(row.Days61To90, scale), Money(row.DaysOver90, scale), Money(row.Balance, scale),
                })]),
            PayableAgingDto aging => (
                ["Supplier", "Not yet due", "1 to 30", "31 to 60", "61 to 90", "Over 90", "Owed"],
                [.. aging.Suppliers.Select(row => new[]
                {
                    row.SupplierName, Money(row.Current, scale), Money(row.Days1To30, scale), Money(row.Days31To60, scale),
                    Money(row.Days61To90, scale), Money(row.DaysOver90, scale), Money(row.Balance, scale),
                })]),
            ProfitAndLossDto profit => (
                ["Account", "Kind", "Amount"],
                [.. profit.Revenue.Select(row => new[] { $"{row.Code} {row.Name}", "revenue", Money(row.Amount, scale) })
                    .Concat(profit.Expenses.Select(row => new[] { $"{row.Code} {row.Name}", "expense", Money(row.Amount, scale) }))]),
            BalanceSheetDto sheet => (
                ["Account", "Side", "Amount"],
                [.. sheet.Assets.Select(row => new[] { $"{row.Code} {row.Name}", "asset", Money(row.Amount, scale) })
                    .Concat(sheet.Liabilities.Select(row => new[] { $"{row.Code} {row.Name}", "liability", Money(row.Amount, scale) }))
                    .Concat(sheet.Equity.Select(row => new[] { $"{row.Code} {row.Name}", "equity", Money(row.Amount, scale) }))]),
            CashSummaryDto cash => (
                ["Account", "Opening", "In", "Out", "Closing"],
                [.. cash.Accounts.Select(row => new[]
                {
                    $"{row.Code} {row.Name}", Money(row.OpeningBalance, scale), Money(row.Debits, scale),
                    Money(row.Credits, scale), Money(row.ClosingBalance, scale),
                })]),
            StockSummaryDto stock => (
                ["Branch", "Item", "SKU", "On hand (6 dp)", "Value", "Last movement"],
                [.. stock.Rows.Select(row => new[]
                {
                    row.LocationName, row.VariantName, row.Sku ?? "-",
                    row.OnHand.ToString("N6", System.Globalization.CultureInfo.InvariantCulture),
                    Money(row.StockValue, scale),
                    row.LastMovementAtUtc is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "-",
                })]),
            SalesSummaryDto sales => (
                ["Payment method", "Tenders", "Amount"],
                [.. sales.PaymentsByMethod.Select(row => new[] { row.Method, row.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Money(row.Amount, scale) })]),
            TaxSummaryDto tax => (
                ["Rate", "Mode", "Output tax", "Input tax", "Net payable"],
                [.. tax.Rates.Select(row => new[]
                {
                    row.TaxRate.ToString("N2", System.Globalization.CultureInfo.InvariantCulture), row.TaxMode,
                    Money(row.OutputTaxAmount, scale), Money(row.InputTaxAmount, scale), Money(row.NetTaxPayable, scale),
                })]),
            ZReportDto session => (
                ["Register", "State", "Sales", "Net sales", "Refunds", "Expected cash", "Counted", "Variance"],
                [[
                    session.RegisterName, session.SessionStatus,
                    session.SalesCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Money(session.NetSales, scale),
                    Money(session.RefundAmount, scale),
                    Money(session.ExpectedCash, scale),
                    session.CountedCash is { } counted ? Money(counted, scale) : "not counted",
                    session.Variance is { } variance ? Money(variance, scale) : "not counted",
                ]]),
            _ => ([], []),
        };

    /// <summary>The headline figures a statement carries, so a screen can name what it is without a second read.</summary>
    public static IReadOnlyList<(string Caption, string Figure)> Headlines(object report, int scale) => report switch
    {
        ReceivableAgingDto aging =>
        [
            ("Owed to the business", Money(aging.TotalOpenReceivables, scale)),
            ("Per the control account", Money(aging.LedgerReceivableBalance, scale)),
            ("Difference", Money(aging.ReconciliationVariance, scale)),
            ("As at", aging.AsOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        ],
        PayableAgingDto aging =>
        [
            ("Owed to suppliers", Money(aging.TotalOpenPayables, scale)),
            ("Per the control account", Money(aging.LedgerPayablesBalance, scale)),
            ("Goods received not invoiced", Money(aging.OpenGrniBalance, scale)),
            ("Total supplier exposure", Money(aging.TotalSupplierLiabilityExposure, scale)),
            ("Difference", Money(aging.ReconciliationVariance, scale)),
        ],
        ProfitAndLossDto profit =>
        [
            ("Revenue", Money(profit.TotalRevenue, scale)),
            ("Expenses", Money(profit.TotalExpenses, scale)),
            ("Result of the window", Money(profit.NetProfit, scale)),
            (profit.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " to "
                + profit.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), profit.Currency),
        ],
        BalanceSheetDto sheet =>
        [
            ("Assets", Money(sheet.TotalAssets, scale)),
            ("Liabilities", Money(sheet.TotalLiabilities, scale)),
            ("Equity", Money(sheet.TotalEquity, scale)),
            ("Earnings of the year", Money(sheet.CurrentEarnings, scale)),
            ("Balanced", sheet.Balanced ? "yes" : "no - which is a fact to answer"),
        ],
        CashSummaryDto cash =>
        [
            ("Opening", Money(cash.TotalOpening, scale)),
            ("In", Money(cash.TotalDebits, scale)),
            ("Out", Money(cash.TotalCredits, scale)),
            ("Closing", Money(cash.TotalClosing, scale)),
        ],
        StockSummaryDto stock =>
        [
            ("Lines", stock.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("On hand", stock.TotalQuantity.ToString("N6", System.Globalization.CultureInfo.InvariantCulture)),
            ("Value", Money(stock.TotalValue, scale)),
            ("As at", stock.AsOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        ],
        SalesSummaryDto sales =>
        [
            ("Tickets", sales.SalesCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Net of discounts", Money(sales.Total - sales.Tax, scale)),
            ("Tax", Money(sales.Tax, scale)),
            ("Total", Money(sales.Total, scale)),
        ],
        TaxSummaryDto tax =>
        [
            ("Output tax collected", Money(tax.TotalOutputTax, scale)),
            ("Input tax paid", Money(tax.TotalInputTax, scale)),
            ("Net payable", Money(tax.NetTaxPayable, scale)),
        ],
        ZReportDto session =>
        [
            ("Net sales", Money(session.NetSales, scale)),
            ("Refunds", Money(session.RefundAmount, scale)),
            ("Expected cash", Money(session.ExpectedCash, scale)),
            ("Counted", session.CountedCash is { } counted ? Money(counted, scale) : "not counted yet"),
            ("Variance", session.Variance is { } variance ? Money(variance, scale) : "not counted yet"),
        ],
        _ => [],
    };
}
