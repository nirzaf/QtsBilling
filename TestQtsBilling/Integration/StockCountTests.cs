using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>Blind count snapshots, controlled approval, and valued inventory reconciliation.</summary>
public sealed class StockCountTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public StockCountTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Blind_count_submits_then_approves_a_valued_adjustment_once()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var request = new CreateStockCountRequest(_fixture.BusinessA.LocationId, "Quarterly shelf count");
        var create = await SendCreateAsync(client, request, "count-cycle-1");
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var opened = System.Text.Json.JsonSerializer.Deserialize<StockCountDetailDto>(createBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal(StockCountState.Open, opened.Status);
        Assert.Single(opened.Lines);
        Assert.Null(opened.Lines.Single().ExpectedQuantity);
        Assert.Null(opened.Lines.Single().ExpectedUnitCost);

        var replay = await SendCreateAsync(client, request, "count-cycle-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<StockCountDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(opened.Id, replayed!.Id);

        var record = await client.PutAsJsonAsync($"/api/v1/inventory/stock-counts/{opened.Id:D}/lines",
            new RecordStockCountLinesRequest([new StockCountLineRequest(_fixture.BusinessA.VariantId, 95m)]),
            QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, record.StatusCode);
        var submitted = await record.Content.ReadFromJsonAsync<StockCountDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(StockCountState.Submitted, submitted!.Status);
        Assert.Null(submitted.Lines.Single().ExpectedQuantity);
        Assert.Equal(95m, submitted.Lines.Single().CountedQuantity);

        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stock_count_lines SET counted_quantity = 96 WHERE id = {submitted.Lines.Single().Id}")));

        var approve = await client.PostAsync($"/api/v1/inventory/stock-counts/{opened.Id:D}/approve", content: null);
        var approveBody = await approve.Content.ReadAsStringAsync();
        Assert.True(approve.StatusCode == HttpStatusCode.OK, $"Approval returned {(int)approve.StatusCode}: {approveBody}");
        var approved = System.Text.Json.JsonSerializer.Deserialize<StockCountDetailDto>(approveBody, QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(StockCountState.Approved, approved!.Status);
        Assert.NotNull(approved.JournalEntryId);
        Assert.Equal(100m, approved.Lines.Single().ExpectedQuantity);
        Assert.Equal(60m, approved.Lines.Single().ExpectedUnitCost);
        Assert.Equal(-5m, approved.Lines.Single().VarianceQuantity);
        Assert.Equal(-300m, approved.Lines.Single().VarianceValue);

        var replayApprove = await client.PostAsync($"/api/v1/inventory/stock-counts/{opened.Id:D}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, replayApprove.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == _fixture.BusinessA.VariantId);
            Assert.Equal(95m, balance.OnHand);
            Assert.Equal(5_700m, balance.ValueOnHand);

            var movement = await db.InventoryMovements.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.ReferenceType == "stock_count_line" && row.ReferenceId == approved.Lines.Single().Id);
            Assert.Equal(MovementType.StockAdjustment, movement.MovementType);
            Assert.Equal(-5m, movement.QuantityDelta);
            Assert.Equal(-300m, movement.ValueDelta);

            var accounts = await db.Accounts.AsNoTracking().Where(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && (row.Code == "1200" || row.Code == "5200")).ToDictionaryAsync(row => row.Code);
            var journalLines = await db.JournalLines.AsNoTracking().Where(row => row.JournalEntryId == approved.JournalEntryId).ToListAsync();
            Assert.Equal(300m, journalLines.Single(row => row.AccountId == accounts["5200"].Id).DebitAmount);
            Assert.Equal(300m, journalLines.Single(row => row.AccountId == accounts["1200"].Id).CreditAmount);
            Assert.Equal(1, await db.InventoryMovements.AsNoTracking().CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.ReferenceType == "stock_count_line" && row.ReferenceId == approved.Lines.Single().Id));
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task Count_creation_requires_inventory_adjust_permission()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var response = await SendCreateAsync(client, new CreateStockCountRequest(_fixture.BusinessA.LocationId, null), "count-viewer-1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_count_without_variances_can_be_approved_without_a_zero_value_journal()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var create = await SendCreateAsync(client, new CreateStockCountRequest(_fixture.BusinessA.LocationId, null), "count-no-variance-1");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var opened = await create.Content.ReadFromJsonAsync<StockCountDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        var record = await client.PutAsJsonAsync($"/api/v1/inventory/stock-counts/{opened!.Id:D}/lines",
            new RecordStockCountLinesRequest([new StockCountLineRequest(_fixture.BusinessA.VariantId, 100m)]),
            QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, record.StatusCode);
        var approve = await client.PostAsync($"/api/v1/inventory/stock-counts/{opened.Id:D}/approve", content: null);
        var approved = await approve.Content.ReadFromJsonAsync<StockCountDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(StockCountState.Approved, approved!.Status);
        Assert.Null(approved.JournalEntryId);
        Assert.Equal(0m, approved.Lines.Single().VarianceQuantity);
        Assert.Equal(0m, approved.Lines.Single().VarianceValue);
    }

    private static Task<HttpResponseMessage> SendCreateAsync(HttpClient client, CreateStockCountRequest request, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/stock-counts")
        {
            Content = JsonContent.Create(request, options: QtsBilling.Contracts.Serialization.ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(message);
    }
}
