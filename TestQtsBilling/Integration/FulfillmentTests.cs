using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>Reservation, delivery, and delivery-linked invoice flows (production-readiness task 42).</summary>
public sealed class FulfillmentTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public FulfillmentTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T42_01_competing_reservations_and_walk_in_sale_cannot_oversell_reserved_stock()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateStockVariantAsync(client);
        var first = await CreateConfirmedOrderAsync(client, variantId, 60m);
        var second = await CreateConfirmedOrderAsync(client, variantId, 60m);

        var attempts = await Task.WhenAll(
            ReserveRawAsync(client, first, "t42-race-a-" + Guid.NewGuid().ToString("N")),
            ReserveRawAsync(client, second, "t42-race-b-" + Guid.NewGuid().ToString("N")));
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in attempts) response.Dispose();

        using var pos = NewKeyedPost("/api/v1/pos/sales", new
        {
            locationId = _fixture.BusinessA.LocationId,
            currency = "QAR",
            lines = new[] { new { variantId, quantity = 41m, unitPrice = 100m, discountAmount = 0m, taxRate = 0.15m } },
            payment = new { method = "Cash", amount = 4_715m, cashReceived = 4_715m },
            note = "walk-in against reserved stock",
        }, "t42-walkin-" + Guid.NewGuid().ToString("N"));
        using var refusedSale = await client.SendAsync(pos);
        Assert.Equal(HttpStatusCode.Conflict, refusedSale.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == variantId);
            Assert.Equal(100m, balance.OnHand);
            Assert.Equal(1, await db.InventoryReservations.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && (row.OrderId == first.Id || row.OrderId == second.Id) && row.Status == InventoryReservationStatus.Active));
        });
    }

    [Fact]
    public async Task T42_02_partial_delivery_and_invoice_transfer_exact_cost_from_delivered_not_invoiced()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateStockVariantAsync(client);
        var order = await CreateConfirmedOrderAsync(client, variantId, 2m);
        var reservation = await ReserveAsync(client, order, "t42-reserve-partial-" + Guid.NewGuid().ToString("N"));
        var delivery = await DeliverAsync(client, order, 1m, "t42-delivery-partial-" + Guid.NewGuid().ToString("N"));
        var deliveryLine = Assert.Single(delivery.Lines);
        Assert.Equal(60m, delivery.CostValue);
        Assert.NotNull(delivery.JournalEntryNumber);

        var firstInvoice = await CreateDeliveryInvoiceAsync(client, order.Id, delivery.Id, deliveryLine.Id, 0.4m, "t42-invoice-slice-a-" + Guid.NewGuid().ToString("N"));
        var secondInvoice = await CreateDeliveryInvoiceAsync(client, order.Id, delivery.Id, deliveryLine.Id, 0.6m, "t42-invoice-slice-b-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(46m, firstInvoice.Lines.Single().LineTotal);
        Assert.Equal(69m, secondInvoice.Lines.Single().LineTotal);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var persistedDelivery = await db.SalesOrderDeliveries.AsNoTracking().SingleAsync(row => row.Id == delivery.Id);
            Assert.Equal(60m, (await db.SalesOrderDeliveryLines.AsNoTracking().SingleAsync(row => row.DeliveryId == delivery.Id)).CostValue);
            Assert.NotNull(persistedDelivery.JournalEntryId);

            var allocations = await db.DeliveryInvoiceAllocations.AsNoTracking().Where(row => row.DeliveryLineId == deliveryLine.Id).ToListAsync();
            Assert.Equal(1m, allocations.Sum(row => row.Quantity));
            Assert.Equal(60m, allocations.Sum(row => row.CostValue));
            var deliveredAccount = await db.Accounts.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Code == "1250");
            var cogsAccount = await db.Accounts.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Code == "5000");
            var deliveredBalance = await db.JournalLines.AsNoTracking().Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.AccountId == deliveredAccount.Id)
                .SumAsync(row => row.DebitAmount - row.CreditAmount);
            Assert.Equal(0m, deliveredBalance);
            var invoiceCost = await db.JournalLines.AsNoTracking().Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.AccountId == cogsAccount.Id)
                .SumAsync(row => row.DebitAmount - row.CreditAmount);
            Assert.Equal(60m, invoiceCost);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    [Fact]
    public async Task T42_03_duplicate_delivery_replays_once_and_concurrent_invoice_selection_allocates_once()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateStockVariantAsync(client);
        var order = await CreateConfirmedOrderAsync(client, variantId, 1m);
        var reservation = await ReserveAsync(client, order, "t42-reserve-once-" + Guid.NewGuid().ToString("N"));
        var deliveryKey = "t42-delivery-once-" + Guid.NewGuid().ToString("N");
        var deliveryRequest = DeliveryRequest(order, 1m);
        var delivery = await ReadOkAsync<SalesOrderDeliveryDto>(await SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries", deliveryRequest, deliveryKey), HttpStatusCode.Created);
        var replay = await ReadOkAsync<SalesOrderDeliveryDto>(await SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries", deliveryRequest, deliveryKey));
        Assert.Equal(delivery.Id, replay.Id);
        var deliveryLine = Assert.Single(delivery.Lines);

        var invoiceRequest = InvoiceRequest(deliveryLine.Id, 1m);
        var invoiceKeyA = "t42-invoice-race-a-" + Guid.NewGuid().ToString("N");
        var invoiceKeyB = "t42-invoice-race-b-" + Guid.NewGuid().ToString("N");
        var invoiceAttempts = await Task.WhenAll(
            SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries/{delivery.Id}/invoices", invoiceRequest, invoiceKeyA),
            SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries/{delivery.Id}/invoices", invoiceRequest, invoiceKeyB));
        var winnerIndex = Array.FindIndex(invoiceAttempts, response => response.StatusCode == HttpStatusCode.Created);
        Assert.True(winnerIndex >= 0);
        Assert.Equal(HttpStatusCode.Conflict, invoiceAttempts[1 - winnerIndex].StatusCode);
        using var loser = invoiceAttempts[1 - winnerIndex];
        var createdInvoice = await ReadOkAsync<InvoiceDetailDto>(invoiceAttempts[winnerIndex], HttpStatusCode.Created);
        var winningKey = winnerIndex == 0 ? invoiceKeyA : invoiceKeyB;
        var invoiceReplay = await ReadOkAsync<InvoiceDetailDto>(await SendAsync(client,
            $"/api/v1/sales-orders/{order.Id}/deliveries/{delivery.Id}/invoices", invoiceRequest, winningKey));
        Assert.Equal(createdInvoice.Invoice.Id, invoiceReplay.Invoice.Id);

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(1, await db.SalesOrderDeliveries.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.OrderId == order.Id));
            var deliveryLineIds = await db.SalesOrderDeliveryLines.AsNoTracking().Where(row => row.DeliveryId == delivery.Id).Select(row => row.Id).ToArrayAsync();
            Assert.Equal(1, await db.InventoryMovements.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.ReferenceType == "sales_order_delivery_line" && deliveryLineIds.Contains(row.ReferenceId)));
            Assert.Equal(1, await db.Invoices.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.SourceOrderId == order.Id));
            Assert.Equal(1, await db.DeliveryInvoiceAllocations.CountAsync(row => row.DeliveryLineId == deliveryLine.Id));
        });
    }

    [Fact]
    public async Task T42_04_cancelling_remainder_releases_only_unshipped_reservation()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateStockVariantAsync(client);
        var order = await CreateConfirmedOrderAsync(client, variantId, 3m);
        var reservation = await ReserveAsync(client, order, "t42-reserve-cancel-" + Guid.NewGuid().ToString("N"));
        await DeliverAsync(client, order, 1m, "t42-delivery-before-cancel-" + Guid.NewGuid().ToString("N"));
        var currentOrder = await ReadOkAsync<SalesOrderDto>(await client.GetAsync($"/api/v1/sales-orders/{order.Id}"));
        using var cancel = await client.PostAsJsonAsync($"/api/v1/sales-orders/{order.Id}/cancel-remainder",
            new CancelOrderRemainderRequest(currentOrder.Version, "Customer cancelled the unshipped balance"), ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var reservationLine = await db.InventoryReservationLines.AsNoTracking().SingleAsync(row => row.ReservationId == reservation.Id);
            Assert.Equal(1m, reservationLine.QuantityConsumed);
            Assert.Equal(2m, reservationLine.QuantityReleased);
            Assert.Equal(0m, reservationLine.QuantityReserved - reservationLine.QuantityConsumed - reservationLine.QuantityReleased);
            Assert.Equal(1m, (await db.SalesOrderLines.AsNoTracking().SingleAsync(row => row.OrderId == order.Id)).QuantityFulfilled);
        });

        var pos = new
        {
            locationId = _fixture.BusinessA.LocationId,
            currency = "QAR",
            lines = new[] { new { variantId, quantity = 99m, unitPrice = 100m, discountAmount = 0m, taxRate = 0.15m } },
            payment = new { method = "Cash", amount = 11_385m, cashReceived = 11_385m },
            note = "use the released unshipped stock",
        };
        using var sale = await SendAsync(client, "/api/v1/pos/sales", pos, "t42-released-stock-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
    }

    [Fact]
    public async Task T42_05_failure_after_stock_issue_rolls_back_delivery_and_reservation_consumption()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var variantId = await CreateStockVariantAsync(client);
        var order = await CreateConfirmedOrderAsync(client, variantId, 1m);
        var reservation = await ReserveAsync(client, order, "t42-reserve-rollback-" + Guid.NewGuid().ToString("N"));
        await _fixture.WithFreshContextAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION qts_test_fail_delivery_insert() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'injected delivery write failure' USING ERRCODE = 'check_violation';
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER trg_test_fail_delivery_insert
                BEFORE INSERT ON sales_order_deliveries
                FOR EACH ROW EXECUTE FUNCTION qts_test_fail_delivery_insert();
            """));
        try
        {
            using var response = await SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries", DeliveryRequest(order, 1m), "t42-delivery-rollback-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            await _fixture.WithFreshContextAsync(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS trg_test_fail_delivery_insert ON sales_order_deliveries; DROP FUNCTION IF EXISTS qts_test_fail_delivery_insert();"));
        }

        await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == variantId);
            Assert.Equal(100m, balance.OnHand);
            Assert.Equal(0, await db.SalesOrderDeliveries.CountAsync(row => row.OrderId == order.Id));
            Assert.Equal(0, await db.InventoryMovements.CountAsync(row => row.VariantId == variantId && row.ReferenceType == "sales_order_delivery_line"));
            Assert.Equal(0, await db.JournalEntries.CountAsync(row => row.SourceType == JournalSourceType.SalesOrderDelivery
                && db.SalesOrderDeliveries.Any(delivery => delivery.Id == row.SourceId && delivery.OrderId == order.Id)));
            var reservationLine = await db.InventoryReservationLines.AsNoTracking().SingleAsync(row => row.ReservationId == reservation.Id);
            Assert.Equal(0m, reservationLine.QuantityConsumed);
            Assert.Equal(0m, reservationLine.QuantityReleased);
            Assert.Equal(1m, reservationLine.QuantityReserved - reservationLine.QuantityConsumed - reservationLine.QuantityReleased);
            Assert.Equal(0m, (await db.SalesOrderLines.AsNoTracking().SingleAsync(row => row.OrderId == order.Id)).QuantityFulfilled);
        });
    }

    private async Task<SalesOrderDto> CreateConfirmedOrderAsync(HttpClient client, Guid variantId, decimal quantity)
    {
        var today = BusinessToday();
        var request = new CreateSalesOrderRequest(_fixture.BusinessA.LocationId, null, today, today.AddDays(10), "T42 fulfillment order",
            [new SalesOrderLineRequest(variantId, "Fixture variant", quantity, 100m, 0m, _fixture.BusinessA.TaxRateId, "Exclusive")]);
        var order = await ReadOkAsync<SalesOrderDto>(await SendAsync(client, "/api/v1/sales-orders", request, "t42-order-" + Guid.NewGuid().ToString("N")), HttpStatusCode.Created);
        return await ReadOkAsync<SalesOrderDto>(await client.PostAsJsonAsync($"/api/v1/sales-orders/{order.Id}/confirm", new ConfirmSalesOrderRequest(order.Version, "Confirm fulfillment scope"), ContractJson.Options));
    }

    private async Task<InventoryReservationDto> ReserveAsync(HttpClient client, SalesOrderDto order, string key)
        => await ReadOkAsync<InventoryReservationDto>(await ReserveRawAsync(client, order, key), HttpStatusCode.Created);

    private Task<HttpResponseMessage> ReserveRawAsync(HttpClient client, SalesOrderDto order, string key)
        => SendAsync(client, $"/api/v1/sales-orders/{order.Id}/reservations",
            new ReserveSalesOrderRequest(order.Version, DateTimeOffset.UtcNow.AddDays(1)), key);

    private async Task<SalesOrderDeliveryDto> DeliverAsync(HttpClient client, SalesOrderDto order, decimal quantity, string key)
    {
        Assert.Single(order.Lines);
        return await ReadOkAsync<SalesOrderDeliveryDto>(await SendAsync(client, $"/api/v1/sales-orders/{order.Id}/deliveries", DeliveryRequest(order, quantity), key), HttpStatusCode.Created);
    }

    private DeliverSalesOrderRequest DeliveryRequest(SalesOrderDto order, decimal quantity)
        => new(order.Version, BusinessToday(), "T42 dispatch", [new DeliverSalesOrderLineRequest(order.Lines.Single().Id, quantity)]);

    private static CreateDeliveryInvoiceRequest InvoiceRequest(Guid deliveryLineId, decimal quantity)
        => new(BusinessToday(), null, "Invoice delivered quantity", [new CreateDeliveryInvoiceLineRequest(deliveryLineId, quantity)]);

    private async Task<InvoiceDetailDto> CreateDeliveryInvoiceAsync(HttpClient client, Guid orderId, Guid deliveryId, Guid deliveryLineId, decimal quantity, string key)
        => await ReadOkAsync<InvoiceDetailDto>(await SendAsync(client, $"/api/v1/sales-orders/{orderId}/deliveries/{deliveryId}/invoices", InvoiceRequest(deliveryLineId, quantity), key), HttpStatusCode.Created);

    private static DateOnly BusinessToday() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime);

    private async Task<Guid> CreateStockVariantAsync(HttpClient client)
    {
        var token = Guid.NewGuid().ToString("N");
        using var productResponse = await client.PostAsJsonAsync("/api/v1/products", new
        {
            name = $"Fulfillment fixture {token}",
            sku = $"FUL-{token}",
            trackStock = true,
            variantName = "Fulfillment fixture",
            sellingPrice = 100m,
            purchasePrice = 60m,
        });
        var productBody = await productResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        using var productJson = System.Text.Json.JsonDocument.Parse(productBody);
        var variantId = productJson.RootElement.GetProperty("variants")[0].GetProperty("id").GetGuid();
        await _fixture.WithFreshContextAsync(async db =>
        {
            var productId = await db.ProductVariants.AsNoTracking().Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Id == variantId)
                .Select(row => row.ProductId).SingleAsync();
            var product = await db.Products.SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Id == productId);
            product.TaxRateId = _fixture.BusinessA.TaxRateId;
            await db.SaveChangesAsync();
        });

        using var movementResponse = await client.PostAsJsonAsync("/api/v1/inventory/movements", new
        {
            locationId = _fixture.BusinessA.LocationId,
            variantId,
            movementType = "Opening",
            quantityDelta = 100m,
            unitCost = 60m,
            referenceType = "t42_fixture_opening",
            referenceId = Guid.CreateVersion7(),
            memo = "Isolated T42 test stock",
        });
        var movementBody = await movementResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, movementResponse.StatusCode);
        Assert.NotEmpty(movementBody);
        return variantId;
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string route, T body, string key)
    {
        using var message = NewKeyedPost(route, body, key);
        return await client.SendAsync(message);
    }

    private static HttpRequestMessage NewKeyedPost<T>(string route, T body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    private static async Task<T> ReadOkAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) where T : class
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            return System.Text.Json.JsonSerializer.Deserialize<T>(body, ContractJson.Options)
                ?? throw new InvalidOperationException($"The response body did not contain {typeof(T).Name}.");
        }
    }
}
