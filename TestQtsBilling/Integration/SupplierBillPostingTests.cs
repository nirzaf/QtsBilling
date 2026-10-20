using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class SupplierBillPostingTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public SupplierBillPostingTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Issued_bill_clears_grni_and_supplier_payments_reduce_the_open_balance_once()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var accounts = await ConfigureAccountingAsync();
        var supplierId = await CreateSupplierAsync(client);
        var (orderId, receiptLineId) = await ReceiveAsync(client, supplierId, 1m);
        var billId = await CreateBillAsync(client, supplierId, orderId, receiptLineId, 1m, 60.005m, "SB-TEST-ROUNDING");

        using var issue = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchasing/bills/{billId}/issue");
        issue.Headers.Add("Idempotency-Key", "supplier-bill-issue-" + Guid.NewGuid().ToString("N"));
        var issueResponse = await client.SendAsync(issue);
        var issueBody = await issueResponse.Content.ReadAsStringAsync();
        Assert.True(issueResponse.StatusCode == HttpStatusCode.OK, $"Bill issue failed with {(int)issueResponse.StatusCode}: {issueBody}");

        using var issuedJson = JsonDocument.Parse(issueBody);
        Assert.Equal("issued", issuedJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(60.01m, issuedJson.RootElement.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(0.005m, issuedJson.RootElement.GetProperty("roundingAmount").GetDecimal());
        var journalEntryId = issuedJson.RootElement.GetProperty("journalEntryId").GetGuid();

        await _fixture.WithFreshContextAsync(async db =>
        {
            var bill = await db.SupplierBills.AsNoTracking().SingleAsync(row => row.Id == billId);
            Assert.Equal(60.01m, bill.BalanceDue);
            Assert.Equal(0m, bill.AmountPaid);

            var lines = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.JournalEntryId == journalEntryId)
                .ToListAsync();
            Assert.Equal(60m, lines.Single(line => line.AccountId == accounts["2101"]).DebitAmount);
            Assert.Equal(0.005m, lines.Single(line => line.AccountId == accounts["5150"]).DebitAmount);
            Assert.Equal(0.005m, lines.Single(line => line.AccountId == accounts["5999"]).DebitAmount);
            Assert.Equal(60.01m, lines.Single(line => line.AccountId == accounts["2000"]).CreditAmount);
            Assert.Equal(lines.Sum(line => line.DebitAmount), lines.Sum(line => line.CreditAmount));

            await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supplier_bills SET total_amount = {60.02m} WHERE id = {billId}");
            });
            await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supplier_bill_lines SET quantity_billed = {2m} WHERE supplier_bill_id = {billId}");
            });
        });

        var paymentKey = "supplier-payment-" + Guid.NewGuid().ToString("N");
        var firstPayment = await PayAsync(client, supplierId, billId, 20m, paymentKey);
        var replay = await PayAsync(client, supplierId, billId, 20m, paymentKey);
        Assert.Equal(firstPayment, replay);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var bill = await db.SupplierBills.AsNoTracking().SingleAsync(row => row.Id == billId);
            Assert.Equal(SupplierBillStatus.PartiallyPaid, bill.Status);
            Assert.Equal(20m, bill.AmountPaid);
            Assert.Equal(40.01m, bill.BalanceDue);
            Assert.Equal(1, await db.SupplierPayments.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId));
            Assert.Equal(1, await db.SupplierPaymentAllocations.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId));
            Assert.Equal(1, await db.AuditEvents.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.EntityType == "supplier_bill" && row.EntityId == billId && row.Action == "created"));
            Assert.Equal(1, await db.AuditEvents.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.EntityType == "supplier_bill" && row.EntityId == billId && row.Action == "issued"));
            Assert.Equal(1, await db.AuditEvents.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.EntityType == "supplier_payment" && row.Action == "created"));
        });

        var agingDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var agingResponse = await client.GetAsync($"/api/v1/reports/ap-aging?asOf={agingDate:yyyy-MM-dd}");
        var agingBody = await agingResponse.Content.ReadAsStringAsync();
        Assert.True(agingResponse.StatusCode == HttpStatusCode.OK, agingBody);
        using var aging = JsonDocument.Parse(agingBody);
        var agingResult = aging.RootElement;
        var supplierRow = agingResult.GetProperty("suppliers").EnumerateArray()
            .Single(row => row.GetProperty("supplierId").GetGuid() == supplierId);
        Assert.Equal(40.01m, supplierRow.GetProperty("balance").GetDecimal());
        Assert.True(agingResult.GetProperty("reconciled").GetBoolean());
        Assert.Equal(agingResult.GetProperty("totalSupplierLiabilityExposure").GetDecimal(),
            agingResult.GetProperty("ledgerSupplierLiabilityExposure").GetDecimal());

        await PayAsync(client, supplierId, billId, 40.01m, "supplier-payment-" + Guid.NewGuid().ToString("N"));
        await _fixture.WithFreshContextAsync(async db =>
        {
            var bill = await db.SupplierBills.AsNoTracking().SingleAsync(row => row.Id == billId);
            Assert.Equal(SupplierBillStatus.Paid, bill.Status);
            Assert.Equal(60.01m, bill.AmountPaid);
            Assert.Equal(0m, bill.BalanceDue);
            Assert.Equal(2, await db.AuditEvents.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.EntityType == "supplier_payment" && row.Action == "created"));
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task A_receipt_quantity_cannot_be_billed_more_than_once()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await ConfigureAccountingAsync();
        var supplierId = await CreateSupplierAsync(client);
        var (orderId, receiptLineId) = await ReceiveAsync(client, supplierId, 10m);
        var firstBill = await CreateBillAsync(client, supplierId, orderId, receiptLineId, 10m, 60m, "SB-TEST-FULL");
        var secondBill = await CreateBillAsync(client, supplierId, orderId, receiptLineId, 1m, 60m, "SB-TEST-OVER");

        var firstResponse = await IssueAsync(client, firstBill);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var secondResponse = await IssueAsync(client, secondBill);
        var problem = await secondResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        Assert.Contains("OVER_RECEIPT", problem, StringComparison.Ordinal);

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(SupplierBillStatus.Issued, (await db.SupplierBills.AsNoTracking().SingleAsync(row => row.Id == firstBill)).Status);
            Assert.Equal(SupplierBillStatus.Draft, (await db.SupplierBills.AsNoTracking().SingleAsync(row => row.Id == secondBill)).Status);
            Assert.Equal(1, await db.JournalEntries.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.SourceType == QtsBilling.Persistence.Postgres.JournalSourceType.Purchase
                && (row.SourceId == firstBill || row.SourceId == secondBill)));
        });
    }

    [Fact]
    public async Task Inclusive_vat_bill_preserves_net_tax_and_payable_totals()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var accounts = await ConfigureAccountingAsync();
        var supplierId = await CreateSupplierAsync(client);
        var (orderId, receiptLineId) = await ReceiveAsync(client, supplierId, 1m);
        var billId = await CreateBillAsync(client, supplierId, orderId, receiptLineId, 1m, 115m,
            "SB-TEST-INCLUSIVE", taxRate: 0.15m, taxMode: "Inclusive");

        var response = await IssueAsync(client, billId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Bill issue failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var result = json.RootElement;
        Assert.Equal(115m, result.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(15m, result.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(100m, result.GetProperty("lines")[0].GetProperty("netAmount").GetDecimal());
        Assert.Equal(115m, result.GetProperty("lines")[0].GetProperty("lineTotal").GetDecimal());

        var entryId = result.GetProperty("journalEntryId").GetGuid();
        await _fixture.WithFreshContextAsync(async db =>
        {
            var journal = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.JournalEntryId == entryId)
                .ToListAsync();
            Assert.Equal(60m, journal.Single(line => line.AccountId == accounts["2101"]).DebitAmount);
            Assert.Equal(40m, journal.Single(line => line.AccountId == accounts["5150"]).DebitAmount);
            Assert.Equal(15m, journal.Single(line => line.AccountId == accounts["1300"]).DebitAmount);
            Assert.Equal(115m, journal.Single(line => line.AccountId == accounts["2000"]).CreditAmount);
        });
    }

    private async Task<Dictionary<string, Guid>> ConfigureAccountingAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var accountTypes = new Dictionary<string, (string Name, QtsBilling.Persistence.Postgres.AccountType Type)>
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
                ["5999"] = ("Rounding", QtsBilling.Persistence.Postgres.AccountType.Expense),
            };
            var accounts = await db.Accounts.Where(row => row.BusinessId == _fixture.BusinessA.BusinessId).ToDictionaryAsync(row => row.Code);
            foreach (var (code, (name, type)) in accountTypes)
            {
                if (!accounts.ContainsKey(code))
                {
                    var account = new Account { BusinessId = _fixture.BusinessA.BusinessId, Code = code, Name = name, AccountType = type };
                    db.Accounts.Add(account);
                    accounts[code] = account;
                }
            }
            await db.SaveChangesAsync();

            var settings = await db.BusinessAccountingSettings.SingleOrDefaultAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId);
            if (settings is null)
            {
                settings = new BusinessAccountingSettings { BusinessId = _fixture.BusinessA.BusinessId };
                db.BusinessAccountingSettings.Add(settings);
            }
            settings.SettlementScale = 2;
            settings.CashAccountId = accounts["1000"].Id;
            settings.BankClearingAccountId = accounts["1010"].Id;
            settings.AccountsReceivableAccountId = accounts["1100"].Id;
            settings.InventoryAssetAccountId = accounts["1200"].Id;
            settings.InputTaxReceivableAccountId = accounts["1300"].Id;
            settings.AccountsPayableAccountId = accounts["2000"].Id;
            settings.OutputTaxPayableAccountId = accounts["2100"].Id;
            settings.GrniClearingAccountId = accounts["2101"].Id;
            settings.CustomerDepositsAccountId = accounts["2105"].Id;
            settings.RetainedEarningsAccountId = accounts["3000"].Id;
            settings.SalesRevenueAccountId = accounts["4000"].Id;
            settings.SalesReturnsAccountId = accounts["4010"].Id;
            settings.CogsAccountId = accounts["5000"].Id;
            settings.PurchasePriceVarianceAccountId = accounts["5150"].Id;
            settings.InventoryShrinkageAccountId = accounts["5200"].Id;
            settings.CashOverShortAccountId = accounts["5300"].Id;
            settings.RoundingAccountId = accounts["5999"].Id;
            await db.SaveChangesAsync();
            return accounts.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.Ordinal);
        });

    private async Task<Guid> CreateSupplierAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/suppliers",
            new { name = $"Bill supplier {Guid.NewGuid():N}", email = $"bill-{Guid.NewGuid():N}@fixture.test" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Supplier creation failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<(Guid OrderId, Guid ReceiptLineId)> ReceiveAsync(HttpClient client, Guid supplierId, decimal quantity)
    {
        var orderResponse = await client.PostAsJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId,
            locationId = _fixture.BusinessA.LocationId,
            orderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            expectedDate = (DateOnly?)null,
            note = "supplier bill test",
            lines = new[] { new { variantId = _fixture.BusinessA.VariantId, quantity, unitCost = 60m, discountAmount = 0m, taxRate = 0m } },
        });
        var orderBody = await orderResponse.Content.ReadAsStringAsync();
        Assert.True(orderResponse.IsSuccessStatusCode, $"Purchase order creation failed with {(int)orderResponse.StatusCode}: {orderBody}");
        using var orderJson = JsonDocument.Parse(orderBody);
        var orderId = orderJson.RootElement.GetProperty("order").GetProperty("id").GetGuid();
        var orderLineId = orderJson.RootElement.GetProperty("lines")[0].GetProperty("id").GetGuid();

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/order") { Content = JsonContent.Create(new { }) };
        var confirmResponse = await client.SendAsync(confirm);
        var confirmBody = await confirmResponse.Content.ReadAsStringAsync();
        Assert.True(confirmResponse.IsSuccessStatusCode, $"Purchase order confirmation failed with {(int)confirmResponse.StatusCode}: {confirmBody}");

        using var receipt = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/receipts")
        {
            Content = JsonContent.Create(new { note = "supplier bill receipt", lines = new[] { new { purchaseOrderLineId = orderLineId, quantity } } }),
        };
        receipt.Headers.Add("Idempotency-Key", "supplier-receipt-" + Guid.NewGuid().ToString("N"));
        var receiptResponse = await client.SendAsync(receipt);
        var receiptBody = await receiptResponse.Content.ReadAsStringAsync();
        Assert.True(receiptResponse.StatusCode == HttpStatusCode.Created, $"Goods receipt failed with {(int)receiptResponse.StatusCode}: {receiptBody}");

        var receiptLineId = await _fixture.WithFreshContextAsync(db => db.PurchaseReceiptLines.AsNoTracking()
            .Where(line => line.PurchaseOrderLineId == orderLineId)
            .Select(line => line.Id).SingleAsync());
        return (orderId, receiptLineId);
    }

    private async Task<Guid> CreateBillAsync(HttpClient client, Guid supplierId, Guid orderId, Guid receiptLineId,
        decimal quantity, decimal cost, string reference, decimal taxRate = 0m, string taxMode = "Exclusive")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchasing/bills")
        {
            Content = JsonContent.Create(new
            {
                supplierId,
                locationId = _fixture.BusinessA.LocationId,
                purchaseOrderId = orderId,
                supplierInvoiceReference = reference,
                billDate = DateOnly.FromDateTime(DateTime.UtcNow),
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                lines = new[] { new { purchaseReceiptLineId = receiptLineId, quantityBilled = quantity, unitCost = cost, discountAmount = 0m, taxRate, taxMode } },
            }),
        };
        request.Headers.Add("Idempotency-Key", "supplier-bill-create-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Supplier bill creation failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PayAsync(HttpClient client, Guid supplierId, Guid billId, decimal amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchasing/supplier-payments")
        {
            Content = JsonContent.Create(new
            {
                supplierId,
                method = "Cash",
                amount,
                paidOn = DateOnly.FromDateTime(DateTime.UtcNow),
                currency = "QAR",
                reference = "fixture settlement",
                allocations = new[] { new { supplierBillId = billId, amount } },
            }),
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK,
            $"Supplier payment failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> IssueAsync(HttpClient client, Guid billId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchasing/bills/{billId}/issue");
        request.Headers.Add("Idempotency-Key", "supplier-bill-issue-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }
}
