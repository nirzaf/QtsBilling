using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The stock balance under concurrency and the negative-stock policy (specification task 14,
/// T14.01 - T14.05).
/// </summary>
/// <remarks>
/// The balance row is created with a scoped database upsert and only then locked, so two concurrent
/// movements on a variant that has no balance yet serialize on the row instead of racing to create it.
/// Negative stock is prohibited in this release, adjustments included.
/// </remarks>
public sealed class InventoryConcurrencyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public InventoryConcurrencyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T14_01_two_concurrent_movements_serialize_on_one_balance_row()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client);

        // Two movements on a variant that has no balance row yet, at the same time.
        var first = RecordMovementAsync(client, variantId, 1m);
        var second = RecordMovementAsync(client, variantId, 2m);

        var responses = await Task.WhenAll(first, second);

        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"The movement was refused with {(int)response.StatusCode}: {body}");
        }

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balances = await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                    && balance.VariantId == variantId)
                .ToListAsync();

            // Exactly one balance row, carrying both movements.
            var balance = Assert.Single(balances);
            Assert.Equal(3m, balance.OnHand);

            // The movements tie to the balance.
            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                    && movement.VariantId == variantId)
                .SumAsync(movement => movement.QuantityDelta);

            Assert.Equal(balance.OnHand, movements);
        });
    }

    [Fact]
    public async Task T14_02_a_movement_that_would_make_the_stock_negative_is_refused()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client);

        var opened = await RecordMovementAsync(client, variantId, 5m);
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);

        var refused = await RecordMovementAsync(client, variantId, -6m, "TransferOut");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            // The refused movement left nothing behind.
            Assert.Equal(5m, balance.OnHand);
        });
    }

    [Fact]
    public async Task T14_03_a_stock_adjustment_may_not_make_the_stock_negative_either()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateVariantAsync(client);

        var opened = await RecordMovementAsync(client, variantId, 2m);
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);

        // An adjustment is not a way around the rule: the database forbids a negative balance too.
        var refused = await RecordAdjustmentAsync(client, variantId, -3m);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.VariantId == variantId)
                .FirstAsync();

            Assert.Equal(2m, balance.OnHand);
        });
    }

    private async Task<Guid> CreateVariantAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/products",
            new
            {
                name = $"Race variant {Guid.NewGuid():N}",
                sku = $"RACE-{Guid.NewGuid():N}",
                taxType = "Exclusive",
                trackStock = true,
                variants = new[]
                {
                    new
                    {
                        name = "Race variant",
                        sku = $"RACE-V-{Guid.NewGuid():N}",
                        sellingPrice = 100m,
                        purchasePrice = 60m,
                        isDefault = true,
                    },
                },
            });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The product could not be created with {(int)response.StatusCode}: {body}");

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var variants = document.RootElement.GetProperty("variants");

        return variants[0].GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> RecordMovementAsync(HttpClient client, Guid variantId, decimal quantity, string movementType = "Opening")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/movements")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                variantId,
                movementType,
                quantityDelta = quantity,
                unitCost = 60m,
                referenceType = "test",
                referenceId = Guid.CreateVersion7(),
                memo = "concurrency test",
            }),
        };

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RecordAdjustmentAsync(HttpClient client, Guid variantId, decimal quantity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/movements")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                variantId,
                movementType = "StockAdjustment",
                quantityDelta = quantity,
                unitCost = 60m,
                referenceType = "test",
                referenceId = Guid.CreateVersion7(),
                memo = "negative stock test",
            }),
        };

        return await client.SendAsync(request);
    }
}
