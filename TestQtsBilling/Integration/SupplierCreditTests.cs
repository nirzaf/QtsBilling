using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The payable side of procure-to-pay as an operator is expected to run it (specification task 27, cases
/// T27.01 - T27.05), over the typed client the payables screen uses. A bill is matched to what was received and
/// reverses nothing by hand; a credit takes back the bill's own price for a quantity, clearing the goods-received
/// value a posted return left behind and never touching stock a second time; a settlement names exactly one open
/// document - a bill or an imported opening payable - and a credit's unused balance stays a stated figure with an
/// account of its own instead of becoming a negative payable or imaginary cash.
/// </summary>
public sealed class SupplierCreditTests : IClassFixture<PostgresApiFixture>
{
    private const string Payable = "2000";
    private const string Grni = "2101";
    private const string InputTax = "1300";
    private const string Variance = "5150";
    private const string Cash = "1000";

    private readonly PostgresApiFixture _fixture;
    private HttpClient? _http;

    public SupplierCreditTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T27_01_a_bill_at_a_changed_approved_price_clears_only_that_receipt_and_posts_the_variance_and_input_tax()
    {
        await EnsurePayablesChartAsync();
        var purchasing = await ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // The fixture business is shared by every fact in this class, so stock and control accounts are read as
        // deltas: the figures proved below are what this bill moved, not what the business happens to carry.
        var ledgerBefore = await ControlBalancesAsync();
        var stockBefore = (await OnHandAsync(_fixture.BusinessA.VariantId), await OnHandValueAsync(_fixture.BusinessA.VariantId));

        // Six of ten ordered pieces arrive, capitalised at the order's cost of 60.
        var order = await ReceiveAsync(purchasing, $"T27-01 {suffix}", ordered: 10m, received: 6m, unitCost: 60m);
        var afterReceipt = await ControlBalancesAsync();
        Assert.Equal(360m, afterReceipt[Grni] - ledgerBefore[Grni]);
        Assert.Equal(6m, (await OnHandAsync(order.VariantId)) - stockBefore.Item1);
        Assert.Equal(360m, (await OnHandValueAsync(order.VariantId)) - stockBefore.Item2);

        // The supplier's bill charges 65 for the same six pieces with 15 % input tax.
        var billId = await CreateBillAsync(purchasing, order, suffix, quantity: 6m, unitCost: 65m, taxRate: 0.15m);
        var issued = await purchasing.IssueBillAsync(billId, $"t27-01-issue-{suffix}");

        Assert.Equal(SupplierBillState.Issued, issued.Status);
        Assert.Equal(448.5m, issued.TotalAmount);
        Assert.Equal(448.5m, issued.BalanceDue);
        Assert.True(issued.JournalEntryNumber is { Length: > 0 }, "an issued bill must name the entry that carries the obligation");

        // The entry the bill posts is the three-way match itself: the receipt's own GRNI value cleared, the input
        // tax recognised, the approved price difference taken to variance, and the payable carried at the bill total.
        var lines = await EntryLinesAsync(issued.JournalEntryId!.Value);
        Assert.Equal(360m, lines.GetValueOrDefault(Grni).Debit);
        Assert.Equal(58.5m, lines.GetValueOrDefault(InputTax).Debit);
        Assert.Equal(30m, lines.GetValueOrDefault(Variance).Debit);
        Assert.Equal(448.5m, lines.GetValueOrDefault(Payable).Credit);
        Assert.Equal(0m, lines.GetValueOrDefault("1200").Debit + lines.GetValueOrDefault("1200").Credit);

        // Only the billed receipt cleared: the GRNI value of this flow is exhausted, and stock is untouched by bill
        // issue - an order and a bill are commitments, a delivery is the stock event.
        var afterBill = await ControlBalancesAsync();
        Assert.Equal(ledgerBefore[Grni], afterBill[Grni]);
        Assert.Equal(448.5m, afterBill[Payable] - ledgerBefore[Payable]);
        Assert.Equal(stockBefore.Item2 + 360m, await OnHandValueAsync(order.VariantId));

        // And the account read ties out: the supplier's own balance equals the documents owed, which equals the
        // movement this flow made in the payable control.
        var statement = await purchasing.GetStatementAsync(order.SupplierId);
        Assert.Equal(448.5m, statement.ClosingBalance);
        Assert.Equal(448.5m, statement.MovementCredit);
        Assert.Equal(0m, statement.MovementDebit);
        Assert.Equal(448.5m, Assert.Single(statement.OpenItems, item => item.TargetKind == "bill").RemainingAmount);
        Assert.Equal(0m, statement.UnappliedCredits);
        Assert.False(statement.Truncated);
        Assert.Contains(statement.Lines, line => line.Kind == "bill" && line.CreditAmount == 448.5m);
    }

    [Fact]
    public async Task T27_02_a_credit_for_a_delivery_already_returned_clears_the_leftover_grni_value_and_never_removes_stock_twice()
    {
        await EnsurePayablesChartAsync();
        var purchasing = await ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var ledgerBefore = await ControlBalancesAsync();
        var stockStart = await OnHandAsync(_fixture.BusinessA.VariantId);
        var order = await ReceiveAsync(purchasing, $"T27-02 {suffix}", ordered: 10m, received: 6m, unitCost: 60m);
        var afterReceipt = await ControlBalancesAsync();
        Assert.Equal(360m, afterReceipt[Grni] - ledgerBefore[Grni]);
        Assert.Equal(6m, (await OnHandAsync(order.VariantId)) - stockStart);

        // Two of the six pieces go back: the return is the document that reduces stock and the GRNI liability.
        var returned = await purchasing.ReturnAsync(order.OrderId, new PurchaseReceiptRequest(
            $"damaged in transit {suffix}", [new PurchaseReceiptLineRequest(order.OrderLineId, 2m)]),
            $"t27-02-return-{suffix}");
        Assert.Equal("Return", returned.Kind, ignoreCase: true);
        Assert.Equal(240m, (await ControlBalancesAsync())[Grni] - ledgerBefore[Grni]);
        var onHandAfterReturn = await OnHandAsync(order.VariantId);
        Assert.Equal(4m, onHandAfterReturn - stockStart);

        var billId = await CreateBillAsync(purchasing, order, suffix, quantity: 6m, unitCost: 65m, taxRate: 0.15m);
        var issued = await purchasing.IssueBillAsync(billId, $"t27-02-issue-{suffix}");

        // Billing all six pieces clears the GRNI for six, while the return had already taken two back out: the
        // leftover debit in that account is exactly the receipt value of what went back, and it is what the credit
        // below has to clear. A statement of "GRNI is zero" here would be the silence this test exists to break.
        Assert.Equal(-120m, (await ControlBalancesAsync())[Grni] - ledgerBefore[Grni]);

        var credited = await purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"two crates went back {suffix}", returned.Id, [new SupplierCreditLineRequest(issued.Lines[0].Id, 2m, returned.Lines[0].Id)]),
            $"t27-02-credit-{suffix}");

        var credit = Assert.Single(credited.Credits!);
        Assert.Equal(130m, credit.BaseAmount);
        Assert.Equal(19.5m, credit.TaxAmount);
        Assert.Equal(149.5m, credit.ValueAmount);
        Assert.False(string.IsNullOrWhiteSpace(credit.CreditNumber));
        Assert.Equal(returned.ReceiptNumber, credit.SourceReceiptNumber);

        // The entry reverses exactly what the bill recognised for those two pieces - the payable at the bill price,
        // the input tax, the receipt-cost value the return had left as a debit in GRNI, and the price difference.
        var lines = await EntryLinesAsync(credit.JournalEntryId);
        Assert.Equal(149.5m, lines.GetValueOrDefault(Payable).Debit);
        Assert.Equal(19.5m, lines.GetValueOrDefault(InputTax).Credit);
        Assert.Equal(120m, lines.GetValueOrDefault(Grni).Credit);
        Assert.Equal(10m, lines.GetValueOrDefault(Variance).Credit);

        // T27.02's point: the credit took nothing out of stock, because the return already did - and the GRNI debit
        // the return left behind is now cleared, so the clearing account holds nothing for this flow. The stock value
        // is read as the weighted-average value the ledger actually carries; what matters here is that the credit
        // moved neither the quantity nor the value.
        var valueBeforeCredit = await OnHandValueAsync(order.VariantId);
        Assert.Equal(onHandAfterReturn, await OnHandAsync(order.VariantId));
        Assert.Equal(ledgerBefore[Grni], (await ControlBalancesAsync())[Grni]);
        Assert.Equal(4m, Assert.Single((await purchasing.GetOrderAsync(order.OrderId)).Lines).QuantityReceived);
        Assert.Equal(4m, (await OnHandAsync(order.VariantId)) - stockStart);
        Assert.Equal(valueBeforeCredit, await OnHandValueAsync(order.VariantId));

        // The bill it credited still shows what that line can still be credited for, and the payable fell by the
        // credit's own value.
        Assert.Equal(2m, Assert.Single(credited.Lines).QuantityCredited);
        Assert.Equal(4m, Assert.Single(credited.Lines).QuantityBilled - Assert.Single(credited.Lines).QuantityCredited);
        Assert.Equal(299m, credited.BalanceDue);
        Assert.Equal(299m, (await purchasing.GetStatementAsync(order.SupplierId)).ClosingBalance);

        // A second credit for the same two pieces is refused whole: the cumulative guard counts what is left.
        var greedy = await CaptureAsync(() => purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"the same crates again {suffix}", returned.Id, [new SupplierCreditLineRequest(credited.Lines[0].Id, 3m, returned.Lines[0].Id)]),
            $"t27-02-greedy-{suffix}"));
        Assert.Equal(ApiErrorCodes.OverCredit, greedy.Code);
        Assert.Equal(299m, (await purchasing.GetBillAsync(billId)).BalanceDue);
        Assert.Equal(onHandAfterReturn, await OnHandAsync(order.VariantId));
        Assert.Equal(2, await CountAsync<PurchaseReceipt>(receipt => receipt.PurchaseOrderId == order.OrderId));
    }

    [Fact]
    public async Task T27_03_concurrent_settlements_and_a_credit_cannot_over_consume_one_remaining_payable()
    {
        await EnsurePayablesChartAsync();
        var ledgerBefore = await ControlBalancesAsync();
        var purchasing = await ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var order = await ReceiveAsync(purchasing, $"T27-03 {suffix}", ordered: 10m, received: 10m, unitCost: 50m);
        var billId = await CreateBillAsync(purchasing, order, suffix, quantity: 10m, unitCost: 50m, taxRate: 0m);
        var issued = await purchasing.IssueBillAsync(billId, $"t27-03-issue-{suffix}");
        Assert.Equal(500m, issued.BalanceDue);

        // Two cash settlements that each fit alone, plus a credit that would take the remainder. Only one payment can
        // have the last of the payable, and the three together can never consume more than the bill owed: what a
        // credit cannot apply to the bill it came from stays as the credit's own balance (T27.03, T27.04).
        var attempts = await Task.WhenAll(
            SettleAsync(purchasing, order.SupplierId, 400m, billId, $"t27-03-pay-a-{suffix}"),
            SettleAsync(purchasing, order.SupplierId, 400m, billId, $"t27-03-pay-b-{suffix}"));

        var settled = attempts.Count(attempt => attempt.Ok);
        Assert.Equal(1, settled);

        var after = await purchasing.GetBillAsync(billId);
        Assert.Equal(100m, after.BalanceDue);
        Assert.Equal(400m, after.AmountPaid);
        Assert.Equal(1, await CountAsync<SupplierPayment>(payment => payment.BusinessId == _fixture.BusinessA.BusinessId));

        // The same payable then takes a credit for exactly what remains, and a credit for more than the bill line can
        // still yield is refused whole rather than partly applied.
        var credited = await purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"two pieces were never delivered {suffix}", null, [new SupplierCreditLineRequest(issued.Lines[0].Id, 2m, null)]),
            $"t27-03-credit-{suffix}");
        var credit = Assert.Single(credited.Credits!);
        Assert.Equal(100m, credit.ValueAmount);
        Assert.Equal(100m, credit.AppliedAmount);
        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(0m, credited.BalanceDue);
        Assert.Equal(SupplierBillState.Paid, credited.Status);

        var greedy = await CaptureAsync(() => purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"more than the line can yield {suffix}", null, [new SupplierCreditLineRequest(issued.Lines[0].Id, 9m, null)]),
            $"t27-03-greedy-{suffix}"));
        Assert.Equal(ApiErrorCodes.OverCredit, greedy.Code);

        // The payable control is back to where it started, and the account read ties out: every document this flow
        // raised is settled, with nothing held and nothing over-consumed.
        Assert.Equal(ledgerBefore[Payable], (await ControlBalancesAsync())[Payable]);
        var statement = await purchasing.GetStatementAsync(order.SupplierId);
        Assert.Equal(0m, statement.ClosingBalance);
        Assert.Equal(0m, statement.UnappliedCredits);
        Assert.Equal(500m, statement.MovementCredit);
        Assert.Equal(500m, statement.MovementDebit);
        Assert.Empty(statement.OpenItems);

        foreach (var entry in await UnbalancedEntriesAsync())
        {
            throw new Xunit.Sdk.XunitException($"journal {entry} does not balance");
        }

        // A replay of the settlement that succeeded returns that same result rather than a second payment.
        var winner = attempts.Single(attempt => attempt.Ok);
        var replay = await purchasing.PaySupplierAsync(winner.Request!, winner.Key);
        Assert.Equal(winner.PaymentId, replay.Id);
        Assert.Equal(1, await CountAsync<SupplierPayment>(payment => payment.BusinessId == _fixture.BusinessA.BusinessId));

        // Over-consumption by amount alone is refused before anything is posted.
        var tooMuch = await CaptureAsync(() => purchasing.PaySupplierAsync(
            new CreateSupplierPaymentRequest(order.SupplierId, "cash", 900m, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", "OVER",
                [new SupplierAllocationTarget(billId, null, 900m)]),
            $"t27-03-toomuch-{suffix}"));
        Assert.True(tooMuch.IsConflict);
        Assert.Equal(0m, (await purchasing.GetBillAsync(billId)).BalanceDue);
    }

    [Fact]
    public async Task T27_04_a_credit_against_a_paid_bill_is_a_stated_balance_on_account_not_cash_and_not_a_write_off()
    {
        await EnsurePayablesChartAsync();
        var purchasing = await ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var order = await ReceiveAsync(purchasing, $"T27-04 {suffix}", ordered: 4m, received: 4m, unitCost: 25m);
        var billId = await CreateBillAsync(purchasing, order, suffix, quantity: 4m, unitCost: 25m, taxRate: 0m);
        var issued = await purchasing.IssueBillAsync(billId, $"t27-04-issue-{suffix}");
        await purchasing.PaySupplierAsync(new CreateSupplierPaymentRequest(
            order.SupplierId, "cash", 100m, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", $"paid-{suffix}",
            [new SupplierAllocationTarget(billId, null, 100m)]), $"t27-04-pay-{suffix}");

        var paid = await purchasing.GetBillAsync(billId);
        Assert.Equal(SupplierBillState.Paid, paid.Status);
        Assert.Equal(0m, paid.BalanceDue);
        var cashBefore = (await ControlBalancesAsync()).GetValueOrDefault(Cash);
        var payableBefore = (await ControlBalancesAsync()).GetValueOrDefault(Payable);
        var controlBefore = (await purchasing.GetStatementAsync(order.SupplierId)).PayableControlBalance;

        var credited = await purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"goods short-supplied, agreed by phone {suffix}", null, [new SupplierCreditLineRequest(paid.Lines[0].Id, 2m, null)]),
            $"t27-04-credit-{suffix}");

        var credit = Assert.Single(credited.Credits!);
        Assert.Equal(50m, credit.ValueAmount);
        Assert.Equal(0m, credit.AppliedAmount);
        Assert.Equal(50m, credit.RemainingAmount);
        Assert.Contains(Payable, credit.ControlAccountCodes!);

        // Nothing about it reads as money received: the cash account is untouched, and the payable control falls by
        // the credit's value, which is what a balance owed back to us looks like in the ledger.
        var afterCredit = await ControlBalancesAsync();
        Assert.Equal(cashBefore, afterCredit.GetValueOrDefault(Cash));
        Assert.Equal(-50m, afterCredit.GetValueOrDefault(Payable) - payableBefore);

        var statement = await purchasing.GetStatementAsync(order.SupplierId);
        Assert.Equal(50m, statement.UnappliedCredits);

        // The supplier's account reads negative by the credit held: the payable is gone and the supplier still owes
        // us the credit's value. That is the explicit balance the recipe asks for, and the identity that ties the
        // account read to the ledger - documents owed (none) less credits held (50).
        Assert.Equal(-50m, statement.ClosingBalance);
        Assert.Equal(0m, statement.OpenItems.Sum(item => item.RemainingAmount));
        Assert.Equal(-50m, statement.OpenItems.Sum(item => item.RemainingAmount) - statement.UnappliedCredits);
        Assert.Equal(-50m, statement.PayableControlBalance - controlBefore);
        Assert.Empty(statement.OpenItems);
        Assert.Contains(statement.Lines, line => line.Kind == "credit" && line.DebitAmount == 50m);

        // It can be used, once: applying it against another open bill **of the same supplier** settles that bill,
        // and the credit's own balance falls rather than the payable being reduced a second time.
        var otherOrder = await ReceiveAsync(purchasing, $"T27-04b {suffix}", ordered: 5m, received: 5m, unitCost: 30m, supplierId: order.SupplierId);
        var otherBill = await CreateBillAsync(purchasing, otherOrder, suffix, quantity: 5m, unitCost: 30m, taxRate: 0m);
        await purchasing.IssueBillAsync(otherBill, $"t27-04-issue-b-{suffix}");

        var applied = await purchasing.ApplyCreditAsync(credit.Id, new ApplySupplierCreditRequest(otherBill, null, 50m),
            $"t27-04-apply-{suffix}");
        Assert.Equal(100m, applied.BalanceDue);
        Assert.Equal(0m, applied.Credits is null ? 0m : applied.Credits.Sum(row => row.RemainingAmount));

        // A credit of one supplier may not be spent on another's bill, even inside one business.
        var strangerOrder = await ReceiveAsync(purchasing, $"T27-04c {suffix}", ordered: 2m, received: 2m, unitCost: 20m);
        var strangerBill = await CreateBillAsync(purchasing, strangerOrder, suffix, quantity: 2m, unitCost: 20m, taxRate: 0m);
        await purchasing.IssueBillAsync(strangerBill, $"t27-04-issue-c-{suffix}");
        var foreign = await CaptureAsync(() => purchasing.ApplyCreditAsync(credit.Id,
            new ApplySupplierCreditRequest(strangerBill, null, 1m), $"t27-04-foreign-{suffix}"));
        Assert.True(foreign.IsConflict);
        Assert.Equal(40m, (await purchasing.GetBillAsync(strangerBill)).BalanceDue);

        var held = await purchasing.GetBillAsync(billId);
        var used = Assert.Single(held.Credits!);
        Assert.Equal(50m, used.AppliedAmount);
        Assert.Equal(0m, used.RemainingAmount);

        var twice = await CaptureAsync(() => purchasing.ApplyCreditAsync(credit.Id, new ApplySupplierCreditRequest(otherBill, null, 50m),
            $"t27-04-twice-{suffix}"));
        Assert.True(twice.IsConflict);
        Assert.Equal(100m, (await purchasing.GetBillAsync(otherBill)).BalanceDue);

        // An over-application of a credit is refused by the credit's own balance, not by the target's.
        var greedy = await CaptureAsync(() => purchasing.ApplyCreditAsync(credit.Id, new ApplySupplierCreditRequest(otherBill, null, 5m),
            $"t27-04-greedy-{suffix}"));
        Assert.True(greedy.IsConflict);
        Assert.Contains("unused", greedy.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(credit.CreditNumber, greedy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T27_05_an_imported_opening_payable_is_settled_and_credited_without_a_fabricated_bill()
    {
        await EnsurePayablesChartAsync();
        var purchasing = await ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var supplier = await CreateSupplierAsync($"T27-05 supplier {suffix}");
        var controlBefore = (await ControlBalancesAsync()).GetValueOrDefault(Payable);
        var (openingId, _) = await SeedOpeningPayableAsync(supplier, 500m, suffix);
        var billsBefore = await CountAsync<SupplierBill>(bill => bill.BusinessId == _fixture.BusinessA.BusinessId);

        var before = await purchasing.GetStatementAsync(supplier);
        var opening = Assert.Single(before.OpenItems, item => item.TargetKind == "opening");
        Assert.Equal(500m, opening.RemainingAmount);
        Assert.Equal(500m, opening.OriginalAmount);
        Assert.Equal(500m, before.PayableControlBalance - controlBefore);
        Assert.Equal(500m, before.OpeningBalance);

        // Cash settles part of the opening. No supplier bill is created for it, and the imported figure stands.
        var payment = await purchasing.PaySupplierAsync(new CreateSupplierPaymentRequest(
            supplier, "bankTransfer", 200m, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", $"OPEN-{suffix}",
            [new SupplierAllocationTarget(null, openingId, 200m)]),
            $"t27-05-pay-{suffix}");
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(openingId, allocation.OpenItemId);
        Assert.Null(allocation.SupplierBillId);

        var after = await purchasing.GetStatementAsync(supplier);
        var settled = Assert.Single(after.OpenItems, item => item.TargetKind == "opening");
        Assert.Equal(300m, settled.RemainingAmount);
        Assert.Equal(200m, settled.SettledAmount);
        Assert.Equal(500m, settled.SettledAmount + settled.RemainingAmount);
        Assert.Equal(300m, after.PayableControlBalance - controlBefore);
        Assert.Equal(billsBefore, await CountAsync<SupplierBill>(bill => bill.BusinessId == _fixture.BusinessA.BusinessId));

        // More than the derived remaining is refused, and the original import is never rewritten.
        var greedy = await CaptureAsync(() => purchasing.PaySupplierAsync(new CreateSupplierPaymentRequest(
            supplier, "bankTransfer", 400m, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", $"OVER-{suffix}",
            [new SupplierAllocationTarget(null, openingId, 400m)]), $"t27-05-greedy-{suffix}"));
        Assert.True(greedy.IsConflict);
        Assert.Equal(300m, (await purchasing.GetStatementAsync(supplier)).PayableControlBalance - controlBefore);

        // A credit issued against a bill that is already paid has nothing to reduce, so it is held - and a held credit
        // can settle an opening payable without any journal being posted twice for it.
        var order = await ReceiveAsync(purchasing, $"T27-05b {suffix}", ordered: 3m, received: 3m, unitCost: 100m, supplierId: supplier);
        var billId = await CreateBillAsync(purchasing, order, suffix, quantity: 3m, unitCost: 100m, taxRate: 0m, supplierId: supplier);
        var issued = await purchasing.IssueBillAsync(billId, $"t27-05-issue-{suffix}");
        await purchasing.PaySupplierAsync(new CreateSupplierPaymentRequest(
            supplier, "bankTransfer", 300m, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", $"BILL-{suffix}",
            [new SupplierAllocationTarget(billId, null, 300m)]), $"t27-05-pay-bill-{suffix}");

        var credited = await purchasing.CreateCreditAsync(billId, new CreateSupplierCreditRequest(
            $"goods were short-delivered, supplier agreed {suffix}", null, [new SupplierCreditLineRequest(issued.Lines[0].Id, 1m, null)]),
            $"t27-05-credit-{suffix}");
        var credit = Assert.Single(credited.Credits!);
        Assert.Equal(0m, credit.AppliedAmount);
        Assert.Equal(100m, credit.RemainingAmount);

        var applied = await purchasing.ApplyCreditAsync(credit.Id, new ApplySupplierCreditRequest(null, openingId, 100m),
            $"t27-05-apply-{suffix}");
        Assert.Equal(0m, applied.BalanceDue);

        var final = await purchasing.GetStatementAsync(supplier);
        Assert.Equal(200m, Assert.Single(final.OpenItems, item => item.TargetKind == "opening").RemainingAmount);
        Assert.Equal(200m, final.PayableControlBalance - controlBefore);
        Assert.Equal(200m, final.ClosingBalance);
        Assert.Equal(0m, final.UnappliedCredits);

        var original = await _fixture.WithFreshContextAsync(async db => (await db.OpenItems.AsNoTracking()
            .SingleAsync(row => row.Id == openingId)).OriginalAmount);
        Assert.Equal(500m, original);

        await _fixture.WithFreshContextAsync(async db =>
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId));
    }

    private sealed record ReceivedOrder(
        Guid SupplierId,
        Guid OrderId,
        Guid OrderLineId,
        Guid VariantId,
        Guid ReceiptLineId);

    private sealed class Attempt
    {
        public bool Ok { get; init; }

        public string Key { get; init; } = string.Empty;

        public CreateSupplierPaymentRequest? Request { get; init; }

        public Guid PaymentId { get; init; }
    }

    /// <summary>
    /// One signed-in client per test, reused by every helper: the fixture's login is rate-limited on purpose, and a
    /// test that signed in once per supplier would be proving the limiter rather than the payable.
    /// </summary>
    private async Task<HttpClient> HttpAsync()
    {
        if (_http is null)
        {
            _http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        }

        return _http;
    }

    private async Task<PurchasingApiClient> ClientAsync()
        => new(await HttpAsync());

    private async Task<Guid> CreateSupplierAsync(string name)
    {
        var suppliers = new SupplierApiClient(await HttpAsync());
        var slug = Guid.NewGuid().ToString("N")[..12];
        var created = await suppliers.CreateSupplierAsync(new SaveSupplierRequest(
            name,
            $"supplier-{slug}@fixture.test",
            $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
            null));

        return created.Id;
    }

    /// <summary>An order, placed and partly or fully received, whose receipt line a bill can be matched to.</summary>
    private async Task<ReceivedOrder> ReceiveAsync(
        PurchasingApiClient purchasing, string note, decimal ordered, decimal received, decimal unitCost, Guid? supplierId = null)
    {
        var supplier = supplierId ?? await CreateSupplierAsync($"T27 supplier {Guid.NewGuid():N}");
        var created = await purchasing.CreateOrderAsync(new SavePurchaseOrderRequest(
            supplier,
            _fixture.BusinessA.LocationId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            note,
            [new PurchaseOrderLineRequest(_fixture.BusinessA.VariantId, ordered, unitCost, 0m, 0m)]));
        var placed = await purchasing.ConfirmOrderAsync(created.Order.Id);
        var orderLine = Assert.Single(placed.Lines);

        var receipt = await purchasing.ReceiveAsync(
            placed.Order.Id,
            new PurchaseReceiptRequest($"delivery {note}", [new PurchaseReceiptLineRequest(orderLine.Id, received)]),
            $"t27-receive-{Guid.NewGuid():N}");

        return new ReceivedOrder(
            supplier,
            placed.Order.Id,
            orderLine.Id,
            orderLine.VariantId,
            Assert.Single(receipt.Lines).PurchaseOrderLineId == orderLine.Id ? receipt.Lines[0].Id : receipt.Lines[0].Id);
    }

    /// <summary>A draft bill matched to the receipt line the order's delivery produced.</summary>
    private async Task<Guid> CreateBillAsync(
        PurchasingApiClient purchasing,
        ReceivedOrder order,
        string suffix,
        decimal quantity,
        decimal unitCost,
        decimal taxRate,
        Guid? supplierId = null)
    {
        var receiptLine = await _fixture.WithFreshContextAsync(async db =>
        {
            var receiptIds = await db.PurchaseReceipts.AsNoTracking()
                .Where(receipt => receipt.BusinessId == _fixture.BusinessA.BusinessId
                    && receipt.PurchaseOrderId == order.OrderId
                    && receipt.Kind == PurchaseReceiptKind.Receipt)
                .Select(receipt => receipt.Id)
                .ToListAsync();

            return await db.PurchaseReceiptLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && receiptIds.Contains(line.PurchaseReceiptId))
                .OrderBy(line => line.LineNo)
                .Select(line => line.Id)
                .FirstOrDefaultAsync();
        });
        Assert.True(receiptLine != Guid.Empty, "the delivery this bill matches must exist");

        var draft = await purchasing.CreateBillAsync(new CreateSupplierBillRequest(
            supplierId ?? order.SupplierId,
            _fixture.BusinessA.LocationId,
            order.OrderId,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            [new SupplierBillLineRequest(receiptLine, quantity, unitCost, 0m, taxRate, "Exclusive")]),
            $"t27-bill-{suffix}-{Guid.NewGuid():N}");

        return draft.Id;
    }

    /// <summary>
    /// One settlement attempt of a payable, recorded with the exact request and key so the winner can be replayed
    /// afterwards the way a screen would re-send an unanswered document.
    /// </summary>
    private async Task<Attempt> SettleAsync(PurchasingApiClient purchasing, Guid supplierId, decimal amount, Guid billId, string key)
    {
        var request = new CreateSupplierPaymentRequest(
            supplierId, "cash", amount, DateOnly.FromDateTime(DateTime.UtcNow), "QAR", key,
            [new SupplierAllocationTarget(billId, null, amount)]);

        try
        {
            var payment = await purchasing.PaySupplierAsync(request, key);
            return new Attempt { Ok = true, Key = key, Request = request, PaymentId = payment.Id };
        }
        catch (ApiException)
        {
            return new Attempt { Ok = false, Key = key, Request = request };
        }
    }

    /// <summary>Credit-minus-debit posted on each payables control account, over every open period.</summary>
    private async Task<Dictionary<string, decimal>> ControlBalancesAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var codes = new[] { Payable, Grni, InputTax, Variance, Cash, "1200" };
            var rows = await (from line in db.JournalLines.AsNoTracking()
                              join entry in db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                              join account in db.Accounts.AsNoTracking() on line.AccountId equals account.Id
                              where line.BusinessId == _fixture.BusinessA.BusinessId
                                  && entry.BusinessId == _fixture.BusinessA.BusinessId
                                  && codes.Contains(account.Code)
                              select new { account.Code, line.DebitAmount, line.CreditAmount })
                .ToListAsync();

            var totals = rows.GroupBy(row => row.Code).ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.CreditAmount - row.DebitAmount));

            foreach (var code in codes)
            {
                totals.TryAdd(code, 0m);
            }

            return totals;
        });

    private async Task<Dictionary<string, (decimal Debit, decimal Credit)>> EntryLinesAsync(Guid entryId)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var rows = await (from line in db.JournalLines.AsNoTracking()
                              where line.BusinessId == _fixture.BusinessA.BusinessId && line.JournalEntryId == entryId
                              join account in db.Accounts.AsNoTracking() on line.AccountId equals account.Id
                              select new { account.Code, line.DebitAmount, line.CreditAmount })
                .ToListAsync();

            return rows.GroupBy(row => row.Code).ToDictionary(
                group => group.Key,
                group => (group.Sum(row => row.DebitAmount), group.Sum(row => row.CreditAmount)));
        });

    /// <summary>Entries that do not balance, so a claim of correctness can be contradicted rather than assumed.</summary>
    private async Task<List<Guid>> UnbalancedEntriesAsync()
        => await _fixture.WithFreshContextAsync(async db => await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == _fixture.BusinessA.BusinessId && entry.TotalDebit != entry.TotalCredit)
            .Select(entry => entry.Id)
            .ToListAsync());

    private async Task<decimal> OnHandAsync(Guid variantId)
        => await _fixture.WithFreshContextAsync(db => db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                && balance.LocationId == _fixture.BusinessA.LocationId
                && balance.VariantId == variantId)
            .Select(balance => balance.OnHand)
            .FirstOrDefaultAsync());

    private async Task<decimal> OnHandValueAsync(Guid variantId)
        => await _fixture.WithFreshContextAsync(db => db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId
                && balance.LocationId == _fixture.BusinessA.LocationId
                && balance.VariantId == variantId)
            .Select(balance => balance.ValueOnHand)
            .FirstOrDefaultAsync());

    private async Task<int> CountAsync<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
        => await _fixture.WithFreshContextAsync(async db => await db.Set<TEntity>().AsNoTracking().CountAsync(predicate));

    /// <summary>An opening payable as the approved native import leaves one: its own journal, and no bill.</summary>
    private async Task<(Guid OpenItemId, decimal Amount)> SeedOpeningPayableAsync(Guid supplierId, decimal amount, string suffix)
    {
        var (payableId, equityId) = await _fixture.WithFreshContextAsync(async db =>
        {
            var payable = await db.Accounts.SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == Payable);
            var equity = await db.Accounts.SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "3000");

            return (payable.Id, equity.Id);
        });

        var http = await HttpAsync();
        var posting = new PostJournalEntryRequest(
            DateOnly.FromDateTime(DateTime.UtcNow),
            $"Opening payable {suffix}",
            "QAR",
            JournalOrigin.Manual,
            SourceId: null,
            [
                new JournalLineRequest(equityId, amount, 0m, "Opening balance equity", null),
                new JournalLineRequest(payableId, 0m, amount, "Opening payable", null),
            ]);

        var response = await http.PostAsJsonAsync("/api/v1/journal-entries", posting);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the opening journal could not be posted with {(int)response.StatusCode}: {body}");
        var entryId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var openingId = Guid.CreateVersion7();
        await _fixture.WithFreshContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.OpenItems.Add(new OpenItem
            {
                Id = openingId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Kind = OpenItemKind.Payable,
                SupplierId = supplierId,
                SourceType = "native_opening",
                SourceId = Guid.CreateVersion7(),
                JournalEntryId = entryId,
                DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
                DueDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(30),
                Currency = "QAR",
                OriginalAmount = amount,
                Reference = $"PAYOPEN-{suffix}",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });

            await db.SaveChangesAsync();
            return true;
        });

        return (openingId, amount);
    }

    /// <summary>
    /// The fixture business starts with no chart, so the payables accounts the three-way match posts to are created
    /// here - the same way task 16's bill tests do it - before any bill is issued.
    /// </summary>
    private async Task EnsurePayablesChartAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var wanted = new Dictionary<string, (string Name, AccountType Type)>
            {
                ["1000"] = ("Cash", AccountType.Asset),
                ["1010"] = ("Bank", AccountType.Asset),
                ["1100"] = ("Accounts receivable", AccountType.Asset),
                ["1200"] = ("Inventory", AccountType.Asset),
                ["1250"] = ("Delivered not invoiced", AccountType.Asset),
                ["1300"] = ("Input VAT", AccountType.Asset),
                [Payable] = ("Accounts payable", AccountType.Liability),
                ["2100"] = ("Output VAT", AccountType.Liability),
                [Grni] = ("GRNI", AccountType.Liability),
                ["2105"] = ("Customer deposits", AccountType.Liability),
                ["3000"] = ("Retained earnings", AccountType.Equity),
                ["4000"] = ("Sales", AccountType.Revenue),
                ["4010"] = ("Sales returns", AccountType.Revenue),
                ["5000"] = ("COGS", AccountType.Expense),
                [Variance] = ("Purchase price variance", AccountType.Expense),
                ["5200"] = ("Inventory shrinkage", AccountType.Expense),
                ["5300"] = ("Cash over short", AccountType.Expense),
                ["5999"] = ("Rounding", AccountType.Expense),
            };

            var accounts = await db.Accounts
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId)
                .ToDictionaryAsync(row => row.Code, StringComparer.Ordinal);

            foreach (var (code, (name, type)) in wanted)
            {
                if (!accounts.ContainsKey(code))
                {
                    var account = new Account
                    {
                        BusinessId = _fixture.BusinessA.BusinessId,
                        Code = code,
                        Name = name,
                        AccountType = type,
                    };
                    db.Accounts.Add(account);
                    accounts[code] = account;
                }
            }

            await db.SaveChangesAsync();

            var settings = await db.BusinessAccountingSettings
                .SingleOrDefaultAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId);
            if (settings is null)
            {
                settings = new BusinessAccountingSettings { BusinessId = _fixture.BusinessA.BusinessId };
                db.BusinessAccountingSettings.Add(settings);
            }

            settings.SettlementScale = 2;
            settings.CashAccountId = accounts["1000"].Id;
            settings.BankClearingAccountId = accounts["1010"].Id;
            settings.AccountsReceivableAccountId = accounts["1100"].Id;
            settings.InventoryAssetAccountId = accounts["1200"].Id;
            settings.DeliveredNotInvoicedAccountId = accounts["1250"].Id;
            settings.InputTaxReceivableAccountId = accounts["1300"].Id;
            settings.AccountsPayableAccountId = accounts[Payable].Id;
            settings.OutputTaxPayableAccountId = accounts["2100"].Id;
            settings.GrniClearingAccountId = accounts[Grni].Id;
            settings.CustomerDepositsAccountId = accounts["2105"].Id;
            settings.RetainedEarningsAccountId = accounts["3000"].Id;
            settings.SalesRevenueAccountId = accounts["4000"].Id;
            settings.SalesReturnsAccountId = accounts["4010"].Id;
            settings.CogsAccountId = accounts["5000"].Id;
            settings.PurchasePriceVarianceAccountId = accounts[Variance].Id;
            settings.InventoryShrinkageAccountId = accounts["5200"].Id;
            settings.CashOverShortAccountId = accounts["5300"].Id;
            settings.RoundingAccountId = accounts["5999"].Id;
            await db.SaveChangesAsync();
            return true;
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
