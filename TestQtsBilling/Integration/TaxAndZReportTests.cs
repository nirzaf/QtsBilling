using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class TaxAndZReportTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public TaxAndZReportTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tax_summary_uses_posted_sales_credit_notes_and_expense_tax_snapshots()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1300"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "1300",
                    Name = "Input tax receivable",
                    AccountType = AccountType.Asset,
                });
                await db.SaveChangesAsync();
            }
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
                await db.SaveChangesAsync();
            }
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        await PostSaleAsync(client, registerId: null, "Cash", null);
        var invoiceId = await CreateInvoiceAsync(client, today);
        var issue = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.IsSuccessStatusCode, $"Invoice issue failed with {(int)issue.StatusCode}: {issueBody}");

        using var creditMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId}/credit-notes")
        {
            Content = JsonContent.Create(new InvoiceCreditNoteRequest("Tax reporting credit", null)),
        };
        creditMessage.Headers.Add("Idempotency-Key", "tax-credit-" + Guid.NewGuid().ToString("N"));
        var creditResponse = await client.SendAsync(creditMessage);
        var creditBody = await creditResponse.Content.ReadAsStringAsync();
        Assert.True(creditResponse.StatusCode == HttpStatusCode.Created, $"Credit note failed with {(int)creditResponse.StatusCode}: {creditBody}");
        Assert.False(string.IsNullOrWhiteSpace(creditBody));

        var expenseRequest = new SaveExpenseRequest(
            ExpenseCategoryId: null,
            LocationId: _fixture.BusinessA.LocationId,
            SupplierId: null,
            Description: "Taxable office supplies",
            Amount: 115m,
            OccurredOn: today,
            PaidVia: "Cash",
            Reference: "TEST-TAX-EXPENSE",
            TaxRate: 0.15m,
            TaxAmount: 15m,
            TaxMode: "Inclusive");
        using var expenseMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/expenses")
        {
            Content = JsonContent.Create(expenseRequest),
        };
        expenseMessage.Headers.Add("Idempotency-Key", "expense-tax-" + Guid.NewGuid().ToString("N"));
        var expenseResponse = await client.SendAsync(expenseMessage);
        var expenseBody = await expenseResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, expenseResponse.StatusCode);
        using var expenseJson = JsonDocument.Parse(expenseBody);
        Assert.Equal(15m, expenseJson.RootElement.GetProperty("taxAmount").GetDecimal());
        Assert.Equal("Inclusive", expenseJson.RootElement.GetProperty("taxMode").GetString());

        var reportResponse = await client.GetAsync($"/api/v1/reports/tax-summary?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var reportBody = await reportResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        using var reportJson = JsonDocument.Parse(reportBody);
        var report = reportJson.RootElement;
        Assert.Equal(15m, report.GetProperty("totalOutputTax").GetDecimal());
        Assert.Equal(15m, report.GetProperty("totalInputTax").GetDecimal());
        Assert.Equal(0m, report.GetProperty("netTaxPayable").GetDecimal());

        var exclusive = report.GetProperty("rates").EnumerateArray()
            .Single(row => row.GetProperty("taxMode").GetString() == "Exclusive");
        var inclusive = report.GetProperty("rates").EnumerateArray()
            .Single(row => row.GetProperty("taxMode").GetString() == "Inclusive");
        Assert.Equal(15m, exclusive.GetProperty("outputTaxAmount").GetDecimal());
        Assert.Equal(0m, exclusive.GetProperty("inputTaxAmount").GetDecimal());
        Assert.Equal(0m, inclusive.GetProperty("outputTaxAmount").GetDecimal());
        Assert.Equal(15m, inclusive.GetProperty("inputTaxAmount").GetDecimal());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var expense = await db.Expenses.AsNoTracking().SingleAsync(row => row.Description == "Taxable office supplies");
            var entry = await db.JournalEntries.AsNoTracking().SingleAsync(row => row.SourceType == JournalSourceType.Expense && row.SourceId == expense.Id);
            var inputTaxAccount = await db.Accounts.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Code == "1300");
            var lines = await db.JournalLines.AsNoTracking().Where(line => line.JournalEntryId == entry.Id).ToListAsync();
            Assert.Equal(100m, lines.Single(line => line.AccountId != inputTaxAccount.Id && line.DebitAmount > 0m).DebitAmount);
            Assert.Equal(15m, lines.Single(line => line.AccountId == inputTaxAccount.Id).DebitAmount);
            Assert.Equal(115m, lines.Single(line => line.CreditAmount > 0m).CreditAmount);
        });
    }

    [Fact]
    public async Task Z_report_uses_drawer_movements_and_separates_unsettled_tenders()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var registerRequest = new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, "=Z report " + Guid.NewGuid().ToString("N"));
        using var createRegister = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/registers")
        {
            Content = JsonContent.Create(registerRequest),
        };
        createRegister.Headers.Add("Idempotency-Key", "register-z-" + Guid.NewGuid().ToString("N"));
        var registerResponse = await client.SendAsync(createRegister);
        var registerBody = await registerResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        using var registerJson = JsonDocument.Parse(registerBody);
        var registerId = registerJson.RootElement.GetProperty("id").GetGuid();

        var openResponse = await client.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId}/sessions",
            new OpenCashRegisterSessionRequest(50m, "opening float"));
        var openBody = await openResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, openResponse.StatusCode);
        using var openJson = JsonDocument.Parse(openBody);
        var sessionId = openJson.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        await PostSaleAsync(client, registerId, "Cash", null);
        await PostSaleAsync(client, registerId, "Cheque", "CHK-Z-REPORT");

        var paidIn = await client.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId}/sessions/{sessionId}/movements",
            new CashMovementRequest("cashIn", 10m, "Paid in for float correction"));
        Assert.Equal(HttpStatusCode.Created, paidIn.StatusCode);
        var paidOut = await client.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId}/sessions/{sessionId}/movements",
            new CashMovementRequest("cashOut", 5m, "Paid out for supplies"));
        Assert.Equal(HttpStatusCode.Created, paidOut.StatusCode);

        var closeResponse = await client.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId}/sessions/{sessionId}/close",
            new CloseCashRegisterSessionRequest(170m, "counted at close"));
        var closeBody = await closeResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(closeBody));

        var reportResponse = await client.GetAsync($"/api/v1/reports/z-report/{sessionId}");
        var reportBody = await reportResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        using var reportJson = JsonDocument.Parse(reportBody);
        var report = reportJson.RootElement;
        Assert.Equal(2, report.GetProperty("salesCount").GetInt32());
        Assert.Equal(200m, report.GetProperty("grossSales").GetDecimal());
        Assert.Equal(0m, report.GetProperty("discounts").GetDecimal());
        Assert.Equal(30m, report.GetProperty("tax").GetDecimal());
        Assert.Equal(200m, report.GetProperty("netSales").GetDecimal());
        Assert.Equal(230m, report.GetProperty("totalSales").GetDecimal());
        Assert.Equal(170m, report.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(170m, report.GetProperty("countedCash").GetDecimal());
        Assert.Equal(0m, report.GetProperty("variance").GetDecimal());
        Assert.Equal(10m, report.GetProperty("paidIn").GetDecimal());
        Assert.Equal(5m, report.GetProperty("paidOut").GetDecimal());

        var cashTender = report.GetProperty("salesByPaymentMethod").EnumerateArray().Single();
        Assert.Equal("Cash", cashTender.GetProperty("method").GetString());
        Assert.Equal(115m, cashTender.GetProperty("amount").GetDecimal());

        var csvResponse = await client.GetAsync($"/api/v1/reports/z-report/{sessionId}?format=csv");
        var csvBytes = await csvResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes[..3]);
        var csv = System.Text.Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("\"Section\",\"Item\",\"Field\",\"Value\"", csv, StringComparison.Ordinal);
        Assert.Contains("'=Z report", csv, StringComparison.Ordinal);
    }

    private async Task<Guid> CreateInvoiceAsync(HttpClient client, DateOnly issueDate)
    {
        var customerId = await CreateCustomerAsync(client);
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            CustomerId: customerId,
            IssueDate: issueDate,
            DueDate: null,
            Note: "tax summary source invoice",
            Lines:
            [
                new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 100m, 0m, 0.15m),
            ],
            RecognitionMode: InvoiceRecognitionMode.DirectGoods);
        var response = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Invoice creation failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Tax report customer",
            email = $"tax-report-{suffix}@fixture.test",
            phone = "+97450000001",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer creation failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostSaleAsync(HttpClient client, Guid? registerId, string method, string? reference)
    {
        var request = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            CustomerId: null,
            Currency: "QAR",
            Lines: [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
            Payment: new PaymentCaptureRequest(method, 115m, method == "Cash" ? 115m : null, reference),
            Note: "tax and register report test",
            RegisterId: registerId);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(request),
        };
        requestMessage.Headers.Add("Idempotency-Key", "sale-report-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(requestMessage);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("saleId").GetGuid();
    }
}
