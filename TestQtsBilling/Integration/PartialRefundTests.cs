using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
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
                && movement.ReferenceType == "refund_line" && movement.MovementType == MovementType.SaleReversal
                && db.RefundLines.Any(row => row.Id == movement.ReferenceId
                    && db.Refunds.Any(refund => refund.SaleId == saleId && refund.Id == row.RefundId))));
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
                && entry.SourceType == JournalSourceType.Refund
                && db.Refunds.Any(refund => refund.SaleId == saleId && refund.Id == entry.SourceId)));
            Assert.Equal(115m, await db.CashRegisterMovements.Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.SaleId == saleId && movement.MovementType == CashMovementType.Refund).SumAsync(movement => movement.Amount));
            Assert.Equal(0.4m, await db.InventoryMovements.Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.ReferenceType == "refund_line" && movement.MovementType == MovementType.SaleReversal
                && db.RefundLines.Any(row => row.Id == movement.ReferenceId
                    && db.Refunds.Any(refund => refund.SaleId == saleId && refund.Id == row.RefundId))).SumAsync(movement => movement.QuantityDelta));

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

    [Fact]
    public async Task A_refund_or_a_paid_out_the_drawer_cannot_cover_is_refused_and_commits_nothing()
    {
        // The edge task 21's recipe step 4 exposes: money may only leave a drawer that holds it. Expected cash is
        // derived from the session's own movements (specification D07), so a refund bigger than that is refused
        // before any row, journal or movement exists - and a counted shortage at close is never the substitute.
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var sales = new SaleApiClient(client);
        var registers = new CashRegisterApiClient(client);
        var registerId = await CreateOpenRegisterAsync(client);
        var session = await registers.GetCurrentSessionAsync(registerId);
        var sessionId = session.Session.Id;

        var posted = await sales.PostSaleAsync(new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
            new PaymentCaptureRequest("Cash", 115m, 115m, null),
            "drawer sufficiency test",
            registerId), "sufficiency-sale-" + Guid.NewGuid().ToString("N"));

        // 50 float + 115 cash in = 165. A paid-out of 140 is real money leaving, and it is allowed.
        await registers.AddMovementAsync(registerId, sessionId, new CashMovementRequest("cashOut", 140m, "rent paid"),
            "sufficiency-out-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(25m, (await registers.GetCurrentSessionAsync(registerId)).ExpectedCash);

        // The refund of 115 now exceeds what the till holds: refused with its own code, and nothing is written.
        var saleLineId = await _fixture.WithFreshContextAsync(async db => await db.SaleLines.AsNoTracking()
            .Where(line => line.SaleId == posted.SaleId).Select(line => line.Id).SingleAsync());

        var refused = await RefundAsync(client, posted.SaleId, registerId, saleLineId, 1m, RefundDisposition.Restock);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(ApiErrorCodes.InsufficientDrawerCash, refusedBody, StringComparison.Ordinal);

        Assert.Equal(0, await _fixture.WithFreshContextAsync(db => db.Refunds.AsNoTracking()
            .CountAsync(refund => refund.SaleId == posted.SaleId)));
        Assert.Equal(0, await _fixture.WithFreshContextAsync(db => db.CashRegisterMovements.AsNoTracking()
            .CountAsync(movement => movement.SaleId == posted.SaleId && movement.MovementType == CashMovementType.Refund)));
        Assert.Equal(0, await _fixture.WithFreshContextAsync(async db => await db.JournalEntries.AsNoTracking()
            .CountAsync(entry => entry.SourceType == JournalSourceType.Refund
                && db.Refunds.Any(refund => refund.SaleId == posted.SaleId && refund.Id == entry.SourceId))));
        Assert.Equal(25m, (await registers.GetCurrentSessionAsync(registerId)).ExpectedCash);
        Assert.Equal(SaleStatus.Posted, (await _fixture.WithFreshContextAsync(async db => await db.Sales.AsNoTracking()
            .SingleAsync(sale => sale.Id == posted.SaleId))).Status);

        // A paid-out beyond the cash is refused by the same rule, so the till cannot be taken below zero either.
        var overdrawn = await Assert.ThrowsAsync<ApiException>(() => registers.AddMovementAsync(
            registerId, sessionId, new CashMovementRequest("cashOut", 100m, "more than the till holds"),
            "sufficiency-over-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(409, overdrawn.StatusCode);
        Assert.Equal(ApiErrorCodes.InsufficientDrawerCash, overdrawn.Code);
        Assert.Equal(25m, (await registers.GetCurrentSessionAsync(registerId)).ExpectedCash);

        // The server's own position states the chosen drawer's cash, so the shortage is visible before a person
        // promises money; the authoritative refusal still comes from the command itself.
        var blocked = await sales.GetRefundPositionAsync(posted.SaleId);
        var ownDrawer = Assert.Single(blocked.Drawers, drawer => drawer.RegisterId == registerId);
        Assert.Equal(25m, ownDrawer.ExpectedCash);
        Assert.Equal(25m, ownDrawer.AvailableForRefund);
        Assert.True(ownDrawer.AvailableForRefund < blocked.RemainingRefundableAmount);

        // Cash taken in against real money makes the very same refund payable - the refusal was about the cash.
        await registers.AddMovementAsync(registerId, sessionId, new CashMovementRequest("cashIn", 120m, "float topped up"),
            "sufficiency-in-" + Guid.NewGuid().ToString("N"));

        var paidBack = await RefundAsync(client, posted.SaleId, registerId, saleLineId, 1m, RefundDisposition.Restock);
        var paidBody = await paidBack.Content.ReadAsStringAsync();
        Assert.True(paidBack.StatusCode == HttpStatusCode.Created, $"the refund was refused with {(int)paidBack.StatusCode}: {paidBody}");
        Assert.Equal(115m, JsonDocument.Parse(paidBody).RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(30m, (await registers.GetCurrentSessionAsync(registerId)).ExpectedCash);
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
        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pos/registers/{registerId}/sessions")
        {
            Content = JsonContent.Create(new OpenCashRegisterSessionRequest(50m, "partial refund test float")),
        };
        open.Headers.Add("Idempotency-Key", "partial-open-" + Guid.NewGuid().ToString("N"));
        var openResponse = await client.SendAsync(open);
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
