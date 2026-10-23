using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Reports;
using QtsBilling.Persistence.Postgres;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Reports that can be trusted with the daily numbers (specification task 33, cases T33.01 - T33.04). The statements
/// are read through the same typed client the screens use, over the real server and a real PostgreSQL database, and
/// each one is tied back to the posted facts it claims to summarise: an aging run answers for the date it names and
/// not for today, subledger totals agree with the control accounts they roll into, an export keeps its numbers exact
/// and refuses to be executed as a spreadsheet formula, and a filter never reaches another business's books or an
/// unbounded window.
/// </summary>
public sealed class ReportTieOutTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public ReportTieOutTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T33_01_an_aging_run_answers_for_the_date_it_names_and_not_for_today()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var businessId = _fixture.BusinessB.BusinessId;
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, businessId));

        var reports = new ReportApiClient(http);
        var today = (await reports.GetBalanceSheetAsync()).AsOf;
        var twoDaysAgo = today.AddDays(-2);
        var yesterday = today.AddDays(-1);

        var customerId = await CreateCustomerAsync(http, "As-of customer");
        var invoiceId = await CreateIssuedInvoiceAsync(http, _fixture.BusinessB, customerId, twoDaysAgo, 100m);

        // At the day after it was issued the debt is open, and the control account says the same thing.
        var beforePayment = await reports.GetReceivableAgingAsync(yesterday);
        Assert.Equal(yesterday, beforePayment.AsOf);
        Assert.Equal(100m, beforePayment.TotalOpenReceivables);
        Assert.Equal(100m, beforePayment.LedgerReceivableBalance);
        Assert.Equal(0m, beforePayment.ReconciliationVariance);
        Assert.True(beforePayment.Reconciled, "a clean one-invoice ledger must tie without a hand in the middle");
        Assert.False(beforePayment.IncludesProvisional);
        var row = Assert.Single(beforePayment.Customers, item => item.CustomerId == customerId);
        Assert.Equal(100m, row.Days1To30 + row.Current);

        // The receipt arrives today. It settles what the customer owes now - it cannot reach backwards and erase the
        // balance that was genuinely open on the date the earlier run named.
        await PayInvoiceAsync(http, invoiceId, 100m);

        var historical = await reports.GetReceivableAgingAsync(yesterday);
        Assert.Equal(100m, historical.TotalOpenReceivables);
        Assert.Equal(100m, historical.LedgerReceivableBalance);
        Assert.Equal(0m, historical.ReconciliationVariance);

        var now = await reports.GetReceivableAgingAsync(today);
        Assert.Equal(0m, now.TotalOpenReceivables);
        Assert.Equal(0m, now.LedgerReceivableBalance);
        Assert.DoesNotContain(now.Customers, item => item.Balance > 0m);

        // A credit raised today is likewise absent from yesterday's answer, and present on its own day.
        var creditInvoiceId = await CreateIssuedInvoiceAsync(http, _fixture.BusinessB, customerId, yesterday, 60m);
        var creditLineId = await _fixture.WithFreshContextAsync(async db => await db.InvoiceLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.InvoiceId == creditInvoiceId)
            .Select(line => line.Id)
            .SingleAsync());
        using (var creditMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{creditInvoiceId:D}/credit-notes")
        {
            Content = JsonContent.Create(new InvoiceCreditNoteRequest(
                "returned goods, credited today", null, [new InvoiceCreditLineRequest(creditLineId, 0.5m)])),
        })
        {
            creditMessage.Headers.Add("Idempotency-Key", $"reports-credit-{Guid.NewGuid():N}");
            var credited = await http.SendAsync(creditMessage);
            var creditBody = await credited.Content.ReadAsStringAsync();
            Assert.True(credited.StatusCode == HttpStatusCode.Created, $"the credit note could not be raised: {(int)credited.StatusCode} {creditBody}");
        }

        var withCreditToday = await reports.GetReceivableAgingAsync(today);
        var withCreditYesterday = await reports.GetReceivableAgingAsync(yesterday);
        Assert.True(
            withCreditToday.TotalOpenReceivables < withCreditYesterday.TotalOpenReceivables,
            $"a credit issued today reduced nothing on today's run: today {withCreditToday.TotalOpenReceivables}, yesterday {withCreditYesterday.TotalOpenReceivables}");

        // An unresolved difference is shown as a difference, never quietly dropped from the total.
        var variance = await reports.GetReceivableAgingAsync(yesterday);
        Assert.Equal(
            variance.TotalOpenReceivables - variance.LedgerReceivableBalance,
            variance.ReconciliationVariance);
    }

    [Fact]
    public async Task T33_02_each_subledger_total_ties_to_the_control_account_it_rolls_into()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var businessId = _fixture.BusinessA.BusinessId;
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, businessId));

        var reports = new ReportApiClient(http);
        var today = (await reports.GetBalanceSheetAsync()).AsOf;

        // Wholesale side: an invoice half settled, so the receivable list and the AR account must both carry the
        // remainder and nothing else.
        var customerId = await CreateCustomerAsync(http, "Tie-out customer");
        var invoiceId = await CreateIssuedInvoiceAsync(http, _fixture.BusinessA, customerId, today.AddDays(-3), 240m);
        await PayInvoiceAsync(http, invoiceId, 90m);

        var aging = await reports.GetReceivableAgingAsync(today);
        Assert.Equal(150m, aging.TotalOpenReceivables);
        Assert.Equal(150m, aging.LedgerReceivableBalance);
        Assert.Equal(0m, aging.ReconciliationVariance);
        Assert.True(aging.Reconciled);

        // Stock: the report's own total is the sum of the branches it lists, and it says plainly that provisional
        // offline money is not in it.
        var stock = await reports.GetStockSummaryAsync(today);
        Assert.Equal(stock.Rows.Sum(row => row.StockValue), stock.TotalValue);
        Assert.False(stock.IncludesProvisional);

        // Cash and the statements: opening + movements - movements = closing, and the balance sheet balances.
        var cash = await reports.GetCashSummaryAsync(today.AddYears(-1), today);
        foreach (var account in cash.Accounts)
        {
            Assert.Equal(
                account.OpeningBalance + account.Debits - account.Credits,
                account.ClosingBalance);
        }

        Assert.Equal(cash.TotalClosing, cash.Accounts.Sum(account => account.ClosingBalance));

        var profit = await reports.GetProfitAndLossAsync(today.AddYears(-1), today);
        Assert.Equal(profit.TotalRevenue - profit.TotalExpenses, profit.NetProfit);

        var sheet = await reports.GetBalanceSheetAsync(today);
        Assert.True(sheet.Balanced, $"assets {sheet.TotalAssets} against liabilities {sheet.TotalLiabilities} and equity {sheet.TotalEquity} plus earnings {sheet.CurrentEarnings}");
        Assert.Equal(sheet.TotalLiabilities + sheet.TotalEquity, sheet.TotalLiabilitiesAndEquity);
        Assert.Equal(sheet.TotalAssets, sheet.TotalLiabilitiesAndEquity);
        Assert.Equal(businessId, sheet.BusinessId);

        // The AR list and the GL are the same money read two ways: one row of posted journal lines, one row of open
        // documents. Read them from the database and neither may disagree with the report.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var settings = await db.BusinessAccountingSettings.AsNoTracking().SingleAsync(row => row.BusinessId == businessId);
            var ledger = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == businessId && line.AccountId == settings.AccountsReceivableAccountId)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);
            Assert.Equal(150m, ledger);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, businessId);
        });
    }

    [Fact]
    public async Task T33_03_an_export_keeps_quantities_exact_and_refuses_to_be_a_spreadsheet_formula()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var businessId = _fixture.BusinessA.BusinessId;
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, businessId));

        // A name a spreadsheet would execute, and a document whose money and quantity must survive the file exactly.
        var dangerous = await CreateCustomerAsync(http, "=HYPERLINK(\"http://invalid.example\",\"paid\")");

        var reports = new ReportApiClient(http);
        var today = (await reports.GetBalanceSheetAsync()).AsOf;
        var invoiceId = await CreateIssuedInvoiceAsync(http, _fixture.BusinessA, dangerous, today.AddDays(-1), 33.33m, quantity: 1.5m);

        var export = await reports.ExportAsync(new ReportRequest(ReportKind.ReceivableAging, AsOf: today));
        var csv = Encoding.UTF8.GetString(export.Content);

        Assert.EndsWith(".csv", export.FileName, StringComparison.Ordinal);
        Assert.StartsWith("\ufeff", ExportBom(export.Content), StringComparison.Ordinal);
        Assert.Contains("Section,Item,Field,Label,Value", csv.Replace("\"", string.Empty), StringComparison.Ordinal);

        // The formula cell left the file neutralised: the guard character is in front of it, and no value column
        // begins with a character a spreadsheet would act on.
        var valueCells = csv
            .Split('\n')
            .Where(line => line.Contains("\"Value\"", StringComparison.Ordinal))
            .Select(line => line.Split(',')[^1].Trim('"', '\r'))
            .ToList();
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(valueCells, cell => cell.StartsWith("=", StringComparison.Ordinal)
            || cell.StartsWith("+", StringComparison.Ordinal)
            || cell.StartsWith("@", StringComparison.Ordinal));
        Assert.All(
            csv.Split('\n').Where(line => line.Contains("http", StringComparison.OrdinalIgnoreCase)).ToList(),
            line => Assert.Contains("'", line, StringComparison.Ordinal));

        // The exact money survives the file, and a quantity keeps the six decimals the movement column stores.
        Assert.Contains("33.33", csv, StringComparison.Ordinal);
        var stockCsv = Encoding.UTF8.GetString((await reports.ExportAsync(new ReportRequest(ReportKind.StockSummary, AsOf: today))).Content);
        Assert.Matches("OnHand\",\"On Hand\",\"[0-9]+\\.[0-9]{6}\"", stockCsv);

        // The machine column and the human label both travel in the file, invariant-culture numbers throughout.
        Assert.Contains("\"TotalOpenReceivables\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Total Open Receivables\"", csv, StringComparison.Ordinal);
        Assert.Contains(33.33m.ToString(CultureInfo.InvariantCulture), csv, StringComparison.Ordinal);

        // A half-settled document changes the total by exactly the money that was settled - and by nothing else, so
        // one receipt cannot quietly move a different customer's debt.
        var beforeTender = await reports.GetReceivableAgingAsync(today);
        await PayInvoiceAsync(http, invoiceId, 13.33m);
        var afterTender = await reports.GetReceivableAgingAsync(today);
        Assert.Equal(13.33m, beforeTender.TotalOpenReceivables - afterTender.TotalOpenReceivables);
        Assert.Equal(
            beforeTender.LedgerReceivableBalance - 13.33m,
            afterTender.LedgerReceivableBalance);
        Assert.Equal(afterTender.ReconciliationVariance, afterTender.TotalOpenReceivables - afterTender.LedgerReceivableBalance);
    }

    [Fact]
    public async Task T33_04_a_scope_or_size_control_refuses_without_saying_what_it_refused()
    {
        var ownerA = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ownerB = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, _fixture.BusinessA.BusinessId));
        await _fixture.WithFreshContextAsync(async db => await FixtureAccounting.EnsurePayablesAsync(db, _fixture.BusinessB.BusinessId));

        var reportsB = new ReportApiClient(ownerB);

        // What business B owed before business A raised a document, so the comparison below is about that document
        // and not about whatever else the two businesses already hold.
        var bBefore = await reportsB.GetReceivableAgingAsync();

        var customerId = await CreateCustomerAsync(ownerA, "Scoped customer A");
        await CreateIssuedInvoiceAsync(ownerA, _fixture.BusinessA, customerId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), 77m);

        // A different business, asked the same question, sees none of this.
        var agingB = await reportsB.GetReceivableAgingAsync();
        Assert.Equal(_fixture.BusinessB.BusinessId, agingB.BusinessId);
        Assert.DoesNotContain(agingB.Customers, row => row.CustomerName.Contains("Scoped customer A", StringComparison.Ordinal));
        Assert.Equal(bBefore.TotalOpenReceivables, agingB.TotalOpenReceivables);
        Assert.Equal(bBefore.Customers.Count, agingB.Customers.Count);

        // A location identifier that belongs to another business answers 404, not a name-bearing 403: the reader
        // learns nothing about whether the branch exists.
        var foreignLocation = _fixture.BusinessA.LocationId;
        var stockAcrossTenants = await ownerB.GetAsync(
            $"/api/v1/reports/stock-summary?locationId={foreignLocation:D}");
        Assert.Equal(HttpStatusCode.NotFound, stockAcrossTenants.StatusCode);
        var body = await stockAcrossTenants.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Scoped customer A", body, StringComparison.Ordinal);

        // An unbounded window is refused with the control named, rather than streamed for two centuries.
        var tooWide = await ownerA.GetAsync("/api/v1/reports/profit-and-loss?from=1900-01-01&to=2100-12-31");
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
        var wideBody = await tooWide.Content.ReadAsStringAsync();
        Assert.Contains("too wide", wideBody, StringComparison.OrdinalIgnoreCase);

        // Authority is read from the membership as it is now, not from the claims a cookie carries (D02, F07): once
        // the membership is revoked the reports read is refused and no figure travels in the answer.
        var viewer = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var viewerMembership = _fixture.Users.Viewer.MembershipId;
        await _fixture.WithFreshContextAsync(async db => await db.BusinessMemberships
            .Where(membership => membership.Id == viewerMembership)
            .ExecuteUpdateAsync(setters => setters.SetProperty(membership => membership.IsActive, false)));
        try
        {
            var refused = await viewer.GetAsync("/api/v1/reports/ar-aging");
            Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"the reports read answered {(int)refused.StatusCode} after the membership was revoked");
            var refusedBody = await refused.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Scoped customer A", refusedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("totalOpenReceivables", refusedBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _fixture.WithFreshContextAsync(async db => await db.BusinessMemberships
                .Where(membership => membership.Id == viewerMembership)
                .ExecuteUpdateAsync(setters => setters.SetProperty(membership => membership.IsActive, true)));
        }

        // The restored membership reads its own business again - on a freshly signed-in session, because both writes
        // bumped the membership's authorization revision and every cookie issued before that is stale (D02, F07).
        var reader = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var reread = await reader.GetAsync("/api/v1/reports/ar-aging");
        Assert.True(reread.IsSuccessStatusCode, $"the reports read answered {(int)reread.StatusCode} once the membership was restored and the session reissued");

        // The typed client keeps its own counsel about routes: a caller cannot ask for a path it invented.
        await Assert.ThrowsAsync<ArgumentException>(async () => await reportsB.ExportAsync(new ReportRequest(ReportKind.ZReport)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await reportsB.GetZReportAsync(Guid.Empty));
    }

    // ------------------------------------------------------------------ helpers

    private static string ExportBom(byte[] content)
        => content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? "\ufeff" : string.Empty;

    private static async Task<Guid> CreateCustomerAsync(HttpClient http, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var response = await http.PostAsJsonAsync("/api/v1/customers", new
        {
            name,
            email = $"reports-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the customer could not be created: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateIssuedInvoiceAsync(
        HttpClient http, FixtureBusiness business, Guid customerId, DateOnly issueDate, decimal total, decimal quantity = 1m)
    {
        // The document belongs to whichever business the client is signed into, so the branch and the stocked variant
        // come from that fixture business rather than from a guess.
        var request = new SaveInvoiceRequest(
            business.LocationId,
            customerId,
            issueDate,
            issueDate.AddDays(30),
            "reports tie-out invoice",
            [new InvoiceLineRequest(business.VariantId, null, quantity, total / quantity, 0m, 0m)],
            InvoiceRecognitionMode.DirectGoods);

        var create = await http.PostAsJsonAsync("/api/v1/invoices", request);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"invoice create failed: {(int)create.StatusCode} {createBody}");

        Guid invoiceId;
        using (var json = JsonDocument.Parse(createBody))
        {
            invoiceId = json.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        }

        var issue = await http.PostAsync($"/api/v1/invoices/{invoiceId:D}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.IsSuccessStatusCode, $"invoice issue failed: {(int)issue.StatusCode} {issueBody}");

        return invoiceId;
    }

    private static async Task PayInvoiceAsync(HttpClient http, Guid invoiceId, decimal amount)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId:D}/payments")
        {
            Content = JsonContent.Create(new { method = "Cash", amount, reference = "reports tie-out", allocations = Array.Empty<object>() }),
        };
        message.Headers.Add("Idempotency-Key", $"reports-{Guid.NewGuid():N}");
        var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"payment failed: {(int)response.StatusCode} {body}");
    }
}
