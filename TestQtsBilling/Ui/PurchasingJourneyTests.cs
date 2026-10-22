using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Purchasing;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// Procurement as a shipped screen (specification task 26, cases T26.01 - T26.03). An order is a commitment that
/// moves nothing; a goods received note is the moment stock and the ledger both move, only by the quantities the
/// server accepted; and a document whose answer was lost is re-asked about under its own key instead of being
/// invented twice. The cases run over the typed client the screens use, so what is proven here is the path a
/// browser or a terminal actually takes, not a parallel test-only API.
/// </summary>
public sealed class PurchasingJourneyTests : IClassFixture<PostgresApiFixture>
{
    private const string ReceiptReferenceType = "purchase_receipt";
    private const string ReturnReferenceType = "purchase_return";

    private readonly PostgresApiFixture _fixture;

    public PurchasingJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T26_the_rules_separate_a_promise_a_delivery_and_a_bill()
    {
        // A draft can be edited, deleted or placed; a placed order can receive, return, close or print; a closed
        // one is a record. A button that could only be refused is not offered.
        Assert.Equal(
            [PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Place],
            PurchasingRules.AllowedActions(PurchaseOrderState.Draft));
        Assert.Contains(PurchaseOrderAction.Receive, PurchasingRules.AllowedActions(PurchaseOrderState.Ordered));
        Assert.Contains(PurchaseOrderAction.Return, PurchasingRules.AllowedActions(PurchaseOrderState.PartiallyReceived));
        Assert.DoesNotContain(PurchaseOrderAction.Edit, PurchasingRules.AllowedActions(PurchaseOrderState.Ordered));
        Assert.Equal([PurchaseOrderAction.Print], PurchasingRules.AllowedActions(PurchaseOrderState.Received));
        Assert.Equal([PurchaseOrderAction.Print], PurchasingRules.AllowedActions(PurchaseOrderState.Cancelled));

        var line = Line(quantityOrdered: 10m, quantityReceived: 4m);
        Assert.Equal(6m, PurchasingRules.Outstanding(line));
        Assert.Equal(4m, PurchasingRules.Returnable(line));
        Assert.True(PurchasingRules.IsOpen(line));

        // Cancelling closes what never arrived, and a line can never owe or return less than nothing.
        var closed = Line(quantityOrdered: 10m, quantityReceived: 10m);
        Assert.Equal(0m, PurchasingRules.Outstanding(closed));
        Assert.False(PurchasingRules.IsOpen(closed));
        Assert.Equal(0m, PurchasingRules.Outstanding(Line(quantityOrdered: 5m, quantityReceived: 7m)));

        // The three facts are stated apart, because collapsing them is exactly the phantom stock and the double payment.
        Assert.Contains("moves no stock", PurchasingRules.CommitmentRule, StringComparison.Ordinal);
        Assert.Contains("liability is recognised", PurchasingRules.ReceiptRule, StringComparison.Ordinal);
        Assert.Contains("never a negative receipt", PurchasingRules.ReturnRule, StringComparison.Ordinal);
        Assert.Contains("payables workbench (task 27)", PurchasingRules.BillingRule, StringComparison.Ordinal);
        Assert.Contains("overwrite", PurchasingRules.NoManualStockRule, StringComparison.Ordinal);

        // Every refusal the server can answer with is stated with the action that resolves it.
        string Sentence(string code, string message = "refused") => PurchasingRules.RefusalSentence(
            new ApiException(409, code, message, new Dictionary<string, string[]>()), "this delivery");

        Assert.Contains("no longer a draft", Sentence(ApiErrorCodes.PurchaseOrderNotDraft), StringComparison.Ordinal);
        Assert.Contains("not open for deliveries", Sentence(ApiErrorCodes.PurchaseOrderNotOpen), StringComparison.Ordinal);
        Assert.Contains("came from somewhere else", Sentence(ApiErrorCodes.OverReceipt), StringComparison.Ordinal);
        Assert.Contains("Nothing new was posted", Sentence(ApiErrorCodes.SameKeyDifferentPayload), StringComparison.Ordinal);
        Assert.Contains("read back", Sentence(ApiErrorCodes.OptimisticConcurrencyFailure), StringComparison.Ordinal);
        Assert.Contains("not reopened by a screen", Sentence(ApiErrorCodes.AccountingPeriodClosed), StringComparison.Ordinal);
        Assert.Contains("nothing was posted against it", Sentence(ApiErrorCodes.NotFound), StringComparison.Ordinal);

        // Only codes that prove nothing was written release the intention; a lost answer keeps the key and the body.
        Assert.True(PurchasingRules.CommittedNothing(ApiErrorCodes.OverReceipt));
        Assert.True(PurchasingRules.CommittedNothing(ApiErrorCodes.PurchaseOrderNotOpen));
        Assert.False(PurchasingRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(PurchasingRules.CommittedNothing(code: null));

        var first = PurchasingRules.NewKey("receive", "PO-2026-0007");
        Assert.True(first.Length <= PurchasingRules.MaxKeyLength, $"a key must fit the header the server validates: {first}");
        Assert.StartsWith("receive-PO-2026-0007-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, PurchasingRules.NewKey("receive", "PO-2026-0007"));
        Assert.NotEqual(first, PurchasingRules.NewKey("return", "PO-2026-0007"));

        // The reference and the note both reach the document: the contract carries one free-text field, so the
        // screen composes them instead of dropping what the operator typed.
        Assert.Equal("DV-41 · two crates short", PurchasingRules.CombineNote("  DV-41 ", " two crates short "));
        Assert.Equal("DV-41", PurchasingRules.CombineNote("DV-41", "   "));
        Assert.Null(PurchasingRules.CombineNote(null, null));

        Assert.Equal("goods received note", PurchasingRules.KindText("Receipt"));
        Assert.Equal("return", PurchasingRules.KindText("Return"));
        Assert.True(PurchasingRules.IsReturn("Return"));
        Assert.False(PurchasingRules.IsReturn("Receipt"));
        Assert.Equal("JE-2026-0004 · 1200, 2101", PurchasingRules.LedgerText(
            Receipt("GRN-1", journal: "JE-2026-0004", accounts: ["1200", "2101"])));
        Assert.Equal("JE-2026-0004", PurchasingRules.LedgerText(Receipt("GRN-1", journal: "JE-2026-0004", accounts: [])));
        Assert.Contains("nothing posted", PurchasingRules.LedgerText(Receipt("GRN-1", journal: null, accounts: null)), StringComparison.Ordinal);

        // A figure is read at the precision it was stored at, never trimmed to an invented two decimals; whole
        // settlement figures still read as money, and a cost with four decimals keeps what it cost.
        Assert.Equal("60.0045", PurchasingRules.Money(60.0045m));
        Assert.Equal("60.00", PurchasingRules.Money(60m));
        Assert.Equal("60.0004 QAR", PurchasingRules.MoneyText(60.0004m, "QAR"));
        Assert.Equal("10", PurchasingRules.QuantityText(10m));
        Assert.Equal("2.5", PurchasingRules.QuantityText(2.5m));

        var presented = PurchasingRules.PresentationOf("Asia/Qatar");
        Assert.Equal("Asia/Qatar", presented?.TimeZoneId);
        Assert.Null(PurchasingRules.PresentationOf(null));
        Assert.Null(PurchasingRules.PresentationOf(""));
    }

    [Fact]
    public async Task T26_the_screens_are_wired_into_every_host_that_can_show_them()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        // Each route exists in the shared assembly, and the shell offers it as reachable rather than promised.
        Assert.Contains("purchasing/orders", RouteTemplates(typeof(PurchaseOrders)));
        Assert.Contains("purchasing/orders/{OrderId:guid}", RouteTemplates(typeof(PurchaseOrderEditor)));
        Assert.Contains("purchasing/receiving", RouteTemplates(typeof(Receiving)));
        Assert.Contains("purchasing/receiving/{OrderId:guid}", RouteTemplates(typeof(Receiving)));

        Assert.Equal(ShellAvailability.Available, Single("purchasing/orders").Availability);
        Assert.Equal(ShellAvailability.Available, Single("purchasing/receiving").Availability);
        Assert.Equal(Permissions.PurchasingManage, Single("purchasing/orders").RequiredPermission);
        Assert.Null(Single("purchasing/receiving").ArrivalNote);

        // The payables row stays planned: this task must not open a door that task 27 has not built behind it.
        Assert.Equal(ShellAvailability.Planned, Single("purchasing/payables").Availability);

        var pages = new[] { typeof(PurchaseOrders), typeof(PurchaseOrderEditor), typeof(Receiving) };
        var injected = pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IPurchasingApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);
        Assert.Contains(typeof(IInventoryApiClient), injected);
        Assert.Contains(typeof(IDocumentOutput), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the purchasing screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp/QtsWebApp.Client/Program.cs", @"QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // No screen asks a person for a key, none reaches for a public resource, none invents a scale or a clock.
        // Every file is named first, so a screen that was renamed away from the proof fails loudly instead of
        // quietly reading an empty set.
        var markupPaths = new List<string>
        {
            Path.Combine(root, @"QtsBilling.Ui/Pages/Purchasing/PurchasingRules.cs"),
            Path.Combine(root, @"QtsBilling.Ui/Pages/Purchasing/PurchaseDocument.cs"),
        };

        foreach (var page in pages)
        {
            markupPaths.Add(Path.Combine(root, @"QtsBilling.Ui/Pages/Purchasing", $"{page.Name}.razor"));
            markupPaths.Add(Path.Combine(root, @"QtsBilling.Ui/Pages/Purchasing", $"{page.Name}.razor.cs"));
        }

        foreach (var path in markupPaths)
        {
            Assert.True(File.Exists(path), $"the procurement workbench no longer has {path}");
        }

        var markup = string.Concat(markupPaths.Select(File.ReadAllText));

        foreach (var forbidden in new[]
        {
            "Order id", "Supplier id", "Variant id", "Location id", "access_token", "http://", "https://",
            "ToString(\"N2\")", "DateTime.Today", "Guid.Parse",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        Assert.Contains("search by name", markup, StringComparison.Ordinal);
        Assert.Contains("Output.PresentAsync", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T26_01_placing_an_order_commits_but_moves_no_stock_and_posts_nothing()
    {
        var (http, purchasing) = await WorkbenchAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var supplier = await CreateSupplierAsync(http, $"Van supply {suffix}");
        var draft = await purchasing.CreateOrderAsync(NewOrder(supplier, $"PO commitment {suffix}", 12m, 60m));
        var orderId = draft.Order.Id;

        var onHandBefore = await OnHandAsync();
        var movementsBefore = await CountAsync<InventoryMovement>(movement =>
            movement.BusinessId == _fixture.BusinessA.BusinessId
            && (movement.ReferenceType == ReceiptReferenceType || movement.ReferenceType == ReturnReferenceType));
        var journalsBefore = await CountAsync<JournalEntry>(entry =>
            entry.BusinessId == _fixture.BusinessA.BusinessId && entry.SourceType == JournalSourceType.Purchase);

        // Placing is the commitment. Nothing else changes.
        var placed = await purchasing.ConfirmOrderAsync(orderId);
        Assert.Equal(PurchaseOrderState.Ordered, placed.Order.Status);

        Assert.Equal(onHandBefore, await OnHandAsync());
        Assert.Equal(movementsBefore, await CountAsync<InventoryMovement>(movement =>
            movement.BusinessId == _fixture.BusinessA.BusinessId
            && (movement.ReferenceType == ReceiptReferenceType || movement.ReferenceType == ReturnReferenceType)));
        Assert.Equal(journalsBefore, await CountAsync<JournalEntry>(entry =>
            entry.BusinessId == _fixture.BusinessA.BusinessId && entry.SourceType == JournalSourceType.Purchase));

        // And the order says so: nothing received, everything outstanding, no documents behind it.
        var reloaded = await purchasing.GetOrderAsync(orderId);
        Assert.Equal(0m, Assert.Single(reloaded.Lines).QuantityReceived);
        Assert.Equal(12m, PurchasingRules.Outstanding(Assert.Single(reloaded.Lines)));
        Assert.Empty(await purchasing.ListReceiptsAsync(orderId));

        // A draft that was never placed is deletable; a placed order is a commitment and refuses.
        var unused = await purchasing.CreateOrderAsync(NewOrder(supplier, $"PO unused {suffix}", 3m, 10m));
        await purchasing.DeleteOrderAsync(unused.Order.Id);
        var gone = await CaptureAsync(() => purchasing.GetOrderAsync(unused.Order.Id));
        Assert.Equal(404, gone.StatusCode);

        var refused = await CaptureAsync(() => purchasing.UpdateOrderAsync(orderId, NewOrder(supplier, $"PO commitment {suffix}", 99m, 1m)));
        Assert.Equal(ApiErrorCodes.PurchaseOrderNotDraft, refused.Code);
        Assert.Equal(PurchaseOrderState.Ordered, (await purchasing.GetOrderAsync(orderId)).Order.Status);
        Assert.Equal(12m, Assert.Single((await purchasing.GetOrderAsync(orderId)).Lines).QuantityOrdered);

        // The list reads the same order back by number and by state, which is how an operator finds it (R07).
        var page = await purchasing.ListOrdersAsync(PurchaseOrderState.Ordered, search: placed.Order.OrderNumber);
        var listed = Assert.Single(page.Items, row => row.Id == orderId);
        Assert.Equal(PurchaseOrderState.Ordered, listed.Status);
        Assert.Equal(60m * 12m, listed.TotalAmount);
    }

    [Fact]
    public async Task T26_02_split_deliveries_and_a_replayed_receipt_move_only_what_was_accepted()
    {
        var (http, purchasing) = await WorkbenchAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var supplier = await CreateSupplierAsync(http, $"Split van {suffix}");
        var draft = await purchasing.CreateOrderAsync(NewOrder(supplier, $"PO split {suffix}", 20m, 60m));
        var order = await purchasing.ConfirmOrderAsync(draft.Order.Id);
        var lineId = Assert.Single(order.Lines).Id;

        var start = await OnHandAsync();
        var firstKey = PurchasingRules.NewKey("receive", order.Order.OrderNumber);
        var firstBody = new PurchaseReceiptRequest(
            PurchasingRules.CombineNote($"DV-{suffix}", "first van"),
            [new PurchaseReceiptLineRequest(lineId, 7m)]);

        var first = await purchasing.ReceiveAsync(order.Order.Id, firstBody, firstKey);
        Assert.Equal(7m, Assert.Single(first.Lines).Quantity);
        Assert.Equal(13m, PurchasingRules.Outstanding(Assert.Single((await purchasing.GetOrderAsync(order.Order.Id)).Lines)));
        Assert.Equal(start + 7m, await OnHandAsync());
        Assert.True(first.JournalEntryNumber is { Length: > 0 }, "a receipt that capitalises stock must name the entry it produced");
        Assert.Contains("1200", first.ControlAccountCodes ?? []);
        Assert.Equal(420m, await PostedValueOfReceiptAsync(first, expectedUnitCost: 60m));

        // The answer was lost in reality: the screen keeps the key AND the exact body and re-sends them. The server
        // replays its own document, so one van is one goods received note - never two, and never twelve pieces.
        var replay = await purchasing.ReceiveAsync(order.Order.Id, firstBody, firstKey);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.ReceiptNumber, replay.ReceiptNumber);
        Assert.Equal(start + 7m, await OnHandAsync());
        Assert.Equal(1, await CountAsync<PurchaseReceipt>(receipt => receipt.PurchaseOrderId == order.Order.Id));
        Assert.Equal(420m, await PostedValueOfReceiptAsync(first, expectedUnitCost: 60m));

        // The second van is a new intention: a new key, a new document, and only the accepted quantity lands.
        var second = await purchasing.ReceiveAsync(
            order.Order.Id,
            new PurchaseReceiptRequest("second van", [new PurchaseReceiptLineRequest(lineId, 5m)]),
            PurchasingRules.NewKey("receive", order.Order.OrderNumber));
        Assert.Equal(start + 12m, await OnHandAsync());
        Assert.Equal(2, await CountAsync<PurchaseReceipt>(receipt => receipt.PurchaseOrderId == order.Order.Id));
        Assert.Equal(300m, await PostedValueOfReceiptAsync(second, expectedUnitCost: 60m));

        var partly = await purchasing.GetOrderAsync(order.Order.Id);
        Assert.Equal(PurchaseOrderState.PartiallyReceived, partly.Order.Status);
        Assert.Equal(12m, Assert.Single(partly.Lines).QuantityReceived);

        // A key reused for different quantities is refused outright, so a replay cannot quietly become an amendment.
        var crossed = await CaptureAsync(() => purchasing.ReceiveAsync(
            order.Order.Id,
            new PurchaseReceiptRequest("second van", [new PurchaseReceiptLineRequest(lineId, 6m)]),
            firstKey));
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, crossed.Code);
        Assert.Equal(start + 12m, await OnHandAsync());
        Assert.Equal(2, await CountAsync<PurchaseReceipt>(receipt => receipt.PurchaseOrderId == order.Order.Id));

        // A return is its own linked document at the cost the goods came in at: never a negative receipt.
        var returned = await purchasing.ReturnAsync(
            order.Order.Id,
            new PurchaseReceiptRequest($"crates went back {suffix}", [new PurchaseReceiptLineRequest(lineId, 3m)]),
            PurchasingRules.NewKey("return", order.Order.OrderNumber));
        Assert.Equal(start + 9m, await OnHandAsync());
        Assert.Equal(3m, Assert.Single(returned.Lines).Quantity);

        var returnDeltas = await _fixture.WithFreshContextAsync(async db => await db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.MovementType == MovementType.PurchaseReturn
                && db.PurchaseReceiptLines.Any(row => row.PurchaseReceiptId == returned.Id && row.Id == movement.ReferenceId))
            .Select(movement => movement.QuantityDelta)
            .ToListAsync());
        Assert.Equal([-3m], returnDeltas);
        Assert.Equal(180m, await PostedValueOfReceiptAsync(returned, expectedUnitCost: 60m));

        // The line now holds what the branch actually has - twelve arrived and three went back - while every
        // document that moved it stays on the record as its own entry with its own journal.
        var settled = await purchasing.GetOrderAsync(order.Order.Id);
        Assert.Equal(9m, Assert.Single(settled.Lines).QuantityReceived);
        Assert.Equal(11m, PurchasingRules.Outstanding(Assert.Single(settled.Lines)));
        Assert.Equal(9m, PurchasingRules.Returnable(Assert.Single(settled.Lines)));
        var documents = await purchasing.ListReceiptsAsync(order.Order.Id);
        Assert.Equal(3, documents.Count);
        Assert.Single(documents, document => PurchasingRules.IsReturn(document.Kind));
        Assert.Equal(12m, documents.Where(document => !PurchasingRules.IsReturn(document.Kind))
            .Sum(document => Assert.Single(document.Lines).Quantity));
        Assert.All(documents, document => Assert.True(document.JournalEntryNumber is { Length: > 0 }));

        // Every entry the workflow produced balances, as the ledger always must.
        await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId));
    }

