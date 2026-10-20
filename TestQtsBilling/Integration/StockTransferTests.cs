using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>Valued, idempotent inter-location transfer posting and rollback behavior.</summary>
public sealed class StockTransferTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public StockTransferTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Dispatch_and_receipt_preserve_stock_value_and_post_balanced_transit_entries()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var destinationId = await CreateDestinationAsync();
        var request = new CreateStockTransferRequest(
            _fixture.BusinessA.LocationId,
            destinationId,
            [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 10m)],
            "Branch replenishment");
        var create = await SendCreateAsync(client, request, "transfer-workflow-1");
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = System.Text.Json.JsonSerializer.Deserialize<StockTransferDetailDto>(createBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal(StockTransferState.Draft, created.Status);

        var replay = await SendCreateAsync(client, request, "transfer-workflow-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<StockTransferDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(created.Id, replayed!.Id);

        var changedPayload = request with { Note = "Changed after submission" };
        var changedReplay = await SendCreateAsync(client, changedPayload, "transfer-workflow-1");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.Contains("SAME_KEY_DIFFERENT_PAYLOAD", await changedReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var dispatch = await client.PostAsync($"/api/v1/inventory/transfers/{created.Id:D}/dispatch", content: null);
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        var dispatched = await dispatch.Content.ReadFromJsonAsync<StockTransferDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(StockTransferState.Dispatched, dispatched!.Status);
        Assert.Equal(600m, dispatched.TotalValue);
        Assert.Equal(600m, dispatched.Lines.Single().ValueAtDispatch);
        Assert.NotNull(dispatched.DispatchJournalEntryId);

        // A repeated dispatch must return the original operation without consuming another 10 units.
        var dispatchReplay = await client.PostAsync($"/api/v1/inventory/transfers/{created.Id:D}/dispatch", content: null);
        Assert.Equal(HttpStatusCode.OK, dispatchReplay.StatusCode);

        var receive = await client.PostAsync($"/api/v1/inventory/transfers/{created.Id:D}/receive", content: null);
        Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
        var received = await receive.Content.ReadFromJsonAsync<StockTransferDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(StockTransferState.Received, received!.Status);
        Assert.Equal(600m, received.TotalValue);
        Assert.NotNull(received.ReceiveJournalEntryId);

        var receiveReplay = await client.PostAsync($"/api/v1/inventory/transfers/{created.Id:D}/receive", content: null);
        Assert.Equal(HttpStatusCode.OK, receiveReplay.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var source = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == _fixture.BusinessA.VariantId);
            var destination = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == destinationId && row.VariantId == _fixture.BusinessA.VariantId);
            Assert.Equal(90m, source.OnHand);
            Assert.Equal(5_400m, source.ValueOnHand);
            Assert.Equal(10m, destination.OnHand);
            Assert.Equal(600m, destination.ValueOnHand);

            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.ReferenceType == "stock_transfer" && row.ReferenceId == created.Id)
                .ToListAsync();
            Assert.Equal(2, movements.Count);
            Assert.Equal(-600m, movements.Single(row => row.MovementType == MovementType.TransferOut).ValueDelta);
            Assert.Equal(600m, movements.Single(row => row.MovementType == MovementType.TransferIn).ValueDelta);

            var accounts = await db.Accounts.AsNoTracking().Where(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && (row.Code == "1200" || row.Code == "1210")).ToDictionaryAsync(row => row.Code);
            var dispatchLines = await db.JournalLines.AsNoTracking().Where(row => row.JournalEntryId == dispatched.DispatchJournalEntryId).ToListAsync();
            Assert.Equal(600m, dispatchLines.Single(row => row.AccountId == accounts["1210"].Id).DebitAmount);
            Assert.Equal(600m, dispatchLines.Single(row => row.AccountId == accounts["1200"].Id).CreditAmount);
            var receiveLines = await db.JournalLines.AsNoTracking().Where(row => row.JournalEntryId == received.ReceiveJournalEntryId).ToListAsync();
            Assert.Equal(600m, receiveLines.Single(row => row.AccountId == accounts["1200"].Id).DebitAmount);
            Assert.Equal(600m, receiveLines.Single(row => row.AccountId == accounts["1210"].Id).CreditAmount);
            await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async transferDb =>
                await transferDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE stock_transfer_lines SET quantity = 11 WHERE id = {received.Lines.Single().Id}")));
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task Insufficient_stock_rolls_back_the_dispatch_without_a_movement_or_journal()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var destinationId = await CreateDestinationAsync();
        var request = new CreateStockTransferRequest(
            _fixture.BusinessA.LocationId,
            destinationId,
            [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 1_000m)],
            null);
        var create = await SendCreateAsync(client, request, "transfer-insufficient-1");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<StockTransferDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        var dispatch = await client.PostAsync($"/api/v1/inventory/transfers/{created!.Id:D}/dispatch", content: null);
        Assert.Equal(HttpStatusCode.Conflict, dispatch.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var transfer = await db.StockTransfers.AsNoTracking().SingleAsync(row => row.Id == created.Id);
            Assert.Equal(StockTransferStatus.Draft, transfer.Status);
            Assert.Null(transfer.DispatchJournalEntryId);
            Assert.False(await db.InventoryMovements.AsNoTracking().AnyAsync(row => row.ReferenceType == "stock_transfer" && row.ReferenceId == created.Id));
            Assert.False(await db.JournalEntries.AsNoTracking().AnyAsync(row => row.SourceId == created.Id
                && row.SourceType == JournalSourceType.StockTransfer));
        });
    }

    [Fact]
    public async Task Transfer_mutations_require_inventory_adjust_permission()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var request = new CreateStockTransferRequest(
            _fixture.BusinessA.LocationId,
            Guid.NewGuid(),
            [new StockTransferLineRequest(_fixture.BusinessA.VariantId, 1m)],
            null);
        var response = await SendCreateAsync(client, request, "transfer-viewer-1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<Guid> CreateDestinationAsync()
    {
        var destinationId = Guid.CreateVersion7();
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.BusinessLocations.Add(new BusinessLocation
            {
                Id = destinationId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Name = $"Branch {destinationId:N}",
                Code = $"BR-{destinationId:N}"[..20],
                IsActive = true,
            });
            var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
            membership.AllLocations = true;
            await db.SaveChangesAsync();
        });
        return destinationId;
    }

    private static Task<HttpResponseMessage> SendCreateAsync(HttpClient client, CreateStockTransferRequest request, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/transfers")
        {
            Content = JsonContent.Create(request, options: QtsBilling.Contracts.Serialization.ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(message);
    }
}
