using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Orders;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Orders;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The quote-to-order journey as sales staff work it (specification task 22, T22.01 - T22.03 plus the shipping
/// cases), against the real host through the typed clients the shared screens inject. A conversion is one order with
/// the quoted terms and no financial effect; an expired quote or a stale version is refused with the sentence the
/// screen shows; and "sent" is a locally audited status with a printable document, never a claim that an email was
/// delivered, because this product has no mail service.
/// </summary>
public sealed class OrderJourneyTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T22_01_an_accepted_quote_converted_twice_yields_one_order_with_the_quoted_terms_and_no_financial_effect()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var quotes = new QuoteApiClient(owner);
        var orders = new SalesOrderApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var factsBefore = await CaptureFactsAsync();

        var draft = await quotes.CreateAsync(new CreateQuoteRequest(
            _fixture.BusinessA.LocationId,
            null,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(45)),
            "30 days, delivered in one go",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 2m, null, 10m, null, null)]),
            "t22-01-create-" + suffix);

        Assert.Equal("Draft", draft.Status);
        Assert.Equal(0, draft.CurrentRevision);
        Assert.Single(draft.Lines);

        var sent = await quotes.SendAsync(draft.Id, new QuoteCommandRequest(draft.Version, "Handed to the buyer at the counter"));
        Assert.Equal("Sent", sent.Status);
        Assert.Equal(1, sent.CurrentRevision);

        // The note that recorded how it was communicated travels with the sent revision - not with a mail service.
        Assert.Equal("Handed to the buyer at the counter", Assert.Single(sent.Revisions).Reason);

        var accepted = await quotes.AcceptAsync(sent.Id, new QuoteCommandRequest(sent.Version));
        Assert.Equal("Accepted", accepted.Status);

        var key = OrderRules.NewConversionKey(accepted.QuoteNumber);
        var request = new ConvertQuoteRequest(accepted.Version, "Order for the agreed scope");

        var first = await quotes.ConvertAsync(accepted.Id, request, key);
        var second = await quotes.ConvertAsync(accepted.Id, request, key);

        Assert.Equal(first.Order.Id, second.Order.Id);
        Assert.Equal(first.Order.OrderNumber, second.Order.OrderNumber);

        // A different key on the same quote is still the same order - the server refuses a second one.
        var third = await quotes.ConvertAsync(accepted.Id, new ConvertQuoteRequest(accepted.Version, "Order for the agreed scope"),
            "t22-01-other-" + suffix);
        Assert.Equal(first.Order.Id, third.Order.Id);

        // The quoted terms are preserved unchanged: same lines, same prices, same discount, same tax and total.
        Assert.Equal(accepted.Lines.Count, first.Order.Lines.Count);
        foreach (var quoted in accepted.Lines)
        {
            var copied = Assert.Single(first.Order.Lines, line => line.LineNo == quoted.LineNo);
            Assert.Equal(quoted.VariantId, copied.VariantId);
            Assert.Equal(quoted.Quantity, copied.Quantity);
            Assert.Equal(quoted.UnitPrice, copied.UnitPrice);
            Assert.Equal(quoted.DiscountAmount, copied.DiscountAmount);
            Assert.Equal(quoted.TaxAmount, copied.TaxAmount);
            Assert.Equal(quoted.LinePayableAmount, copied.LinePayableAmount);
            Assert.Equal(quoted.TaxMode, copied.TaxMode);
            Assert.Equal(0m, copied.QuantityFulfilled ?? 0m);
        }

        Assert.Equal(accepted.TotalAmount, first.Order.TotalAmount);
        Assert.Equal(accepted.TaxAmount, first.Order.TaxAmount);
        Assert.Equal(accepted.Currency, first.Order.Currency);
        Assert.Equal(accepted.Id, first.Order.SourceQuoteId);
        Assert.Equal("Draft", first.Order.Status);
        Assert.Equal(0, first.Order.CurrentRevision);

        // A draft order is a commitment on paper: no stock has moved and nothing has reached the ledger.
        Assert.Equal(factsBefore, await CaptureFactsAsync());

        var confirmed = await orders.ConfirmAsync(first.Order.Id, new ConfirmSalesOrderRequest(first.Order.Version, "Confirmed by the manager"));

        Assert.Equal("Confirmed", confirmed.Status);
        Assert.Equal(1, confirmed.CurrentRevision);
        Assert.Equal("Confirmed by the manager", Assert.Single(confirmed.Revisions).Reason);

        // Confirming commits to deliver; it still moves no stock and posts nothing (tasks 23 and 24 own those).
        Assert.Equal(factsBefore, await CaptureFactsAsync());

        var stored = await _fixture.WithFreshContextAsync(async db => (
            Orders: await db.SalesOrders.AsNoTracking().CountAsync(order => order.SourceQuoteId == accepted.Id),
            Revisions: await db.SalesOrderRevisions.AsNoTracking().CountAsync(row => row.OrderId == confirmed.Id),
            QuoteStatus: (await db.SalesQuotes.AsNoTracking().SingleAsync(row => row.Id == accepted.Id)).Status,
            Conversions: await db.SalesQuotes.AsNoTracking().CountAsync(row => row.Id == accepted.Id && row.Status == SalesQuoteStatus.Accepted)));

        Assert.Equal(1, stored.Orders);
        Assert.Equal(1, stored.Revisions);
        Assert.Equal(SalesQuoteStatus.Accepted, stored.QuoteStatus);
        Assert.Equal(1, stored.Conversions);
    }

    [Fact]
    public async Task T22_02_an_expired_quote_and_a_stale_version_are_refused_with_the_sentence_the_screen_shows()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var quotes = new QuoteApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(3).DateTime);

        // The fixture's own opening stock and any earlier document are the baseline: what a quote may never do is
        // move a fact, so the claim is "unchanged", not "zero".
        var factsBefore = await CaptureLedgerAsync();

        // 1. A quote whose expiry has passed cannot be sent, accepted or converted.
        var expired = await quotes.CreateAsync(new CreateQuoteRequest(
            _fixture.BusinessA.LocationId, null, today.AddDays(-3), today.AddDays(-1), null, "expired on purpose",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 1m)]),
            "t22-02-expired-" + suffix);

        var sendRefusal = await Assert.ThrowsAsync<ApiException>(() =>
            quotes.SendAsync(expired.Id, new QuoteCommandRequest(expired.Version, "too late")));
        Assert.Equal(409, sendRefusal.StatusCode);
        Assert.Equal(ApiErrorCodes.QuoteExpired, sendRefusal.Code);

        var afterSend = await quotes.GetAsync(expired.Id);
        Assert.Equal("Expired", afterSend.Status);
        Assert.Equal(0, afterSend.CurrentRevision);

        var acceptRefusal = await Assert.ThrowsAsync<ApiException>(() =>
            quotes.AcceptAsync(afterSend.Id, new QuoteCommandRequest(afterSend.Version)));
        Assert.Equal(ApiErrorCodes.QuoteExpired, acceptRefusal.Code);

        var convertRefusal = await Assert.ThrowsAsync<ApiException>(() => quotes.ConvertAsync(
            afterSend.Id,
            new ConvertQuoteRequest(afterSend.Version),
            "t22-02-expired-convert-" + suffix));
        Assert.Equal(ApiErrorCodes.QuoteNotAccepted, convertRefusal.Code);

        // The screen's wording for each of those refusals is the sentence an operator reads - named here so the
        // mapping cannot drift from the codes the server sends.
        Assert.Contains("passed its expiry date", OrderRules.RefusalSentence(sendRefusal, "quote"), StringComparison.Ordinal);
        Assert.Contains("has not been accepted", OrderRules.RefusalSentence(convertRefusal, "quote"), StringComparison.Ordinal);
        Assert.False(OrderRules.CommittedNothing(ApiErrorCodes.TransientRetry));
        Assert.True(OrderRules.CommittedNothing(ApiErrorCodes.QuoteExpired));

        // Nothing was written for the expired document: no revision, no order, and no ledger or stock movement
        // added anywhere in the business by the attempt.
        var facts = await _fixture.WithFreshContextAsync(async db => (
            Revisions: await db.SalesQuoteRevisions.AsNoTracking().CountAsync(row => row.QuoteId == expired.Id),
            Orders: await db.SalesOrders.AsNoTracking().CountAsync(order => order.SourceQuoteId == expired.Id)));

        Assert.Equal(0, facts.Revisions);
        Assert.Equal(0, facts.Orders);
        Assert.Equal(factsBefore, await CaptureLedgerAsync());

        // 2. A stale amendment is refused and the snapshot it would have overwritten is untouched.
        var valid = await quotes.CreateAsync(new CreateQuoteRequest(
            _fixture.BusinessA.LocationId, null, today, today.AddDays(20), null, "stale amendment test",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 1m)]),
            "t22-02-valid-" + suffix);

        var sent = await quotes.SendAsync(valid.Id, new QuoteCommandRequest(valid.Version, "initial revision"));
        var storedQuote = await _fixture.WithFreshContextAsync(async db => await db.SalesQuotes.AsNoTracking()
            .SingleAsync(row => row.Id == valid.Id));

        var stale = await Assert.ThrowsAsync<ApiException>(() => quotes.AmendAsync(
            valid.Id,
            new QuoteAmendRequest(
                valid.Version,                       // the version from before the send, deliberately stale
                sent.ExpiryDate.AddDays(5),
                sent.PromisedDeliveryDate,
                "an amendment written against an old version",
                [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 9m)])));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);
        Assert.Contains("Someone else changed", OrderRules.RefusalSentence(stale, "quote"), StringComparison.Ordinal);

        var unchanged = await _fixture.WithFreshContextAsync(async db => (
            Version: (await db.SalesQuotes.AsNoTracking().SingleAsync(row => row.Id == valid.Id)).Version,
            Revisions: await db.SalesQuoteRevisions.AsNoTracking().CountAsync(row => row.QuoteId == valid.Id),
            RevisionTotal: (await db.SalesQuoteRevisions.AsNoTracking().SingleAsync(row => row.QuoteId == valid.Id)).TotalAmount));

        Assert.Equal(storedQuote.Version, unchanged.Version);
        Assert.Equal(1, unchanged.Revisions);
        Assert.Equal(sent.TotalAmount, unchanged.RevisionTotal);

        // The same intention against the version now on screen is accepted, and it is a new revision.
        var amended = await quotes.AmendAsync(
            valid.Id,
            new QuoteAmendRequest(sent.Version, sent.ExpiryDate.AddDays(5), sent.PromisedDeliveryDate,
                "the buyer asked for more", [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 2m)]));

        Assert.Equal(2, amended.CurrentRevision);
        Assert.Equal(2, amended.Revisions.Count);
        Assert.Equal("the buyer asked for more", amended.Revisions[1].Reason);
        Assert.NotEqual(sent.TotalAmount, amended.TotalAmount);

        // A rejected quote has no actions left, which is what the screen's rule states.
        var rejected = await quotes.RejectAsync(amended.Id, new QuoteCommandRequest(amended.Version, "price too high"));
        Assert.Equal("Rejected", rejected.Status);
        Assert.Empty(OrderRules.AllowedQuoteActions(rejected.Status, alreadyConverted: false));

        var convertAfterReject = await Assert.ThrowsAsync<ApiException>(() => quotes.ConvertAsync(
            rejected.Id, new ConvertQuoteRequest(rejected.Version), "t22-02-after-reject-" + suffix));
        Assert.Equal(ApiErrorCodes.QuoteNotAccepted, convertAfterReject.Code);
    }

    [Fact]
    public async Task T22_03_a_quote_is_issued_and_read_locally_with_no_mail_service_and_no_fabricated_delivery()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var quotes = new QuoteApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var draft = await quotes.CreateAsync(new CreateQuoteRequest(
            _fixture.BusinessA.LocationId, null, null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)), null,
            "cash on delivery, 14 days",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 1m)]),
            "t22-03-create-" + suffix);

        // A draft is not communicated: no audit of a send exists yet, and there is no revision to print.
        Assert.Equal("Draft", draft.Status);
        Assert.Empty(draft.Revisions);
        Assert.Null(draft.SentAtUtc);
        Assert.Equal(0, await AuditCountAsync(draft.Id, "sent"));

        var sent = await quotes.SendAsync(draft.Id, new QuoteCommandRequest(draft.Version, "printed and handed over"));

        // "Sent" is a local, audited fact: the timestamp, the actor, the revision and the recorded note - and
        // nothing that would claim a message left this machine.
        Assert.Equal("Sent", sent.Status);
        Assert.NotNull(sent.SentAtUtc);
        Assert.Equal(1, await AuditCountAsync(sent.Id, "sent"));

        var revision = Assert.Single(sent.Revisions);

        Assert.Equal("printed and handed over", revision.Reason);
        Assert.Equal(sent.TotalAmount, revision.TotalAmount);
        Assert.Equal(sent.TaxAmount, revision.TaxAmount);
        Assert.Equal(sent.Currency, revision.Currency);
        Assert.Equal(revision.Lines.Sum(line => line.LinePayableAmount), revision.TotalAmount);

        // The product itself carries no mail or notification dependency, and the reprint needs none: it is composed
        // from this snapshot, so a quote stays readable and printable with no network at all.
        var offenders = new[] { "MailKit", "SmtpClient", "System.Net.Mail", "SendGrid", "AmazonSES", "Hormund" }
            .Where(dependency => ProjectReferences().Any(
                project => project.Contains(dependency, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offenders);

        var startup = await File.ReadAllTextAsync(Path.Combine(
            InvoiceEditorTests.RepositoryRoot(), @"QtsWebApp/QtsWebApp/Program.cs"));
        Assert.DoesNotContain("AddSmtp", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IEmailSender", startup, StringComparison.OrdinalIgnoreCase);

        // The rejection reason is required, so a lost customer decision is never recorded without one.
        var noReason = await Assert.ThrowsAsync<ApiException>(() => quotes.RejectAsync(sent.Id, new QuoteCommandRequest(sent.Version, "  ")));
        Assert.Equal(400, noReason.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, noReason.Code);

        var rejected = await quotes.RejectAsync(sent.Id, new QuoteCommandRequest(sent.Version, "buyer went elsewhere"));
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(1, await AuditCountAsync(rejected.Id, "rejected"));

        // The quote and its document survive rejection unchanged - history is read, not repaired.
        Assert.Equal(revision.TotalAmount, Assert.Single(rejected.Revisions).TotalAmount);
    }

    [Fact]
    public async Task T22_04_the_order_screens_ship_as_shared_pages_wired_into_every_host()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains(typeof(Quotes).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template.Trim('/') == "orders/quotes");
        Assert.Contains(typeof(QuoteEditor).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template.Trim('/') == "orders/quotes/{QuoteId:guid}");
        Assert.Contains(typeof(SalesOrders).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template.Trim('/') == "orders/sales-orders/{OrderId:guid}");

        var quoteRow = Assert.Single(ShellNavigation.Routes, row => row.Href == "orders/quotes");
        var orderRow = Assert.Single(ShellNavigation.Routes, row => row.Href == "orders/sales-orders");
        Assert.Equal(ShellAvailability.Available, quoteRow.Availability);
        Assert.Equal(ShellAvailability.Available, orderRow.Availability);
        Assert.Equal(Permissions.OrdersManage, quoteRow.RequiredPermission);

        var injected = new[]
        {
            typeof(IQuoteApiClient),
            typeof(ISalesOrderApiClient),
            typeof(ICustomerApiClient),
            typeof(QtsBilling.Contracts.Catalog.IProductApiClient),
            typeof(QtsBilling.Contracts.Pricing.IPricingApiClient),
            typeof(IAccountApiClient),
        };

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the order screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp/QtsWebApp.Client/Program.cs", @"QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The markup asks for no identifier a person must type and reaches for no public resource.
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Orders/Quotes.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Orders/QuoteEditor.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Orders/QuoteEditor.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Orders/SalesOrders.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui/Pages/Orders/SalesOrders.razor.cs")));

        Assert.DoesNotContain("Quote id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Order id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", markup, StringComparison.Ordinal);

        // Keys are held by the screen per intention and always fit the header the server validates.
        Assert.True(OrderRules.NewConversionKey("QT-2026-0001").Length <= 64);
        Assert.True(OrderRules.NewCommandKey("quote-draft").Length <= 64);
    }

    [Fact]
    public async Task T22_05_an_order_amendment_keeps_delivered_quantities_and_restates_only_the_remainder()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var quotes = new QuoteApiClient(owner);
        var orders = new SalesOrderApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var factsBefore = await CaptureLedgerAsync();

        var quote = await quotes.CreateAsync(new CreateQuoteRequest(
            _fixture.BusinessA.LocationId, null, null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21)), null,
            "amendment journey",
            [new QuoteLineRequest(_fixture.BusinessA.VariantId, "Fixture variant", 4m)]),
            "t22-05-create-" + suffix);

        quote = await quotes.SendAsync(quote.Id, new QuoteCommandRequest(quote.Version, "sent"));
        quote = await quotes.AcceptAsync(quote.Id, new QuoteCommandRequest(quote.Version));

        var converted = await quotes.ConvertAsync(
            quote.Id, new ConvertQuoteRequest(quote.Version), "t22-05-convert-" + suffix);

        var confirmed = await orders.ConfirmAsync(
            converted.Order.Id, new ConfirmSalesOrderRequest(converted.Order.Version, "confirmed for the journey"));

        var line = Assert.Single(confirmed.Lines);
        var delivered = OrderRules.Outstanding(line);

        // The screen's rows are driven by the outstanding figure, never the ordered one.
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(delivered, OrderRules.Outstanding(line));
        Assert.True(OrderRules.IsOpen(line));

        // Halving what is still outstanding is allowed; asking for more than was ordered is not something the
        // screen offers, and the amendment that keeps 2 of 4 becomes a new revision with its reason.
        var amended = await orders.AmendAsync(
            confirmed.Id,
            new AmendSalesOrderRequest(
                confirmed.Version,
                confirmed.PromisedDeliveryDate,
                "buyer reduced the order",
                [new SalesOrderAmendmentLineRequest(
                    line.LineKey, line.VariantId, line.Description, 2m, null, 0m, null, null)]));

        Assert.Equal(2, amended.CurrentRevision);
        Assert.Equal(2, amended.Revisions.Count);
        Assert.Equal("buyer reduced the order", amended.Revisions[1].Reason);

        var original = Assert.Single(amended.Lines, row => row.LineNo == line.LineNo);

        // The original line's ordered quantity is history: untouched, with the remainder cancelled against it.
        Assert.Equal(4m, original.Quantity);
        Assert.Equal(0m, original.QuantityFulfilled ?? 0m);
        Assert.Equal(4m, original.QuantityCancelled);

        var replacement = amended.Lines.Single(row => row.LineNo != line.LineNo);

        Assert.Equal(2m, replacement.Quantity);
        Assert.Equal(line.UnitPrice, replacement.UnitPrice);
        Assert.Equal(0m, OrderRules.Outstanding(original));
        Assert.Equal(2m, OrderRules.Outstanding(replacement));

        // A cancellation of the remainder needs its reason and closes the order for future delivery.
        var noReason = await Assert.ThrowsAsync<ApiException>(() => orders.CancelRemainderAsync(
            amended.Id, new CancelOrderRemainderRequest(amended.Version, " ")));
        Assert.Equal(400, noReason.StatusCode);

        var closed = await orders.CancelRemainderAsync(
            amended.Id, new CancelOrderRemainderRequest(amended.Version, "customer cancelled the rest"));

        Assert.Equal("Closed", closed.Status);
        Assert.Equal("customer cancelled the rest", closed.RemainderCancellationReason);
        Assert.Empty(OrderRules.AllowedOrderActions(closed.Status));
        Assert.All(closed.Lines, row => Assert.False(OrderRules.IsOpen(row)));

        // Through all of it nothing was delivered, invoiced or posted: a commitment is not a sale. Compared with
        // the baseline the test started from, because the fixture's own opening stock is already in these tables.
        Assert.Equal(factsBefore, await CaptureLedgerAsync());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(int Journals, int Movements, int BalanceRows)> CaptureFactsAsync()
        => await _fixture.WithFreshContextAsync(async db => (
            await db.JournalEntries.AsNoTracking().CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId),
            await db.InventoryMovements.AsNoTracking().CountAsync(movement => movement.BusinessId == _fixture.BusinessA.BusinessId),
            await db.InventoryBalances.AsNoTracking().CountAsync(balance => balance.BusinessId == _fixture.BusinessA.BusinessId)));

    /// <summary>Every financial and stock fact a quote or an order must never touch.</summary>
    private async Task<(int Journals, int Movements, int Invoices, int Sales, int BalanceRows)> CaptureLedgerAsync()
        => await _fixture.WithFreshContextAsync(async db => (
            await db.JournalEntries.AsNoTracking().CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId),
            await db.InventoryMovements.AsNoTracking().CountAsync(movement => movement.BusinessId == _fixture.BusinessA.BusinessId),
            await db.Invoices.AsNoTracking().CountAsync(invoice => invoice.BusinessId == _fixture.BusinessA.BusinessId),
            await db.Sales.AsNoTracking().CountAsync(sale => sale.BusinessId == _fixture.BusinessA.BusinessId),
            await db.InventoryBalances.AsNoTracking().CountAsync(balance => balance.BusinessId == _fixture.BusinessA.BusinessId)));

    private async Task<int> AuditCountAsync(Guid quoteId, string action)
        => await _fixture.WithFreshContextAsync(db => db.AuditEvents.AsNoTracking().CountAsync(row =>
            row.BusinessId == _fixture.BusinessA.BusinessId
            && row.EntityType == "sales_quote"
            && row.EntityId == quoteId
            && row.Action == action));

    private static IEnumerable<string> ProjectReferences()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(path => File.ReadAllText(path));
    }
}
