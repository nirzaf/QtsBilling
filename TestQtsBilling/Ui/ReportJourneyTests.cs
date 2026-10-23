using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Reports;
using QtsBilling.Ui.Components;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Reporting;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The report surface as a shipped screen (specification task 33, cases T33.01 - T33.04 from the manager's side): the
/// page and the dashboard exist in the shared library, every host that renders them can resolve what they inject, the
/// markup asks for no identifier a person could not know, and the statements behind them answer with the names, dates,
/// variances and file names an operator needs to decide anything. The tie-out arithmetic itself stays with
/// <see cref="ReportTieOutTests"/>; this file proves the screen is reachable, honest about what it can do, and unable
/// to reach another business's books - and that a local export is a bare file name on a machine, never a URL.
/// </summary>
public sealed class ReportJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public ReportJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T33_the_report_screen_and_the_dashboard_are_wired_into_every_host_that_shows_them()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains("reports", typeof(Reports)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));

        var row = Assert.Single(ShellNavigation.Routes, route => route.Href == "reports");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.Equal(Permissions.ReportsView, row.RequiredPermission);
        Assert.Null(row.ArrivalNote);

        var injected = new[] { typeof(Reports), typeof(FinancialPulse) }
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IReportApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);
        Assert.Contains(typeof(ILocalFileOutput), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the reports screen but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs", "QtsWebApp/QtsWebApp/Program.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The file bridge is a local-only asset of each host, declared by the page that uses it, and it holds no URL.
        foreach (var (asset, declaration) in new[]
        {
            ("QtsWebApp/QtsWebApp/wwwroot/file-output.js", "QtsWebApp/QtsWebApp/Components/App.razor"),
            ("QtsBilling.Maui/wwwroot/file-output.js", "QtsBilling.Maui/wwwroot/index.html"),
        })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root, asset));
            var host = await File.ReadAllTextAsync(Path.Combine(root, declaration));
            Assert.Contains("qtsFiles", script, StringComparison.Ordinal);
            Assert.Contains("file-output.js", host, StringComparison.Ordinal);
            Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
            Assert.DoesNotContain("XMLHttpRequest", script, StringComparison.Ordinal);
            Assert.DoesNotContain("http://", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", script, StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", script, StringComparison.Ordinal);
            Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        }

        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Reporting/Reports.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Reporting/Reports.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Reporting/ReportRules.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Components/FinancialPulse.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Components/FinancialPulse.razor.cs")));

        // No typed identifiers, no remote resource, no invented scale or clock, and no spreadsheet or chart library.
        foreach (var forbidden in new[]
        {
            "Business id", "Location id", "Session id", "Customer id", "Guid.Parse", "Guid.TryParse", "access_token",
            "http://", "https://", "ToString(\"N2\")", "DateTime.Today", "DateTime.UtcNow", "Environment.",
            "cdn.", "chart.js", "iframe", "window.open", "blazor.js", "fetch(",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        // Labels, focus and the acts: a keyboard user reaches the same three things an operator sees.
        foreach (var wanted in new[]
        {
            "Label=\"Statement\"", "Label=\"First day\"", "Label=\"Last day\"", "Label=\"Branch\"", "search by name",
            "data-qts-action=\"read-report\"", "data-qts-action=\"export-report\"", "data-qts-action=\"retry-read\"",
            "data-qts-action=\"drill-through\"", "data-qts-action=\"pulse-drill\"", "data-qts-region=\"ask\"",
            "data-qts-region=\"figures\"", "data-qts-region=\"pulse\"", "\"stale\"", "\"posted\"",
            "\"needs-attention\"", "\"tied\"", "role=\"alert\"", "role=\"status\"",
            "Save this statement as a local file", "Needs an answer today",
        })
        {
            Assert.Contains(wanted, markup, StringComparison.Ordinal);
        }

        // The rules the screens recite are the rules the server enforces.
        Assert.Contains("the date it names", ReportRules.AsOfRule, StringComparison.Ordinal);
        Assert.Contains("never adjusted away", ReportRules.TieOutRule, StringComparison.Ordinal);
        Assert.Contains("not in these figures", ReportRules.ProvisionalRule, StringComparison.Ordinal);
        Assert.Contains("no BI service", ReportRules.ExportRule, StringComparison.Ordinal);
        Assert.Contains("without saying whether it exists", ReportRules.ScopeRule, StringComparison.Ordinal);
    }

    [Fact]
    public void T33_the_screen_says_what_a_report_answers_for_and_never_invents_a_figure()
    {
        // The window a form starts with belongs to the business's own date, which the caller passes in - the screen
        // never reads a clock of its own (F21).
        var (from, to) = ReportRules.DefaultWindow(new DateOnly(2026, 10, 22));
        Assert.Equal(new DateOnly(2026, 10, 1), from);
        Assert.Equal(new DateOnly(2026, 10, 22), to);
        Assert.Equal("2026-10-01 to 2026-10-22", ReportRules.WindowWords(ReportKind.ProfitAndLoss, null, from, to));
        Assert.Equal("as at 2026-10-22", ReportRules.WindowWords(ReportKind.BalanceSheet, to, null, null));
        Assert.Contains("default window", ReportRules.WindowWords(ReportKind.CashSummary, null, null, null), StringComparison.Ordinal);

        // A variance is named as a difference to answer, and a tie is named as a tie.
        Assert.Contains("difference of 150.00", ReportRules.ReconciliationSentence(860m, 710m, 150m, reconciled: false), StringComparison.Ordinal);
        Assert.Contains("shown as it stands", ReportRules.ReconciliationSentence(860m, 710m, 150m, reconciled: false), StringComparison.Ordinal);
        Assert.DoesNotContain("difference", ReportRules.ReconciliationSentence(860m, 860m, 0m, reconciled: true), StringComparison.Ordinal);
        Assert.True(ReportRules.NeedsAttention(reconciled: false, balanced: true));
        Assert.True(ReportRules.NeedsAttention(reconciled: true, balanced: false));
        Assert.False(ReportRules.NeedsAttention(reconciled: true, balanced: true));

        // A missing figure is described, never zeroed, and provisional money is announced either way.
        Assert.Contains("Nothing is shown in its place", ReportRules.StaleSentence("Cash per the ledger", "offline"), StringComparison.Ordinal);
        Assert.Contains("not in these figures", ReportRules.ProvisionalRule, StringComparison.Ordinal);
        Assert.Contains("No provisional device money is counted here", ReportRules.ProvisionalSentence(false), StringComparison.Ordinal);
        Assert.Contains("has not posted yet", ReportRules.ProvisionalSentence(true), StringComparison.Ordinal);

        // Money at the business's own scale, quantities at six decimals, and the table follows the statement.
        Assert.Equal("1,234.50", ReportRules.Money(1_234.5m, 2));
        Assert.Equal("1,234.5000", ReportRules.Money(1_234.5m, 4));
        var aging = new ReceivableAgingDto(
            Guid.NewGuid(), new DateOnly(2026, 10, 22), "QAR",
            [new ReceivableAgingRowDto(Guid.NewGuid(), "Harbour Supplies", 250m, 400m, 120m, 0m, 90m, 860m)],
            860m, 710m, 150m, false, false);
        var (headers, rows) = ReportRules.Table(aging, 2);
        Assert.Equal(["Customer", "Not yet due", "1 to 30", "31 to 60", "61 to 90", "Over 90", "Owed"], headers);
        Assert.Equal("Harbour Supplies", rows.Single()[0]);
        Assert.Equal("860.00", rows.Single()[^1]);
        Assert.Contains(ReportRules.Headlines(aging, 2), line => line.Caption == "Difference" && line.Figure == "150.00");

        // Stock keeps its six decimals where the money keeps the settlement scale.
        var stock = new StockSummaryDto(
            Guid.NewGuid(), new DateOnly(2026, 10, 22), "QAR",
            [new StockSummaryRowDto(Guid.NewGuid(), "Main counter", Guid.NewGuid(), "Rice 25kg", "RICE-25", 12.5m, 1_000m, null)],
            12.5m, 1_000m, false);
        Assert.Equal("12.500000", ReportRules.Table(stock, 2).Rows.Single()[3]);
        Assert.Contains(ReportRules.Headlines(stock, 2), line => line.Caption == "On hand" && line.Figure == "12.500000");

        // How late is said plainly, including when nothing is late.
        Assert.Contains("as late as 61 to 90 days", ReportRules.OverdueWords(0m, 0m, 0m, 100m, 0m), StringComparison.Ordinal);
        Assert.Equal("Nothing is past its due date.", ReportRules.OverdueWords(100m, 0m, 0m, 0m, 0m));
        Assert.Equal("Nothing is owed.", ReportRules.OverdueWords(0m, 0m, 0m, 0m, 0m));

        // Every refusal the reporting routes answer with becomes the next act, never a status code.
        string Sentence(string code, string message = "refused") => ReportRules.RefusalSentence(
            new ApiException(400, code, message, new Dictionary<string, string[]>()), "the report");
        Assert.Contains("too wide", Sentence(ApiErrorCodes.ValidationFailed, "the window is too wide"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("control accounts", Sentence(ApiErrorCodes.Conflict), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not hold here", Sentence(ApiErrorCodes.Forbidden), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no figure was read", Sentence(ApiErrorCodes.NotFound), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was assumed", Sentence(ApiErrorCodes.InternalError), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T33_a_local_export_is_a_bare_file_name_and_bounded_bytes()
    {
        var csv = System.Text.Encoding.UTF8.GetBytes("\"Section\",\"Item\",\"Field\",\"Label\",\"Value\"\n");

        Assert.Equal("ar-aging-20261022.csv",
            ReportRules.ExportName(ReportKind.ReceivableAging, new DateOnly(2026, 10, 22), null, null, null));
        Assert.Equal("profit-and-loss-20261001-20261022.csv",
            ReportRules.ExportName(ReportKind.ProfitAndLoss, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 22), null));
        var named = ReportRules.ExportName(ReportKind.StockSummary, new DateOnly(2026, 10, 22), null, null, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.StartsWith("stock-summary-20261022-", named, StringComparison.Ordinal);
        Assert.EndsWith(".csv", named, StringComparison.Ordinal);

        // The file the contract accepts is the file a host may write; anything else is refused before a byte is sent.
        var file = new StoredFile(named, csv);
        Assert.Equal(named, file.FileName);
        Assert.True(file.Bytes > 0);

        foreach (var rejected in new[] { "../elsewhere.csv", "reports/2026.csv", @"C:\books.csv", "\\\\server\\share.csv", "books.xlsx", new string('a', 130) + ".csv" })
        {
            Assert.Throws<ArgumentException>(() => new StoredFile(rejected, csv));
        }

        Assert.Throws<ArgumentException>(() => new StoredFile("ar-aging.csv", []));
        Assert.Throws<ArgumentException>(() => new StoredFile("ar-aging.csv", new byte[StoredFile.MaxContentBytes + 1]));

        // The route a report is read from is composed inside the client alone: a caller can only name a statement,
        // a window and a branch, never a path - proved by recording the request the client itself makes.
        Assert.Equal("/api/v1/reports/ar-aging?format=csv", await PathOfAsync(new ReportRequest(ReportKind.ReceivableAging)));
        Assert.Equal("/api/v1/reports/ar-aging?format=csv&asOf=2026-10-22",
            await PathOfAsync(new ReportRequest(ReportKind.ReceivableAging, new DateOnly(2026, 10, 22))));
        Assert.Contains("from=2026-10-01", await PathOfAsync(new ReportRequest(
            ReportKind.TaxSummary, From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 22))), StringComparison.Ordinal);
        Assert.Contains("locationId=", await PathOfAsync(new ReportRequest(
            ReportKind.StockSummary, new DateOnly(2026, 10, 22), LocationId: Guid.Parse("11111111-1111-1111-1111-111111111111"))), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(async () => await PathOfAsync(new ReportRequest(ReportKind.ZReport)));
    }

    /// <summary>The path one request makes the client build, read back from a handler that records and answers.</summary>
    private static async Task<string> PathOfAsync(ReportRequest request)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://qts.internal/") };
        var api = new ReportApiClient(http);
        await api.ExportAsync(request);
        Assert.NotNull(handler.RequestPath);
        return handler.RequestPath!;
    }

    private sealed class RecordingHandler : System.Net.Http.HttpMessageHandler
    {
        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("\"Section\",\"Item\",\"Field\",\"Label\",\"Value\"\n")),
            };
            response.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"report.csv\"");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task T33_the_statements_answer_with_names_dates_and_the_variance_the_screen_shows()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, _fixture.BusinessA.BusinessId));
        var reports = new ReportApiClient(http);
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);

        var aging = await reports.GetReceivableAgingAsync(asOf);
        Assert.Equal(asOf, aging.AsOf);
        Assert.False(aging.IncludesProvisional);
        Assert.False(string.IsNullOrWhiteSpace(aging.Currency));
        Assert.Equal(aging.TotalOpenReceivables - aging.LedgerReceivableBalance, aging.ReconciliationVariance);
        Assert.All(aging.Customers, row => Assert.False(string.IsNullOrWhiteSpace(row.CustomerName)));
        var (headers, rows) = ReportRules.Table(aging, 2);
        Assert.Equal(headers.Count, rows.Count == 0 ? headers.Count : rows.Single().Count);

        // The screen's branch filter is the server's filter, and a branch outside the business is refused without
        // disclosing that it exists - which is what the ScopeRule promises on the page.
        var foreign = Guid.CreateVersion7();
        var refused = await Record.ExceptionAsync(async () => await new ReportApiClient(
            await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB))
            .GetStockSummaryAsync(asOf, foreign));
        var notFound = Assert.IsType<ApiException>(refused);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, notFound.Code);
        Assert.Contains("cross-business", notFound.Message, StringComparison.OrdinalIgnoreCase);

        // A statement that cannot be stated is refused with the control named, and the client keeps the sentence.
        var tooWide = await Record.ExceptionAsync(async () => await reports.GetProfitAndLossAsync(new DateOnly(1901, 1, 1), asOf));
        Assert.Equal(ApiErrorCodes.ValidationFailed, Assert.IsType<ApiException>(tooWide).Code);

        // An export is bytes and a name - no navigation and no token, and the name the server offered survives.
        var export = await reports.ExportAsync(new ReportRequest(ReportKind.ReceivableAging, asOf));
        Assert.EndsWith(".csv", export.FileName, StringComparison.Ordinal);
        Assert.True(export.Bytes > 0);
        var text = System.Text.Encoding.UTF8.GetString(export.Content);
        Assert.Contains("\"Label\"", text, StringComparison.Ordinal);
        Assert.Contains("TotalOpenReceivables", text, StringComparison.Ordinal);
        Assert.Contains("Total Open Receivables", text, StringComparison.Ordinal);
    }
}
