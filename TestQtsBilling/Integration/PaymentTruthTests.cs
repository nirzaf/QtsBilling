using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

using PaymentMethod = QtsBilling.Persistence.Postgres.PaymentMethod;
using PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus;
using InvoiceStatus = QtsBilling.Persistence.Postgres.InvoiceStatus;
using JournalSourceType = QtsBilling.Persistence.Postgres.JournalSourceType;

/// <summary>
/// The payment truth of a posted sale (specification task 13, T13.01 - T13.05).
/// </summary>
/// <remarks>
/// A tender is only settled when the money was actually acknowledged. A customer-credit sale is
/// financed, and a cheque or a transfer is not bank money until an operator confirms it: neither may
/// be reported as collected money.
/// </remarks>
public sealed class PaymentTruthTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PaymentTruthTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T13_01_a_cash_sale_is_settled_and_balances()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var saleId = await PostSaleAsync(client, "Cash", 115m, customerId: null);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == saleId);
            var payments = await db.Payments.AsNoTracking().Where(payment => payment.SaleId == saleId).ToListAsync();

            Assert.Equal(PaymentStatus.Confirmed, sale.PaymentStatus);
            var payment = Assert.Single(payments);
            Assert.Equal(PaymentStatus.Confirmed, payment.Status);
            Assert.Equal(PaymentMethod.Cash, payment.Method);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
            await FinancialAssertions.AssertSaleMatchesItsLinesAsync(db, saleId);
        });
    }

    [Fact]
    public async Task T13_02_a_customer_credit_sale_creates_a_receivable_and_no_collected_money()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);

        var saleId = await PostSaleAsync(client, "CustomerCredit", 115m, customerId);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == saleId);
            var payments = await db.Payments.AsNoTracking().Where(payment => payment.SaleId == saleId).ToListAsync();

            // The money was not collected: the sale is unsettled.
            Assert.Equal(PaymentStatus.Pending, sale.PaymentStatus);
            Assert.All(payments, payment => Assert.Equal(PaymentStatus.Pending, payment.Status));

            // No confirmed payment exists for the sale.
            Assert.DoesNotContain(payments, payment => payment.Status == PaymentStatus.Confirmed);

            // The receivable carries the amount of this sale: the entry debits the receivable account.
            var receivable = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1100");

            var receivableAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId
                    && line.AccountId == receivable.Id
                    && line.JournalEntryId == db.JournalEntries.AsNoTracking()
                        .Where(entry => entry.SourceId == saleId)
                        .Select(entry => entry.Id)
                        .First())
                .SumAsync(line => line.DebitAmount - line.CreditAmount);

            Assert.Equal(115m, receivableAmount);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task T13_03_a_cheque_is_not_bank_money_until_it_is_confirmed()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);

        var saleId = await PostSaleAsync(client, "Cheque", 115m, customerId, reference: "cheque-4711");
        var paymentId = await _fixture.WithFreshContextAsync(async db => await db.Payments.AsNoTracking()
            .Where(payment => payment.SaleId == saleId)
            .Select(payment => payment.Id)
            .SingleAsync());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == saleId);
            var payments = await db.Payments.AsNoTracking().Where(payment => payment.SaleId == saleId).ToListAsync();

            // A reference string is evidence for the review, not a proof that the money arrived.
            Assert.Equal(PaymentStatus.Pending, sale.PaymentStatus);
            var payment = Assert.Single(payments);
            Assert.Equal(PaymentStatus.Pending, payment.Status);

            // The amount is not in the bank yet.
            var bank = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1010");

            var bankAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == bank.Id)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);

            Assert.Equal(0m, bankAmount);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });

        var bankBefore = await _fixture.WithFreshContextAsync(async db =>
        {
            var bank = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1010");
            return await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == bank.Id)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);
        });

        var key = "clear-cheque-" + Guid.NewGuid().ToString("N");
        var cleared = await PostPaymentActionAsync(client, paymentId, "clear", key);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var clearedBody = System.Text.Json.JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.Equal("Confirmed", clearedBody.RootElement.GetProperty("status").GetString());

        var replay = await PostPaymentActionAsync(client, paymentId, "clear", key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = System.Text.Json.JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(clearedBody.RootElement.GetProperty("paymentId").GetGuid(), replayBody.RootElement.GetProperty("paymentId").GetGuid());

        var duplicateClear = await PostPaymentActionAsync(client, paymentId, "clear", "clear-again-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Conflict, duplicateClear.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.Id == paymentId);
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.Id == saleId);
            var bank = await db.Accounts.AsNoTracking().FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1010");
            var bankAfter = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == bank.Id)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);
            var paymentEntryCount = await db.JournalEntries.AsNoTracking()
                .CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                    && entry.SourceType == JournalSourceType.Payment
                    && entry.SourceId == paymentId);

            Assert.Equal(PaymentStatus.Confirmed, payment.Status);
            Assert.NotNull(payment.ClearedAtUtc);
            Assert.Equal(PaymentStatus.Confirmed, sale.PaymentStatus);
            Assert.Equal(bankBefore + 115m, bankAfter);
            Assert.Equal(1, paymentEntryCount);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task A_bounced_cheque_is_durable_and_never_becomes_collected_money()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);
        var saleId = await PostSaleAsync(client, "Cheque", 115m, customerId, reference: "cheque-returned");
        var paymentId = await _fixture.WithFreshContextAsync(async db => await db.Payments.AsNoTracking()
            .Where(payment => payment.SaleId == saleId)
            .Select(payment => payment.Id)
            .SingleAsync());

        var key = "bounce-cheque-" + Guid.NewGuid().ToString("N");
        var bounced = await PostPaymentActionAsync(client, paymentId, "bounce", key, "Returned by bank");
        Assert.Equal(HttpStatusCode.OK, bounced.StatusCode);
        using var bouncedBody = System.Text.Json.JsonDocument.Parse(await bounced.Content.ReadAsStringAsync());
        Assert.Equal("Bounced", bouncedBody.RootElement.GetProperty("status").GetString());

        var replay = await PostPaymentActionAsync(client, paymentId, "bounce", key, "Returned by bank");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.Id == paymentId);
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.Id == saleId);
            var paymentEntries = await db.JournalEntries.AsNoTracking()
                .CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                    && entry.SourceType == JournalSourceType.Payment
                    && entry.SourceId == paymentId);

            Assert.Equal(PaymentStatus.Bounced, payment.Status);
            Assert.Equal("Returned by bank", payment.BounceReason);
            Assert.NotNull(payment.BouncedAtUtc);
            Assert.Equal(PaymentStatus.Bounced, sale.PaymentStatus);
            Assert.Equal(0, paymentEntries);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task A_customer_receipt_can_span_invoice_and_deposit_then_apply_the_deposit_once()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);
        var firstInvoiceId = await CreateAndIssueInvoiceAsync(client, customerId, "first customer receipt invoice");
        var secondInvoiceId = await CreateAndIssueInvoiceAsync(client, customerId, "second customer receipt invoice");

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/customers/{customerId}/payments")
        {
            Content = JsonContent.Create(new
            {
                method = "Cheque",
                amount = 125m,
                receivedDate = DateOnly.FromDateTime(DateTime.UtcNow),
                reference = "customer-cheque-advance",
                allocations = new[] { new { invoiceId = firstInvoiceId, amount = 100m } },
            }),
        };
        createRequest.Headers.Add("Idempotency-Key", "customer-receipt-" + Guid.NewGuid().ToString("N"));
        var createResponse = await client.SendAsync(createRequest);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.True(createResponse.StatusCode == HttpStatusCode.Created, createBody);
        using var created = System.Text.Json.JsonDocument.Parse(createBody);
        var paymentId = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Pending", created.RootElement.GetProperty("status").GetString());
        Assert.Equal(25m, created.RootElement.GetProperty("unallocatedAmount").GetDecimal());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var first = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == firstInvoiceId);
            Assert.Equal(0m, first.AmountPaid);
            Assert.Equal(115m, first.BalanceDue);
        });

        var clear = await PostPaymentActionAsync(client, paymentId, "clear", "customer-clear-" + Guid.NewGuid().ToString("N"));
        var clearBody = await clear.Content.ReadAsStringAsync();
        Assert.True(clear.StatusCode == HttpStatusCode.OK, clearBody);

        using var allocateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/{paymentId}/allocate")
        {
            Content = JsonContent.Create(new
            {
                allocations = new[] { new { invoiceId = secondInvoiceId, amount = 20m } },
            }),
        };
        allocateRequest.Headers.Add("Idempotency-Key", "customer-deposit-" + Guid.NewGuid().ToString("N"));
        var allocateResponse = await client.SendAsync(allocateRequest);
        var allocateBody = await allocateResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, allocateResponse.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(candidate => candidate.Id == paymentId);
            var first = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == firstInvoiceId);
            var second = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == secondInvoiceId);
            var allocationCount = await db.PaymentAllocations.CountAsync(allocation => allocation.PaymentId == paymentId);
            var paymentEntries = await db.JournalEntries.CountAsync(entry => entry.SourceId == paymentId && entry.SourceType == JournalSourceType.Payment);
            var depositAccount = await db.Accounts.AsNoTracking().SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "2105");
            var depositBalance = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == depositAccount.Id)
                .SumAsync(line => line.CreditAmount - line.DebitAmount);

            Assert.Equal(PaymentStatus.Confirmed, payment.Status);
            Assert.Equal(5m, payment.UnallocatedAmount);
            Assert.Equal(100m, first.AmountPaid);
            Assert.Equal(15m, first.BalanceDue);
            Assert.Equal(20m, second.AmountPaid);
            Assert.Equal(95m, second.BalanceDue);
            Assert.Equal(2, allocationCount);
            Assert.Equal(1, paymentEntries);
            Assert.Equal(5m, depositBalance);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });

        await ConfigureReportAccountingAsync();
        var agingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(35);
        var agingResponse = await client.GetAsync($"/api/v1/reports/ar-aging?asOf={agingDate:yyyy-MM-dd}");
        var agingBody = await agingResponse.Content.ReadAsStringAsync();
        Assert.True(agingResponse.StatusCode == HttpStatusCode.OK, agingBody);
        using var aging = System.Text.Json.JsonDocument.Parse(agingBody);
        var agingResult = aging.RootElement;
        Assert.True(agingResult.GetProperty("totalOpenReceivables").GetDecimal() >= 110m);
        Assert.True(agingResult.GetProperty("totalOpenReceivables").GetDecimal()
            == agingResult.GetProperty("ledgerReceivableBalance").GetDecimal(), agingBody);
        Assert.Equal(0m, agingResult.GetProperty("reconciliationVariance").GetDecimal());
        Assert.True(agingResult.GetProperty("reconciled").GetBoolean());
        var agedCustomer = agingResult.GetProperty("customers").EnumerateArray()
            .Single(row => row.GetProperty("customerId").GetGuid() == customerId);
        Assert.Equal(110m, agedCustomer.GetProperty("days31To60").GetDecimal());
    }

    [Fact]
    public async Task Credit_limit_blocks_pos_credit_and_invoice_issue_before_posting()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);
        await _fixture.WithFreshContextAsync(async db =>
        {
            var customer = await db.Customers.SingleAsync(row => row.Id == customerId);
            customer.CreditLimit = 100m;
            await db.SaveChangesAsync();
        });

        using var saleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                customerId,
                currency = "QAR",
                lines = new[]
                {
                    new { variantId = _fixture.BusinessA.VariantId, quantity = 1m, unitPrice = 100m, discountAmount = 0m, taxRate = 0.15m },
                },
                payment = new { method = "CustomerCredit", amount = 115m, cashReceived = (decimal?)null, reference = (string?)null },
                note = "credit limit test",
            }),
        };
        saleRequest.Headers.Add("Idempotency-Key", "credit-limit-sale-" + Guid.NewGuid().ToString("N"));
        var saleResponse = await client.SendAsync(saleRequest);
        var saleBody = await saleResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, saleResponse.StatusCode);
        Assert.Contains("CREDIT_LIMIT_EXCEEDED", saleBody, StringComparison.Ordinal);

        var issueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var draftResponse = await client.PostAsJsonAsync("/api/v1/invoices", new
        {
            locationId = _fixture.BusinessA.LocationId,
            customerId,
            issueDate,
            dueDate = (DateOnly?)null,
            note = "credit limit test invoice",
            lines = new[]
            {
                new { variantId = (Guid?)null, productName = "Credit limit test", quantity = 1m, unitPrice = 100m, discountAmount = 0m, taxRate = 0.15m },
            },
        });
        var draftBody = await draftResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        using var draft = System.Text.Json.JsonDocument.Parse(draftBody);
        var invoiceId = draft.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();

        var issueResponse = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issueResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, issueResponse.StatusCode);
        Assert.Contains("CREDIT_LIMIT_EXCEEDED", issueBody, StringComparison.Ordinal);

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(0, await db.Sales.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.CustomerId == customerId));
            Assert.Equal(InvoiceStatus.Draft, await db.Invoices.Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Id == invoiceId)
                .Select(row => row.Status).SingleAsync());
            Assert.False(await db.JournalEntries.AnyAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.SourceType == JournalSourceType.Invoice && row.SourceId == invoiceId));
        });
    }

    private async Task<Guid> CreateAndIssueInvoiceAsync(HttpClient client, Guid customerId, string note)
    {
        var response = await client.PostAsJsonAsync("/api/v1/invoices", new
        {
            locationId = _fixture.BusinessA.LocationId,
            customerId,
            issueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            note,
            lines = new[]
            {
                new
                {
                    variantId = (Guid?)null,
                    productName = "Consulting service",
                    quantity = 1m,
                    unitPrice = 100m,
                    discountAmount = 0m,
                    taxRate = 0.15m,
                },
            },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using var created = System.Text.Json.JsonDocument.Parse(body);
        var id = created.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issue = await client.PostAsync($"/api/v1/invoices/{id}/issue", content: null);
        Assert.True(issue.IsSuccessStatusCode, await issue.Content.ReadAsStringAsync());
        return id;
    }

    private Task ConfigureReportAccountingAsync()
        => _fixture.WithFreshContextAsync(async db =>
        {
            var accounts = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId)
                .ToDictionaryAsync(account => account.Code, account => account.Id);
            var requiredAccounts = new Dictionary<string, (string Name, QtsBilling.Persistence.Postgres.AccountType Type)>
            {
                ["1000"] = ("Cash", QtsBilling.Persistence.Postgres.AccountType.Asset),
                ["1010"] = ("Bank", QtsBilling.Persistence.Postgres.AccountType.Asset),
                ["1100"] = ("Accounts receivable", QtsBilling.Persistence.Postgres.AccountType.Asset),
                ["1200"] = ("Inventory", QtsBilling.Persistence.Postgres.AccountType.Asset),
                ["1300"] = ("Input VAT", QtsBilling.Persistence.Postgres.AccountType.Asset),
                ["2000"] = ("Accounts payable", QtsBilling.Persistence.Postgres.AccountType.Liability),
                ["2100"] = ("Output VAT", QtsBilling.Persistence.Postgres.AccountType.Liability),
                ["2101"] = ("GRNI", QtsBilling.Persistence.Postgres.AccountType.Liability),
                ["2105"] = ("Customer deposits", QtsBilling.Persistence.Postgres.AccountType.Liability),
                ["3000"] = ("Retained earnings", QtsBilling.Persistence.Postgres.AccountType.Equity),
                ["4000"] = ("Sales", QtsBilling.Persistence.Postgres.AccountType.Revenue),
                ["4010"] = ("Sales returns", QtsBilling.Persistence.Postgres.AccountType.Revenue),
                ["5000"] = ("COGS", QtsBilling.Persistence.Postgres.AccountType.Expense),
                ["5150"] = ("Purchase price variance", QtsBilling.Persistence.Postgres.AccountType.Expense),
                ["5200"] = ("Inventory shrinkage", QtsBilling.Persistence.Postgres.AccountType.Expense),
                ["5300"] = ("Cash over short", QtsBilling.Persistence.Postgres.AccountType.Expense),
            };
            foreach (var (code, (name, type)) in requiredAccounts)
            {
                if (accounts.ContainsKey(code)) continue;
                var account = new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = code,
                    Name = name,
                    AccountType = type,
                };
                db.Accounts.Add(account);
                accounts[code] = account.Id;
            }
            await db.SaveChangesAsync();

            var settings = await db.BusinessAccountingSettings
                .SingleOrDefaultAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId);
            if (settings is null)
            {
                settings = new BusinessAccountingSettings { BusinessId = _fixture.BusinessA.BusinessId };
                db.BusinessAccountingSettings.Add(settings);
            }

            settings.CashAccountId = accounts["1000"];
            settings.BankClearingAccountId = accounts["1010"];
            settings.AccountsReceivableAccountId = accounts["1100"];
            settings.InventoryAssetAccountId = accounts["1200"];
            settings.InputTaxReceivableAccountId = accounts["1300"];
            settings.AccountsPayableAccountId = accounts["2000"];
            settings.OutputTaxPayableAccountId = accounts["2100"];
            settings.GrniClearingAccountId = accounts["2101"];
            settings.CustomerDepositsAccountId = accounts["2105"];
            settings.RetainedEarningsAccountId = accounts["3000"];
            settings.SalesRevenueAccountId = accounts["4000"];
            settings.SalesReturnsAccountId = accounts["4010"];
            settings.CogsAccountId = accounts["5000"];
            settings.PurchasePriceVarianceAccountId = accounts["5150"];
            settings.InventoryShrinkageAccountId = accounts["5200"];
            settings.CashOverShortAccountId = accounts["5300"];
            await db.SaveChangesAsync();
        });

    private async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var email = $"credit-{Guid.NewGuid():N}@fixture.test";
        var phone = $"+9745{Guid.NewGuid():N}".Substring(0, 12);

        var response = await client.PostAsJsonAsync(
            "/api/v1/customers",
            new { name = "Fixture credit customer", email, phone });

        var customerBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"The customer could not be created with {(int)response.StatusCode}: {customerBody}");

        var body = await response.Content.ReadFromJsonAsync<CustomerResponse>();

        Assert.NotNull(body);

        return body!.Id;
    }

    private async Task<Guid> PostSaleAsync(HttpClient client, string method, decimal amount, Guid? customerId, string? reference = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                customerId,
                currency = "QAR",
                lines = new[]
                {
                    new
                    {
                        variantId = _fixture.BusinessA.VariantId,
                        quantity = 1,
                        unitPrice = 100m,
                        discountAmount = 0m,
                        taxRate = 0.15m,
                    },
                },
                payment = new { method, amount, cashReceived = amount, reference },
                note = $"payment truth {method}",
            }),
        };

        request.Headers.Add("Idempotency-Key", "t13-" + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"The sale was refused with {(int)response.StatusCode}: {body}");

        var sale = System.Text.Json.JsonSerializer.Deserialize<SaleResponse>(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(sale);

        return sale!.SaleId;
    }

    private static async Task<HttpResponseMessage> PostPaymentActionAsync(
        HttpClient client,
        Guid paymentId,
        string action,
        string idempotencyKey,
        string? reason = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/{paymentId}/{action}");
        if (reason is not null)
        {
            request.Content = JsonContent.Create(new { reason });
        }
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private sealed record CustomerResponse(Guid Id);

    private sealed record SaleResponse(Guid SaleId);
}
