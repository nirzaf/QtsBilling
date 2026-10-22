using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Purchasing;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The payables workbench as a shipped screen (specification task 27, cases T27.01 - T27.05 from the operator's
/// side): the page exists in the shared assembly, every host that renders it can resolve what it injects, its markup
/// asks for no identifier a person could not know, and the reads it relies on answer with the names, quantities and
/// control accounts an operator needs in order to decide anything. The recognition arithmetic itself belongs to
/// <see cref="SupplierCreditTests"/>; this file proves the screen is reachable, honest about what it can do, and
/// unable to invent a payable.
/// </summary>
public sealed class PayablesJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PayablesJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T27_the_rules_keep_a_matched_bill_a_credit_and_a_settlement_apart()
    {
        // A draft can only be issued; an issued or partly settled bill can be settled, credited, or paid down with a
        // held credit; a void bill offers nothing. Nothing here pretends to edit a posted payable.
        Assert.Equal([PayableAction.Issue], PayablesRules.AllowedActions(SupplierBillState.Draft));
        Assert.Contains(PayableAction.Settle, PayablesRules.AllowedActions(SupplierBillState.Issued));
        Assert.Contains(PayableAction.Credit, PayablesRules.AllowedActions(SupplierBillState.PartiallyPaid));
        Assert.Contains(PayableAction.ApplyCredit, PayablesRules.AllowedActions(SupplierBillState.Paid));
        Assert.Empty(PayablesRules.AllowedActions(SupplierBillState.Void));
        Assert.False(PayablesRules.CanSettle(mayPostAccounting: false));
        Assert.True(PayablesRules.CanCreditBill(mayPostAccounting: true));

        // The credit ceiling is the bill line's own remaining quantity, never a figure the screen invents.
        var line = BillLine(billed: 10m, credited: 4m);
        Assert.Equal(6m, PayablesRules.CreditableQuantity(line));
        Assert.True(PayablesRules.IsOpen(line));
        Assert.Equal(0m, PayablesRules.CreditableQuantity(BillLine(billed: 10m, credited: 10m)));
        Assert.False(PayablesRules.IsOpen(BillLine(billed: 4m, credited: 9m)));

        // The three rules that make a payable trustworthy are stated in the screen's own words.
        Assert.Contains("matched to the goods receipt lines", PayablesRules.MatchRule, StringComparison.Ordinal);
        Assert.Contains("never folded into the stock value", PayablesRules.MatchRule, StringComparison.Ordinal);
        Assert.Contains("nothing is owed", PayablesRules.IssueRule, StringComparison.Ordinal);
        Assert.Contains("never removes stock by itself", PayablesRules.CreditRule, StringComparison.Ordinal);
        Assert.Contains("refused whole", PayablesRules.CreditRule, StringComparison.Ordinal);
        Assert.Contains("never shown as money received", PayablesRules.HeldCreditRule, StringComparison.Ordinal);
        Assert.Contains("never both at once", PayablesRules.SettlementRule, StringComparison.Ordinal);
        Assert.Contains("never rewritten", PayablesRules.OpeningRule, StringComparison.Ordinal);
        Assert.Contains("cannot appear", PayablesRules.AsOfRule, StringComparison.Ordinal);

        // Documents are named the way an operator names them, and an opening is never dressed up as a bill.
        Assert.Equal("Bill SB-2026-0007", PayablesRules.DocumentText(OpenItem("bill", "SB-2026-0007")));
        Assert.Equal("Opening payable PAYOPEN-3", PayablesRules.DocumentText(OpenItem("opening", "PAYOPEN-3")));
        Assert.Equal("supplier credit", PayablesRules.KindText("credit"));
        Assert.Equal("imported opening payable", PayablesRules.KindText("opening"));
        Assert.Equal("issued, unpaid", PayablesRules.StatusText(SupplierBillState.Issued));
        Assert.Equal("settled", PayablesRules.StatusText(SupplierBillState.Paid));

        // Money is read at the scale the statement was composed in, and a whole figure still reads as money.
        var statement = Statement(scale: 2);
        Assert.Equal("690.00", PayablesRules.Money(690m, statement));
        Assert.Equal("640.00 QAR", PayablesRules.MoneyText(640m, statement));
        Assert.Equal("690.00", PayablesRules.Money(690m, null));
        Assert.Contains("Reconciled", PayablesRules.ReconciliationText(statement), StringComparison.Ordinal);

        // A control account that does not tie to the documents is said so rather than smoothed over.
        Assert.Contains("Not reconciled",
            PayablesRules.ReconciliationText(statement with { PayableControlBalance = 700m }), StringComparison.Ordinal);

        // Refusals name the rule and the answer, not just the status.
        string Sentence(string code, string message = "refused") => PayablesRules.RefusalSentence(
            new ApiException(409, code, message, new Dictionary<string, string[]>()), "this credit");

        Assert.Contains("not an issued payable", Sentence(ApiErrorCodes.SupplierBillNotIssued), StringComparison.Ordinal);
        Assert.Contains("read the bill back", Sentence(ApiErrorCodes.OverCredit), StringComparison.Ordinal);
        Assert.Contains("Credit the bill that holds it", Sentence(ApiErrorCodes.DuplicateSupplierReference), StringComparison.Ordinal);
        Assert.Contains("Nothing new was posted", Sentence(ApiErrorCodes.SameKeyDifferentPayload), StringComparison.Ordinal);
        Assert.Contains("read back", Sentence(ApiErrorCodes.OptimisticConcurrencyFailure), StringComparison.Ordinal);
        Assert.Contains("not reopened by a screen", Sentence(ApiErrorCodes.AccountingPeriodClosed), StringComparison.Ordinal);
        Assert.Contains("re-read before the same intention", Sentence(ApiErrorCodes.Conflict), StringComparison.Ordinal);
        Assert.Contains("nothing was posted against it", Sentence(ApiErrorCodes.NotFound), StringComparison.Ordinal);
        Assert.Contains("already recorded a document", Sentence(ApiErrorCodes.IdempotencyKeyReused), StringComparison.Ordinal);

        // Only codes that prove nothing was written release the intention; an unknown answer keeps key and body.
        Assert.True(PayablesRules.CommittedNothing(ApiErrorCodes.OverCredit));
        Assert.True(PayablesRules.CommittedNothing(ApiErrorCodes.SupplierBillNotIssued));
        Assert.False(PayablesRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(PayablesRules.CommittedNothing(code: null));

        // One key per intention, bounded by the header the server validates, distinct per attempt and per document.
        var billId = Guid.CreateVersion7();
        var key = PayablesRules.NewKey("settle", billId);
        Assert.True(key.Length <= PayablesRules.MaxKeyLength);
        Assert.StartsWith("settle-", key, StringComparison.Ordinal);
        Assert.NotEqual(key, PayablesRules.NewKey("settle", billId));
        Assert.NotEqual(key, PayablesRules.NewKey("credit", billId));
    }

    [Fact]
    public async Task T27_the_payables_screen_is_wired_into_every_host_that_can_show_it()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains("purchasing/payables", typeof(Payables)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));

        var row = Assert.Single(ShellNavigation.Routes, route => route.Href == "purchasing/payables");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.Equal(Permissions.PurchasingManage, row.RequiredPermission);
        Assert.Null(row.ArrivalNote);

        var injected = typeof(Payables)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IPurchasingApiClient), injected);
        Assert.Contains(typeof(ISupplierApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);
        Assert.Contains(typeof(IInventoryApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the payables screen but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The shipped markup asks for no key, reaches for no public resource, and hardcodes no scale or clock.
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Purchasing/Payables.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Purchasing/Payables.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Purchasing/PayablesRules.cs")));

        foreach (var forbidden in new[]
        {
            "Bill id", "Supplier id", "Receipt line id", "Credit id", "Open item id", "access_token",
            "http://", "https://", "ToString(\"N2\")", "DateTime.Today", "Guid.Parse",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        Assert.Contains("search by name", markup, StringComparison.Ordinal);
        Assert.Contains("search by number", markup, StringComparison.Ordinal);
        Assert.Contains("Read the account back", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T27_the_account_read_names_documents_and_the_screen_cannot_reach_another_business_payable()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var purchasing = new PurchasingApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A payable cannot be posted without accounts to post to, so the chart is established first - the same
        // choice an implementation partner makes on the setup screen, and the reason a bill refuses rather than
        // guessing.
        await _fixture.WithFreshContextAsync(db =>
            FixtureAccounting.EnsurePayablesAsync(db, _fixture.BusinessA.BusinessId));

        // A draft bill the operator matched to a delivery, read back the way the payables screen reads it.
        var supplier = await CreateSupplierAsync(http, $"Payables screen {suffix}");
        var order = await purchasing.CreateOrderAsync(new SavePurchaseOrderRequest(
            supplier,
            _fixture.BusinessA.LocationId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            $"PO for payables {suffix}",
            [new PurchaseOrderLineRequest(_fixture.BusinessA.VariantId, 8m, 50m, 0m, 0m)]));
        var placed = await purchasing.ConfirmOrderAsync(order.Order.Id);
        var receipt = await purchasing.ReceiveAsync(
            placed.Order.Id,
            new PurchaseReceiptRequest("delivery for the payables screen",
                [new PurchaseReceiptLineRequest(Assert.Single(placed.Lines).Id, 8m)]),
            $"t27-screen-receive-{suffix}");

        var bill = await purchasing.CreateBillAsync(new CreateSupplierBillRequest(
            supplier,
            _fixture.BusinessA.LocationId,
            placed.Order.Id,
            $"SUP-{suffix}",
            DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20),
            [new SupplierBillLineRequest(receipt.Lines[0].Id, 8m, 55m, 0m, 0.15m, "Exclusive")]),
            $"t27-screen-bill-{suffix}");

        // The delivered line is still fully unbilled while the bill is a draft: a draft consumes nothing, which is
        // what bounds the next bill the screen offers - and the ceiling counts from issued bills only.
        var unbilled = (await purchasing.ListReceiptsAsync(placed.Order.Id))
            .SelectMany(row => row.Lines)
            .Single(row => row.Id == receipt.Lines[0].Id);
        Assert.Equal(8m, unbilled.Quantity);
        Assert.Equal(0m, unbilled.QuantityBilled);

        // The drafts list a screen offers to issue names the supplier, the branch and the supplier's own reference:
        // no operator has to recognise a document by a key.
        var drafts = await purchasing.ListBillsAsync(supplier);
        var draft = Assert.Single(drafts, row => row.Id == bill.Id);
        Assert.Equal(SupplierBillState.Draft, draft.Status);
        Assert.Equal("Payables screen " + suffix, draft.SupplierName);
        Assert.True(draft.LocationName is { Length: > 0 });
        Assert.Equal(placed.Order.OrderNumber, draft.OrderNumber);
        Assert.Equal($"SUP-{suffix}", draft.SupplierInvoiceReference);
        Assert.Equal(506m, draft.TotalAmount);
        Assert.Null(draft.JournalEntryNumber);

        // Issuing opens the payable, numbers the entry, and the statement then reads the account with its scale,
        // its zone and its control figures - everything the payables panel shows without computing any of it.
        var issued = await purchasing.IssueBillAsync(bill.Id, $"t27-screen-issue-{suffix}");
        Assert.Equal(SupplierBillState.Issued, issued.Status);
        Assert.True(issued.JournalEntryNumber is { Length: > 0 });
        Assert.Equal(0m, issued.CreditedAmount);
        Assert.Empty(issued.Credits!);

        // Once the bill is issued the delivery line carries what has been billed, which is the ceiling for the next
        // bill the screen offers against that delivery.
        var billedLine = (await purchasing.ListReceiptsAsync(placed.Order.Id))
            .SelectMany(row => row.Lines)
            .Single(row => row.Id == receipt.Lines[0].Id);
        Assert.Equal(8m, billedLine.QuantityBilled);

        var statement = await purchasing.GetStatementAsync(supplier);
        Assert.Equal(supplier, statement.SupplierId);
        Assert.Equal("Payables screen " + suffix, statement.SupplierName);
        Assert.Equal(506m, statement.ClosingBalance);
        Assert.Equal(506m, Assert.Single(statement.OpenItems).RemainingAmount);
        Assert.Equal("bill", Assert.Single(statement.OpenItems).TargetKind);
        Assert.Equal(2, statement.SettlementScale);
        Assert.True(statement.TimeZoneId is { Length: > 0 });
        Assert.Contains(statement.Lines, line => line.Kind == "bill" && line.CreditAmount == 506m);
        Assert.Equal(0m, statement.UnappliedCredits);

        // The supplier's own invoice number may not be entered twice - the refusal names the field, so the screen
        // can say which box to fix instead of showing a status code.
        var duplicate = await CaptureAsync(() => purchasing.CreateBillAsync(new CreateSupplierBillRequest(
            supplier,
            _fixture.BusinessA.LocationId,
            placed.Order.Id,
            $"SUP-{suffix}",
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            [new SupplierBillLineRequest(receipt.Lines[0].Id, 1m, 55m, 0m, 0m, "Exclusive")]),
            $"t27-screen-duplicate-{suffix}"));
        Assert.Contains("supplierInvoiceReference", duplicate.Errors.Keys, StringComparer.Ordinal);
        Assert.Contains("already on file", string.Join(' ', duplicate.Errors["supplierInvoiceReference"]), StringComparison.OrdinalIgnoreCase);

        // Another business cannot reach this payable by guessing its identifier: it is not there, and nothing is
        // disclosed about it - which is what the screen's "this business holds no such document" sentence answers.
        var otherBusiness = new PurchasingApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB));
        var foreign = await CaptureAsync(() => otherBusiness.GetBillAsync(bill.Id));
        Assert.Equal(404, foreign.StatusCode);
        var foreignStatement = await CaptureAsync(() => otherBusiness.GetStatementAsync(supplier));
        Assert.Equal(404, foreignStatement.StatusCode);

        // A role that may not hold purchasing authority is refused the read, and the bill is exactly as it was.
        var viewer = new PurchasingApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer));
        var refused = await CaptureAsync(() => viewer.ListBillsAsync(supplier));
        Assert.True(refused.StatusCode is 401 or 403, $"the payables read answered {refused.StatusCode}, expected a refusal");
        Assert.Equal(SupplierBillState.Issued, (await purchasing.GetBillAsync(bill.Id)).Status);
        Assert.Equal(506m, (await purchasing.GetStatementAsync(supplier)).ClosingBalance);
    }

    private static SupplierBillLineDto BillLine(decimal billed, decimal credited)
        => new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test item", billed, 60m,
            billed * 60m, 0m, 0.15m, "Exclusive", billed * 60m, billed * 9m, billed * 69m, credited);

    private static SupplierOpenItemDto OpenItem(string targetKind, string reference)
        => new(targetKind, Guid.CreateVersion7(), reference, new DateOnly(2026, 3, 4), new DateOnly(2026, 4, 3),
            690m, 0m, 690m, "QAR", false, null);

    private static SupplierStatementDto Statement(int scale) => new(
        Guid.CreateVersion7(),
        "Van supply",
        "QAR",
        new DateOnly(2026, 4, 4),
        DateTimeOffset.UnixEpoch,
        OpeningBalance: 0m,
        MovementDebit: 0m,
        MovementCredit: 690m,
        ClosingBalance: 690m,
        UnappliedCredits: 50m,
        PayableControlBalance: 640m,
        GrniBalance: 0m,
        OpenDocumentCount: 1,
        Truncated: false,
        Lines: [],
        OpenItems: [OpenItem("bill", "SB-2026-0007")],
        Credits: [],
        SettlementScale: scale,
        TimeZoneId: "Asia/Qatar");

    private static async Task<Guid> CreateSupplierAsync(HttpClient http, string name)
    {
        var suppliers = new SupplierApiClient(http);
        var created = await suppliers.CreateSupplierAsync(new SaveSupplierRequest(
            name,
            $"payables-{Guid.NewGuid():N}@fixture.test",
            $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
            null));

        return created.Id;
    }

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
