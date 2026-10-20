using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The recognition of a goods receipt (specification task 16, T16.01 - T16.05).
/// </summary>
/// <remarks>
/// A purchase order is only a commitment and posts nothing. A goods receipt recognises the stock and
/// clears it: the value is capitalised in the inventory and waits in the goods-received-not-invoiced
/// account until the supplier bill arrives. Before this was fixed, the quantity moved while the ledger
/// did not (finding F16).
/// </remarks>
public sealed class PurchasingReceiptPostingTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PurchasingReceiptPostingTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T16_01_a_goods_receipt_capitalises_the_stock_and_clears_it()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var supplierId = await CreateSupplierAsync(client);

        var orderId = await CreateOrderAsync(client, supplierId);
        await ConfirmOrderAsync(client, orderId);

        var order = await _fixture.WithFreshContextAsync(db =>
            db.PurchaseOrderLines.AsNoTracking()
                .Where(line => line.PurchaseOrderId == orderId)
                .Select(line => line.Id)
                .FirstAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/receipts")
        {
            Content = JsonContent.Create(new
            {
                note = "T16.01 receipt",
                lines = new[] { new { purchaseOrderLineId = order, quantity = 10 } },
            }),
        };

        request.Headers.Add("Idempotency-Key", "t16-01-" + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"The receipt was refused with {(int)response.StatusCode}: {body}");

        await _fixture.WithFreshContextAsync(async db =>
        {
            var inventory = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1200");
            var grni = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "2101");

            var inventoryAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == inventory.Id)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);

            var grniAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == grni.Id)
                .SumAsync(line => line.CreditAmount - line.DebitAmount);

            // Ten pieces at the ordered cost of 60: the stock is capitalised and the value is cleared.
            Assert.Equal(600m, inventoryAmount);
            Assert.Equal(600m, grniAmount);

            // The receipt is not a supplier payable: the payables account is untouched.
            var payable = await db.Accounts.AsNoTracking()
                .FirstAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1100");

            var payableAmount = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == payable.Id)
                .SumAsync(line => line.DebitAmount - line.CreditAmount);

            Assert.Equal(0m, payableAmount);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task T16_02_the_stock_quantity_and_the_journal_value_tie_together()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var supplierId = await CreateSupplierAsync(client);

        var orderId = await CreateOrderAsync(client, supplierId);
        await ConfirmOrderAsync(client, orderId);

        var before = await _fixture.WithFreshContextAsync(db =>
            db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                    && balance.VariantId == _fixture.BusinessA.VariantId)
                .Select(balance => balance.OnHand)
                .FirstAsync());

        var orderLineId = await _fixture.WithFreshContextAsync(db =>
            db.PurchaseOrderLines.AsNoTracking()
                .Where(line => line.PurchaseOrderId == orderId)
                .Select(line => line.Id)
                .FirstAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/receipts")
        {
            Content = JsonContent.Create(new
            {
                note = "T16.02 receipt",
                lines = new[] { new { purchaseOrderLineId = orderLineId, quantity = 4 } },
            }),
        };

        request.Headers.Add("Idempotency-Key", "t16-02-" + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var after = await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                    && balance.VariantId == _fixture.BusinessA.VariantId)
                .Select(balance => balance.OnHand)
                .FirstAsync();

            // The quantity moved by exactly the received amount.
            Assert.Equal(before + 4m, after);

            // The movements tie to the balance (the database guard proves it as well).
            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                    && movement.VariantId == _fixture.BusinessA.VariantId)
                .SumAsync(movement => movement.QuantityDelta);

            Assert.Equal(after, movements);

            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    private async Task<Guid> CreateSupplierAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/suppliers",
            new { name = $"Fixture supplier {Guid.NewGuid():N}", email = $"supplier-{Guid.NewGuid():N}@fixture.test" });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The supplier could not be created with {(int)response.StatusCode}: {body}");

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var id = document.RootElement.GetProperty("id");

        return id.GetGuid();
    }

    private async Task<Guid> CreateOrderAsync(HttpClient client, Guid supplierId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/purchase-orders",
            new
            {
                supplierId,
                locationId = _fixture.BusinessA.LocationId,
                orderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                expectedDate = (DateOnly?)null,
                note = "T16 purchase order",
                lines = new[]
                {
                    new { variantId = _fixture.BusinessA.VariantId, quantity = 20m, unitCost = 60m, discountAmount = 0m, taxRate = 0m },
                },
            });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The order could not be created with {(int)response.StatusCode}: {body}");

        using var document = System.Text.Json.JsonDocument.Parse(body);

        return ReadId(document.RootElement, "id", "orderId", "purchaseOrderId");
    }

    private static Guid ReadId(System.Text.Json.JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetGuid();
            }
        }

        // The purchase order detail nests the order under its own key.
        foreach (var child in element.EnumerateObject())
        {
            if (child.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var name in names)
                {
                    if (child.Value.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return value.GetGuid();
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"The response carries no identifier ({string.Join(", ", names)}): {element.GetRawText()}");
    }

    private async Task ConfirmOrderAsync(HttpClient client, Guid orderId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/purchase-orders/{orderId}/order")
        {
            Content = JsonContent.Create(new { }),
        };

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The order could not be confirmed with {(int)response.StatusCode}: {body}");
    }
}
