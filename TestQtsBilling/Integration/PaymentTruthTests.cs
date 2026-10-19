using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

using PaymentMethod = QtsBilling.Persistence.Postgres.PaymentMethod;
using PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus;

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
    }

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

    private sealed record CustomerResponse(Guid Id);

    private sealed record SaleResponse(Guid SaleId);
}
