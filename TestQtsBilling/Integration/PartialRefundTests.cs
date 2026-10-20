using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using RefundDisposition = QtsBilling.Contracts.Sales.RefundDisposition;
using PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class PartialRefundTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PartialRefundTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Partial_cash_refunds_enforce_remaining_quantity_and_apply_disposition_accounting()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var registerId = await CreateOpenRegisterAsync(client);
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
            }
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "5200"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "5200",
                    Name = "Inventory shrinkage",
                    AccountType = AccountType.Expense,
                });
            }
            await db.SaveChangesAsync();
        });

        var saleRequest = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            CustomerId: null,
            Currency: "QAR",
            Lines: [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
            Payment: new PaymentCaptureRequest("Cash", 115m, 115m, null),
            Note: "partial refund integration test",
            RegisterId: registerId);
        using var saleMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(saleRequest),
        };
        saleMessage.Headers.Add("Idempotency-Key", "partial-sale-" + Guid.NewGuid().ToString("N"));
        var saleResponse = await client.SendAsync(saleMessage);
        var saleBody = await saleResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, saleResponse.StatusCode);
        using var saleJson = JsonDocument.Parse(saleBody);
        var saleId = saleJson.RootElement.GetProperty("saleId").GetGuid();
        var saleLineId = await _fixture.WithFreshContextAsync(async db =>
            await db.SaleLines.AsNoTracking().Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.SaleId == saleId)
                .Select(line => line.Id).SingleAsync());

        var first = await RefundAsync(client, saleId, registerId, saleLineId, 0.4m, RefundDisposition.Restock);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"First refund failed with {(int)first.StatusCode}: {firstBody}");
        using (var firstJson = JsonDocument.Parse(firstBody))
        {
            Assert.Equal(46m, firstJson.RootElement.GetProperty("amount").GetDecimal());
            Assert.Equal("Restock", firstJson.RootElement.GetProperty("lines")[0].GetProperty("disposition").GetString());
        }

        await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.Id == saleId);
            Assert.Equal(SaleStatus.Posted, sale.Status);
            Assert.Equal(PaymentStatus.Confirmed, sale.PaymentStatus);
            Assert.Equal(1, await db.InventoryMovements.CountAsync(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.ReferenceType == "refund_line" && movement.MovementType == MovementType.SaleReversal));
        });

        var tooMuch = await RefundAsync(client, saleId, registerId, saleLineId, 0.7m, RefundDisposition.Restock);
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        var second = await RefundAsync(client, saleId, registerId, saleLineId, 0.6m, RefundDisposition.DamagedWriteOff);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using (var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
        {
            Assert.Equal(69m, secondJson.RootElement.GetProperty("amount").GetDecimal());
            Assert.Equal("DamagedWriteOff", secondJson.RootElement.GetProperty("lines")[0].GetProperty("disposition").GetString());
        }

        var historyResponse = await client.GetAsync($"/api/v1/pos/sales/{saleId}/refunds");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var historyJson = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, historyJson.RootElement.GetArrayLength());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.Id == saleId);
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.SaleId == saleId);
            Assert.Equal(SaleStatus.Reversed, sale.Status);
            Assert.Equal(PaymentStatus.Refunded, sale.PaymentStatus);
            Assert.Equal(PaymentStatus.Refunded, payment.Status);
            Assert.Equal(2, await db.JournalEntries.CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                && entry.SourceType == JournalSourceType.Refund));
            Assert.Equal(115m, await db.CashRegisterMovements.Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.SaleId == saleId && movement.MovementType == CashMovementType.Refund).SumAsync(movement => movement.Amount));
            Assert.Equal(0.4m, await db.InventoryMovements.Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.ReferenceType == "refund_line" && movement.MovementType == MovementType.SaleReversal).SumAsync(movement => movement.QuantityDelta));

            var accountIds = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId && (account.Code == "5200" || account.Code == "5000"))
                .ToDictionaryAsync(account => account.Code, account => account.Id);
            var damagedEntry = await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.BusinessId == _fixture.BusinessA.BusinessId && entry.SourceType == JournalSourceType.Refund)
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .FirstAsync();
            var damagedLines = await db.JournalLines.AsNoTracking().Where(line => line.JournalEntryId == damagedEntry.Id).ToListAsync();
            Assert.Equal(0.6m * (await db.SaleLines.AsNoTracking().Where(line => line.Id == saleLineId).Select(line => line.UnitCostSnapshot).SingleAsync()),
                damagedLines.Single(line => line.AccountId == accountIds["5200"]).DebitAmount);
            Assert.Equal(damagedLines.Single(line => line.AccountId == accountIds["5200"]).DebitAmount,
                damagedLines.Single(line => line.AccountId == accountIds["5000"]).CreditAmount);
        });
    }

    private async Task<Guid> CreateOpenRegisterAsync(HttpClient client)
    {
        var registerRequest = new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, "partial-refund-" + Guid.NewGuid().ToString("N"));
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/registers")
        {
            Content = JsonContent.Create(registerRequest),
        };
        create.Headers.Add("Idempotency-Key", "partial-register-" + Guid.NewGuid().ToString("N"));
        var createResponse = await client.SendAsync(create);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var registerJson = JsonDocument.Parse(createBody);
        var registerId = registerJson.RootElement.GetProperty("id").GetGuid();
        var openResponse = await client.PostAsJsonAsync(
            $"/api/v1/pos/registers/{registerId}/sessions",
            new OpenCashRegisterSessionRequest(50m, "partial refund test float"));
        Assert.Equal(HttpStatusCode.Created, openResponse.StatusCode);
        return registerId;
    }

    private static async Task<HttpResponseMessage> RefundAsync(
        HttpClient client,
        Guid saleId,
        Guid registerId,
        Guid saleLineId,
        decimal quantity,
        RefundDisposition disposition)
    {
        var request = new RefundSaleRequest(
            "partial refund test",
            null,
            registerId,
            [new RefundLineRequest(saleLineId, quantity, disposition)]);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pos/sales/{saleId}/refund")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", "partial-refund-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(message);
    }
}
