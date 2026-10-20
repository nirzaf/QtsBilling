using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>Quote and sales-order commitments, revisions, authorization and non-financial boundaries.</summary>
public sealed class QuoteOrderTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public QuoteOrderTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T41_01_quote_conversion_and_confirmation_change_no_ledger_or_stock_facts()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var before = await CaptureFinancialFactsAsync();
        var quote = await CreateQuoteAsync(client, "t41-nonfinancial-quote-" + Guid.NewGuid().ToString("N"));
        quote = await ReadOkAsync<SalesQuoteDto>(await client.PostAsJsonAsync($"/api/v1/quotes/{quote.Id}/send", new QuoteCommandRequest(quote.Version, "Send wholesale scope"), ContractJson.Options));
        quote = await ReadOkAsync<SalesQuoteDto>(await client.PostAsJsonAsync($"/api/v1/quotes/{quote.Id}/accept", new QuoteCommandRequest(quote.Version, "Customer accepted"), ContractJson.Options));

        using var convertRequest = NewKeyedPost($"/api/v1/quotes/{quote.Id}/convert", new ConvertQuoteRequest(quote.Version), "t41-convert-" + Guid.NewGuid().ToString("N"));
        var convertedResponse = await client.SendAsync(convertRequest);
        var conversion = await ReadOkAsync<QuoteConversionResultDto>(convertedResponse, HttpStatusCode.Created);
        var order = await ReadOkAsync<SalesOrderDto>(await client.PostAsJsonAsync($"/api/v1/sales-orders/{conversion.Order.Id}/confirm", new ConfirmSalesOrderRequest(conversion.Order.Version, "Confirm agreed scope"), ContractJson.Options));

        Assert.Equal("Accepted", quote.Status);
        Assert.Equal("Confirmed", order.Status);
        Assert.Equal(1, order.CurrentRevision);
        Assert.Equal(before, await CaptureFinancialFactsAsync());
    }

    [Fact]
    public async Task T41_02_expired_quotes_and_stale_amendments_are_refused_without_rewriting_snapshots()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var businessToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime);
        var expired = await CreateQuoteAsync(client, "t41-expired-" + Guid.NewGuid().ToString("N"), businessToday.AddDays(-2), businessToday.AddDays(-1));
        var sendExpired = await client.PostAsJsonAsync($"/api/v1/quotes/{expired.Id}/send", new QuoteCommandRequest(expired.Version, "Expired before send"), ContractJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, sendExpired.StatusCode);
        Assert.Contains("QUOTE_EXPIRED", await sendExpired.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var expiredAccept = await client.PostAsJsonAsync($"/api/v1/quotes/{expired.Id}/accept", new QuoteCommandRequest(expired.Version + 1), ContractJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, expiredAccept.StatusCode);
        Assert.Contains("QUOTE_EXPIRED", await expiredAccept.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var valid = await CreateQuoteAsync(client, "t41-stale-" + Guid.NewGuid().ToString("N"));
        var sent = await ReadOkAsync<SalesQuoteDto>(await client.PostAsJsonAsync($"/api/v1/quotes/{valid.Id}/send", new QuoteCommandRequest(valid.Version, "Initial sent revision"), ContractJson.Options));
        var staleAmendment = new QuoteAmendRequest(valid.Version, sent.ExpiryDate.AddDays(5), sent.PromisedDeliveryDate,
            "A stale amendment", [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 2m)]);
        var staleResponse = await client.PostAsJsonAsync($"/api/v1/quotes/{valid.Id}/amend", staleAmendment, ContractJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Contains("CONCURRENCY_CONFLICT", await staleResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var expiredRow = await db.SalesQuotes.AsNoTracking().SingleAsync(row => row.Id == expired.Id);
            Assert.Equal(SalesQuoteStatus.Expired, expiredRow.Status);
            Assert.Equal(0, expiredRow.CurrentRevision);
            var sentRow = await db.SalesQuotes.AsNoTracking().SingleAsync(row => row.Id == valid.Id);
            Assert.Equal(SalesQuoteStatus.Sent, sentRow.Status);
            Assert.Equal(1, sentRow.CurrentRevision);
            Assert.Equal(1, await db.SalesQuoteRevisions.CountAsync(row => row.QuoteId == valid.Id));
        });
    }

    [Fact]
    public async Task T41_03_duplicate_quote_conversion_returns_the_original_order_and_agreed_snapshots()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var quote = await CreateQuoteAsync(client, "t41-idempotent-" + Guid.NewGuid().ToString("N"));
        quote = await ReadOkAsync<SalesQuoteDto>(await client.PostAsJsonAsync($"/api/v1/quotes/{quote.Id}/send", new QuoteCommandRequest(quote.Version, "Send"), ContractJson.Options));
        quote = await ReadOkAsync<SalesQuoteDto>(await client.PostAsJsonAsync($"/api/v1/quotes/{quote.Id}/accept", new QuoteCommandRequest(quote.Version, "Accepted"), ContractJson.Options));
        var key = "t41-convert-" + Guid.NewGuid().ToString("N");
        var request = new ConvertQuoteRequest(quote.Version, "Wholesale order");
        using var firstMessage = NewKeyedPost($"/api/v1/quotes/{quote.Id}/convert", request, key);
        var first = await ReadOkAsync<QuoteConversionResultDto>(await client.SendAsync(firstMessage), HttpStatusCode.Created);
        using var replayMessage = NewKeyedPost($"/api/v1/quotes/{quote.Id}/convert", request, key);
        var replay = await ReadOkAsync<QuoteConversionResultDto>(await client.SendAsync(replayMessage));

        Assert.Equal(first.Order.Id, replay.Order.Id);
        Assert.Equal(first.Order.OrderNumber, replay.Order.OrderNumber);
        Assert.Single(first.Order.Lines);
        Assert.Equal(1m, first.Order.Lines.Single().Quantity);
        Assert.Equal(100m, first.Order.Lines.Single().UnitPrice);
        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(1, await db.SalesOrders.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.SourceQuoteId == quote.Id));
            var order = await db.SalesOrders.AsNoTracking().SingleAsync(row => row.Id == first.Order.Id);
            Assert.Equal(SalesOrderStatus.Draft, order.Status);
            Assert.Equal(0, await db.SalesOrderRevisions.CountAsync(row => row.OrderId == order.Id));
        });
    }

    [Fact]
    public async Task T41_04_confirmation_rechecks_discount_authority_and_customer_credit_limit()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var originalAllowance = await _fixture.WithFreshContextAsync(async db =>
        {
            var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
            var value = membership.MaxDiscountRate;
            membership.MaxDiscountRate = 1m;
            await db.SaveChangesAsync();
            return value;
        });
        try
        {
            var discounted = await CreateOrderAsync(client, "t41-discount-" + Guid.NewGuid().ToString("N"), discountAmount: 10m);
            await _fixture.WithFreshContextAsync(async db =>
            {
                var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
                membership.MaxDiscountRate = 0.05m;
                await db.SaveChangesAsync();
            });
            // Changing discount authority bumps the membership authorization revision; sign in again
            // so this request tests the current allowance instead of the intentional stale-session denial.
            using var discountClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
            var refusedDiscount = await discountClient.PostAsJsonAsync($"/api/v1/sales-orders/{discounted.Id}/confirm", new ConfirmSalesOrderRequest(discounted.Version), ContractJson.Options);
            Assert.Equal(HttpStatusCode.Conflict, refusedDiscount.StatusCode);
            Assert.Contains("APPROVAL_REQUIRED", await refusedDiscount.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var customerId = Guid.CreateVersion7();
            await _fixture.WithFreshContextAsync(async db =>
            {
                db.Customers.Add(new Customer { Id = customerId, BusinessId = _fixture.BusinessA.BusinessId, Name = "Credit limit fixture", CreditLimit = 50m });
                var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
                membership.MaxDiscountRate = 1m;
                await db.SaveChangesAsync();
            });
            using var creditClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
            var creditOrder = await CreateOrderAsync(creditClient, "t41-credit-" + Guid.NewGuid().ToString("N"), customerId: customerId);
            var refusedCredit = await creditClient.PostAsJsonAsync($"/api/v1/sales-orders/{creditOrder.Id}/confirm", new ConfirmSalesOrderRequest(creditOrder.Version), ContractJson.Options);
            Assert.Equal(HttpStatusCode.Conflict, refusedCredit.StatusCode);
            Assert.Contains("CREDIT_LIMIT_EXCEEDED", await refusedCredit.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await _fixture.WithFreshContextAsync(async db =>
            {
                Assert.Equal(SalesOrderStatus.Draft, (await db.SalesOrders.AsNoTracking().SingleAsync(row => row.Id == discounted.Id)).Status);
                Assert.Equal(SalesOrderStatus.Draft, (await db.SalesOrders.AsNoTracking().SingleAsync(row => row.Id == creditOrder.Id)).Status);
                Assert.Equal(0, await db.SalesOrderRevisions.CountAsync(row => row.OrderId == discounted.Id || row.OrderId == creditOrder.Id));
            });
        }
        finally
        {
            await _fixture.WithFreshContextAsync(async db =>
            {
                var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == _fixture.Users.OwnerA.MembershipId);
                membership.MaxDiscountRate = originalAllowance;
                await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task T41_05_amendment_preserves_fulfilled_line_and_changes_only_outstanding_commitment()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var order = await CreateOrderAsync(client, "t41-amend-" + Guid.NewGuid().ToString("N"), quantity: 2m, discountAmount: 10m);
        order = await ReadOkAsync<SalesOrderDto>(await client.PostAsJsonAsync($"/api/v1/sales-orders/{order.Id}/confirm", new ConfirmSalesOrderRequest(order.Version, "Confirm initial quantity"), ContractJson.Options));
        var originalLine = Assert.Single(order.Lines);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var line = await db.SalesOrderLines.SingleAsync(row => row.Id == originalLine.Id);
            line.QuantityFulfilled = 1m;
            var aggregate = await db.SalesOrders.SingleAsync(row => row.Id == order.Id);
            aggregate.Status = SalesOrderStatus.PartiallyFulfilled;
            await db.SaveChangesAsync();
        });
        var partial = await ReadOkAsync<SalesOrderDto>(await client.GetAsync($"/api/v1/sales-orders/{order.Id}"));
        var amend = new AmendSalesOrderRequest(partial.Version, DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime).AddDays(10),
            "Replace the outstanding quantity", [new SalesOrderAmendmentLineRequest(originalLine.LineKey, _fixture.BusinessA.VariantId, "Replacement outstanding quantity", 3m, 100m, 0m, _fixture.BusinessA.TaxRateId, "Exclusive")]);
        var amended = await ReadOkAsync<SalesOrderDto>(await client.PostAsJsonAsync($"/api/v1/sales-orders/{order.Id}/amend", amend, ContractJson.Options));

        Assert.Equal("PartiallyFulfilled", amended.Status);
        Assert.Equal(2, amended.CurrentRevision);
        var historyLine = amended.Lines.Single(line => line.LineKey == originalLine.LineKey);
        Assert.Equal(2m, historyLine.Quantity);
        Assert.Equal(1m, historyLine.QuantityFulfilled);
        Assert.Equal(1m, historyLine.QuantityCancelled);
        Assert.Equal(100m, historyLine.UnitPrice);
        var currentRevision = amended.Revisions.Single(revision => revision.RevisionNumber == 2);
        Assert.Equal(2, currentRevision.Lines.Count);
        Assert.Contains(currentRevision.Lines, line => line.LineKey == originalLine.LineKey && line.Quantity == 1m && line.QuantityFulfilled == 1m);
        Assert.Contains(currentRevision.Lines, line => line.LineKey == originalLine.LineKey && line.DiscountAmount == 5m);
        Assert.Contains(currentRevision.Lines, line => line.Quantity == 3m && line.QuantityFulfilled == 0m);

        var initialRevisionId = amended.Revisions.Single(revision => revision.RevisionNumber == 1).Id;
        await _fixture.WithFreshContextAsync(async db =>
        {
            var persisted = await db.SalesOrderLines.AsNoTracking().SingleAsync(row => row.Id == originalLine.Id);
            Assert.Equal(1m, persisted.QuantityFulfilled);
            Assert.Equal(1m, persisted.QuantityCancelled);
            Assert.Equal(100m, persisted.UnitPrice);
            var priorRevision = await db.SalesOrderRevisionLines.AsNoTracking().SingleAsync(row => row.OrderRevisionId == initialRevisionId);
            Assert.Equal(2m, priorRevision.QuantityOrdered);
            Assert.Equal(0m, priorRevision.QuantityFulfilled);
        });
    }

    private async Task<SalesQuoteDto> CreateQuoteAsync(HttpClient client, string key, DateOnly? quoteDate = null, DateOnly? expiryDate = null)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime);
        var request = new CreateQuoteRequest(_fixture.BusinessA.LocationId, null, quoteDate ?? today,
            expiryDate ?? today.AddDays(7), today.AddDays(14), "QA wholesale quote",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 1m)]);
        using var message = NewKeyedPost("/api/v1/quotes", request, key);
        return await ReadOkAsync<SalesQuoteDto>(await client.SendAsync(message), HttpStatusCode.Created);
    }

    private async Task<SalesOrderDto> CreateOrderAsync(HttpClient client, string key, Guid? customerId = null, decimal quantity = 1m, decimal discountAmount = 0m)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime);
        var request = new CreateSalesOrderRequest(_fixture.BusinessA.LocationId, customerId, today, today.AddDays(14), "QA sales order",
            [new SalesOrderLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", quantity, 100m, discountAmount, _fixture.BusinessA.TaxRateId, "Exclusive")]);
        using var message = NewKeyedPost("/api/v1/sales-orders", request, key);
        return await ReadOkAsync<SalesOrderDto>(await client.SendAsync(message), HttpStatusCode.Created);
    }

    private async Task<FinancialFacts> CaptureFinancialFactsAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.LocationId == _fixture.BusinessA.LocationId && row.VariantId == _fixture.BusinessA.VariantId);
            return new FinancialFacts(
                await db.InventoryMovements.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId),
                balance.OnHand,
                balance.ValueOnHand,
                await db.JournalEntries.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId),
                await db.Sales.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId),
                await db.Invoices.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId));
        });

    private static HttpRequestMessage NewKeyedPost<T>(string route, T body, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return message;
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

    private sealed record FinancialFacts(int InventoryMovementCount, decimal OnHand, decimal ValueOnHand, int JournalEntryCount, int SaleCount, int InvoiceCount);
}
