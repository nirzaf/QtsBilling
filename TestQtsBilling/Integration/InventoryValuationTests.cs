using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The perpetual valuation of the stock and the cost of goods sold (specification task 15,
/// T15.01 - T15.05).
/// </summary>
/// <remarks>
/// The value of a movement is the authority: a receipt adds the acquisition value, an issue consumes
/// the moving average, and the cost of goods sold is the value the movement consumed — never the
/// catalog purchase price (finding F15).
/// </remarks>
public sealed class InventoryValuationTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public InventoryValuationTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T15_01_a_receipt_adds_the_acquisition_value_and_the_average_cost()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client, sellingPrice: 100m);

        // A receipt of 10 pieces at 60: the value on hand is 600 and the average cost is 60.
        await ReceiveAsync(client, variantId, quantity: 10, unitCost: 60m);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(10m, balance.OnHand);
            Assert.Equal(600m, balance.ValueOnHand);
            Assert.Equal(60m, balance.AverageUnitCost);

            var movement = await db.InventoryMovements.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(600m, movement.ValueDelta);
        });
    }

    [Fact]
    public async Task T15_02_the_cost_of_goods_sold_is_the_value_the_movement_consumed()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client, sellingPrice: 250m);

        await ReceiveAsync(client, variantId, quantity: 10, unitCost: 60m);

        // The catalog price is 250, the stock cost 60: the cost of goods sold must be 60.
        var saleId = await PostSaleAsync(client, variantId, quantity: 4, unitPrice: 250m);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var cogs = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "5000");

            var cogsAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId
                    && line.AccountId == cogs.Id
                    && line.JournalEntryId == db.JournalEntries.AsNoTracking()
                        .Where(entry => entry.SourceId == saleId)
                        .Select(entry => entry.Id)
                        .First())
                .SumAsync(line => line.DebitAmount - line.CreditAmount);

            // Four pieces at the average cost of 60.
            Assert.Equal(240m, cogsAmount);

            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(6m, balance.OnHand);
            Assert.Equal(360m, balance.ValueOnHand);
            Assert.Equal(60m, balance.AverageUnitCost);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task T15_03_the_final_depletion_takes_the_exact_remaining_value()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client, sellingPrice: 100m);

        await ReceiveAsync(client, variantId, quantity: 3, unitCost: 10m);

        // A quantity of zero must leave a value of zero, whatever the rounding did on the way.
        await PostSaleAsync(client, variantId, quantity: 3, unitPrice: 100m);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(0m, balance.OnHand);
            Assert.Equal(0m, balance.ValueOnHand);
            Assert.Equal(0m, balance.AverageUnitCost);
        });
    }

    [Fact]
    public async Task T15_04_stock_without_a_value_cannot_be_consumed()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client, sellingPrice: 100m);

        // Stock that was never valued: the historical stock of a business whose valuation is still open.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;

            db.InventoryMovements.Add(new QtsBilling.Persistence.Postgres.Entities.InventoryMovement
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                LocationId = _fixture.BusinessA.LocationId,
                VariantId = variantId,
                MovementType = QtsBilling.Persistence.Postgres.MovementType.Opening,
                QuantityDelta = 5m,
                UnitCost = 0m,
                ValueDelta = 0m,
                ReferenceType = "opening",
                ReferenceId = Guid.CreateVersion7(),
                OccurredAtUtc = now,
                Memo = "unvalued opening",
            });

            db.InventoryBalances.Add(new QtsBilling.Persistence.Postgres.Entities.InventoryBalance
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                LocationId = _fixture.BusinessA.LocationId,
                VariantId = variantId,
                OnHand = 5m,
                ValueOnHand = 0m,
                AverageUnitCost = 0m,
                LastMovementAtUtc = now,
            });

            await db.SaveChangesAsync();
        });

        var refused = await PostSaleAsyncRawAsync(client, variantId, quantity: 1, unitPrice: 100m);

        // The sale is refused: the cost cannot be invented as zero.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(5m, balance.OnHand);
        });
    }

    private async Task<Guid> CreateVariantAsync(HttpClient client, decimal sellingPrice)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/products",
            new
            {
                name = $"Valued variant {Guid.NewGuid():N}",
                sku = $"VAL-{Guid.NewGuid():N}",
                trackStock = true,
                variantName = "Valued variant",
                sellingPrice,
                purchasePrice = 60m,
            });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The product could not be created with {(int)response.StatusCode}: {body}");

        using var document = System.Text.Json.JsonDocument.Parse(body);

        return document.RootElement.GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task ReceiveAsync(HttpClient client, Guid variantId, decimal quantity, decimal unitCost)
    {
        var supplierResponse = await client.PostAsJsonAsync(
            "/api/v1/suppliers",
            new { name = $"Valuation supplier {Guid.NewGuid():N}", email = $"valuation-{Guid.NewGuid():N}@fixture.test" });

        var supplierBody = await supplierResponse.Content.ReadAsStringAsync();
        Assert.True(supplierResponse.IsSuccessStatusCode, supplierBody);

        using var supplierDocument = System.Text.Json.JsonDocument.Parse(supplierBody);
        var supplierId = supplierDocument.RootElement.GetProperty("id").GetGuid();

        var orderResponse = await client.PostAsJsonAsync(
            "/api/v1/purchase-orders",
            new
            {
                supplierId,
                locationId = _fixture.BusinessA.LocationId,
                orderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                expectedDate = (DateOnly?)null,
                note = "valuation order",
                lines = new[]
                {
                    new { variantId, quantity = quantity, unitCost, discountAmount = 0m, taxRate = 0m },
                },
            });

        var orderBody = await orderResponse.Content.ReadAsStringAsync();
        Assert.True(orderResponse.IsSuccessStatusCode, orderBody);

        using var orderDocument = System.Text.Json.JsonDocument.Parse(orderBody);
        var orderId = orderDocument.RootElement.GetProperty("order").GetProperty("id").GetGuid();

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/order")
        {
            Content = JsonContent.Create(new { }),
        };

        var confirmed = await client.SendAsync(confirm);
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());

        var orderLineId = await _fixture.WithFreshContextAsync(db =>
            db.PurchaseOrderLines.AsNoTracking()
                .Where(line => line.PurchaseOrderId == orderId)
                .Select(line => line.Id)
                .FirstAsync());

        using var receipt = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/receipts")
        {
            Content = JsonContent.Create(new
            {
                note = "valuation receipt",
                lines = new[] { new { purchaseOrderLineId = orderLineId, quantity } },
            }),
        };

        receipt.Headers.Add("Idempotency-Key", "valuation-" + Guid.NewGuid().ToString("N"));

        var received = await client.SendAsync(receipt);
        Assert.True(received.IsSuccessStatusCode, await received.Content.ReadAsStringAsync());
    }

    private async Task<Guid> PostSaleAsync(HttpClient client, Guid variantId, decimal quantity, decimal unitPrice)
    {
        var response = await PostSaleAsyncRawAsync(client, variantId, quantity, unitPrice);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"The sale was refused with {(int)response.StatusCode}: {body}");

        using var document = System.Text.Json.JsonDocument.Parse(body);

        return document.RootElement.GetProperty("saleId").GetGuid();
    }

    private async Task<HttpResponseMessage> PostSaleAsyncRawAsync(HttpClient client, Guid variantId, decimal quantity, decimal unitPrice)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                currency = "QAR",
                lines = new[]
                {
                    new { variantId, quantity, unitPrice, discountAmount = 0m, taxRate = 0m },
                },
                payment = new { method = "Cash", amount = quantity * unitPrice, cashReceived = quantity * unitPrice },
                note = "valuation sale",
            }),
        };

        request.Headers.Add("Idempotency-Key", "valuation-sale-" + Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }
}