    [Fact]
    public async Task T26_03_an_impossible_delivery_is_refused_whole_and_says_what_to_do()
    {
        var (http, purchasing) = await WorkbenchAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var supplier = await CreateSupplierAsync(http, $"Refused van {suffix}");
        var order = await purchasing.ConfirmOrderAsync((await purchasing.CreateOrderAsync(
            NewOrder(supplier, $"PO refusal {suffix}", 10m, 40m))).Order.Id);
        var lineId = Assert.Single(order.Lines).Id;
        var start = await OnHandAsync();

        // An order that has not been placed yet cannot receive anything: the promise and the delivery are separate.
        var unplaced = await purchasing.CreateOrderAsync(NewOrder(supplier, $"PO not placed {suffix}", 4m, 5m));
        var notOpen = await CaptureAsync(() => purchasing.ReceiveAsync(
            unplaced.Order.Id,
            new PurchaseReceiptRequest("too early", [new PurchaseReceiptLineRequest(Assert.Single(unplaced.Lines).Id, 1m)]),
            PurchasingRules.NewKey("receive", unplaced.Order.OrderNumber)));
        Assert.Equal(ApiErrorCodes.PurchaseOrderNotOpen, notOpen.Code);
        Assert.Equal(PurchaseOrderState.Draft, (await purchasing.GetOrderAsync(unplaced.Order.Id)).Order.Status);
        Assert.Empty(await purchasing.ListReceiptsAsync(unplaced.Order.Id));
        Assert.Equal(start, await OnHandAsync());

        // Receiving more than was ordered is refused, and the whole request is what is refused - not the excess.
        var over = await CaptureAsync(() => purchasing.ReceiveAsync(
            order.Order.Id,
            new PurchaseReceiptRequest("the driver unloaded extra", [new PurchaseReceiptLineRequest(lineId, 11m)]),
            PurchasingRules.NewKey("receive", order.Order.OrderNumber)));
        Assert.Equal(ApiErrorCodes.OverReceipt, over.Code);
        Assert.Equal(start, await OnHandAsync());
        Assert.Empty(await purchasing.ListReceiptsAsync(order.Order.Id));

        // Two lines in one document, one of them impossible: nothing from that document is posted, so the valid
        // line does not arrive without the other - the delivery is one fact or it is none.
        var mixedOrder = await purchasing.ConfirmOrderAsync((await purchasing.CreateOrderAsync(new SavePurchaseOrderRequest(
            supplier,
            _fixture.BusinessA.LocationId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            $"PO mixed {suffix}",
            [
                new PurchaseOrderLineRequest(_fixture.BusinessA.VariantId, 6m, 20m, 0m, 0m),
                new PurchaseOrderLineRequest(_fixture.BusinessA.VariantId, 4m, 30m, 0m, 0m),
            ]))).Order.Id);
        var mixedLines = (await purchasing.GetOrderAsync(mixedOrder.Order.Id)).Lines;
        Assert.Equal(2, mixedLines.Count);

        var mixed = await CaptureAsync(() => purchasing.ReceiveAsync(
            mixedOrder.Order.Id,
            new PurchaseReceiptRequest(
                "one line too big",
                [new PurchaseReceiptLineRequest(mixedLines[0].Id, 6m), new PurchaseReceiptLineRequest(mixedLines[1].Id, 5m)]),
            PurchasingRules.NewKey("receive", mixedOrder.Order.OrderNumber)));
        Assert.Equal(ApiErrorCodes.OverReceipt, mixed.Code);
        Assert.Equal(start, await OnHandAsync());
        Assert.Empty(await purchasing.ListReceiptsAsync(mixedOrder.Order.Id));
        Assert.Equal(0m, Assert.Single((await purchasing.GetOrderAsync(mixedOrder.Order.Id)).Lines,
            line => line.LineNo == 1).QuantityReceived);

        // A line from another order cannot ride along, even when it belongs to this business.
        var foreignLine = await CaptureAsync(() => purchasing.ReceiveAsync(
            order.Order.Id,
            new PurchaseReceiptRequest("wrong paperwork", [new PurchaseReceiptLineRequest(mixedLines[0].Id, 1m)]),
            PurchasingRules.NewKey("receive", order.Order.OrderNumber)));
        Assert.Equal(400, foreignLine.StatusCode);
        Assert.Equal(start, await OnHandAsync());
        Assert.Empty(await purchasing.ListReceiptsAsync(order.Order.Id));

        // Returning more than the branch received is the same kind of lie, and is refused the same way.
        var overReturn = await CaptureAsync(() => purchasing.ReturnAsync(
            order.Order.Id,
            new PurchaseReceiptRequest("nothing arrived to send back", [new PurchaseReceiptLineRequest(lineId, 2m)]),
            PurchasingRules.NewKey("return", order.Order.OrderNumber)));
        Assert.Equal(ApiErrorCodes.OverReceipt, overReturn.Code);
        Assert.Equal(start, await OnHandAsync());

        // A supplier from another business is not a supplier here: the draft is refused before it can be placed.
        var otherBusiness = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var foreignSupplier = await CreateSupplierAsync(otherBusiness, $"Elsewhere {suffix}");
        var borrowed = await CaptureAsync(() => purchasing.CreateOrderAsync(NewOrder(foreignSupplier, $"PO borrowed {suffix}", 2m, 2m)));
        Assert.Equal(400, borrowed.StatusCode);
        Assert.Contains("supplierId", borrowed.Errors.Keys, StringComparer.Ordinal);

        // Each refusal above has an actionable sentence the screen would have shown instead of a status code.
        Assert.Contains("not open for deliveries", PurchasingRules.RefusalSentence(notOpen, "this delivery"), StringComparison.Ordinal);
        Assert.Contains("came from somewhere else", PurchasingRules.RefusalSentence(over, "this delivery"), StringComparison.Ordinal);
        Assert.Contains("came from somewhere else", PurchasingRules.RefusalSentence(mixed, "this delivery"), StringComparison.Ordinal);
        Assert.Contains("no such order", PurchasingRules.RefusalSentence(
            new ApiException(404, ApiErrorCodes.NotFound, "absent", new Dictionary<string, string[]>()), "this delivery"), StringComparison.Ordinal);

        // And nothing was left half-posted anywhere in the workflow.
        await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId));
    }

    private sealed record Workbench(HttpClient Http, PurchasingApiClient Purchasing);

    private async Task<Workbench> WorkbenchAsync()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        return new Workbench(http, new PurchasingApiClient(http));
    }

    private static PurchaseOrderLineDto Line(decimal quantityOrdered, decimal quantityReceived)
        => new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), "Test item", "SKU-1", quantityOrdered, quantityReceived, 10m, 0m, 0m, 0m, 100m);

    private static PurchaseReceiptDto Receipt(string number, string? journal, IReadOnlyList<string>? accounts)
        => new(
            Guid.CreateVersion7(),
            number,
            "Receipt",
            Guid.CreateVersion7(),
            "PO-1",
            Guid.CreateVersion7(),
            DateTimeOffset.UnixEpoch,
            null,
            [],
            journal,
            accounts);

    private static IEnumerable<string> RouteTemplates(Type page) => page
        .GetCustomAttributes(typeof(RouteAttribute), false)
        .Cast<RouteAttribute>()
        .Select(route => route.Template.Trim('/'));

    private static ShellRoute Single(string href)
        => Assert.Single(ShellNavigation.Routes, item => item.Href == href);

    private SavePurchaseOrderRequest NewOrder(Guid supplierId, string note, decimal quantity, decimal unitCost)
        => new(
            supplierId,
            _fixture.BusinessA.LocationId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            note,
            [new PurchaseOrderLineRequest(_fixture.BusinessA.VariantId, quantity, unitCost, 0m, 0m)]);

    /// <summary>A supplier through the screen's own typed client, because that is how an operator makes one.</summary>
    private static async Task<Guid> CreateSupplierAsync(HttpClient http, string name)
    {
        var suppliers = new SupplierApiClient(http);
        var created = await suppliers.CreateSupplierAsync(new SaveSupplierRequest(
            name,
            $"{name.Split(' ')[0].ToLowerInvariant()}-{Guid.NewGuid():N}@fixture.test",
            $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
            null));

        return created.Id;
    }

    private async Task<decimal> OnHandAsync()
        => await _fixture.WithFreshContextAsync(db => db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                && balance.LocationId == _fixture.BusinessA.LocationId
                && balance.VariantId == _fixture.BusinessA.VariantId)
            .Select(balance => balance.OnHand)
            .FirstOrDefaultAsync());

    private async Task<int> CountAsync<TEntity>(Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
        => await _fixture.WithFreshContextAsync(async db => await db.Set<TEntity>().AsNoTracking().CountAsync(predicate));

    /// <summary>The value the receipt actually put through the ledger, read from the entry it produced.</summary>
    private async Task<decimal> PostedValueOfReceiptAsync(PurchaseReceiptDto receipt, decimal expectedUnitCost)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var entry = await db.JournalEntries.AsNoTracking()
                .Where(candidate => candidate.BusinessId == _fixture.BusinessA.BusinessId
                    && candidate.SourceType == JournalSourceType.Purchase
                    && candidate.SourceId == receipt.Id)
                .FirstAsync();

            Assert.Equal(receipt.JournalEntryNumber, entry.EntryNumber);

            var movements = await db.InventoryMovements.AsNoTracking()
                .Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                    && db.PurchaseReceiptLines.Any(row => row.PurchaseReceiptId == receipt.Id && row.Id == movement.ReferenceId))
                .ToListAsync();

            Assert.All(movements, movement => Assert.Equal(expectedUnitCost, movement.UnitCost));
            Assert.Equal(receipt.Lines.Count, movements.Count);

            var debit = await db.JournalLines.AsNoTracking()
                .Where(line => line.JournalEntryId == entry.Id)
                .SumAsync(line => line.DebitAmount);

            var credit = await db.JournalLines.AsNoTracking()
                .Where(line => line.JournalEntryId == entry.Id)
                .SumAsync(line => line.CreditAmount);

            Assert.Equal(debit, credit);

            return debit;
        });

    private static async Task<ApiException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException("the request should have been refused, but it succeeded");
    }
}
