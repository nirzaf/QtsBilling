using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;
using InvoiceStatus = QtsBilling.Persistence.Postgres.InvoiceStatus;

namespace TestQtsBilling.Integration;

/// <summary>
/// Customer settlement (specification task 25): money a customer leaves on deposit is a liability until it is
/// applied, one application settles exactly one document - an issued invoice or an imported opening receivable -
/// and money handed back is journalled, reasoned and reversible only by a new event. These run against the real
/// host and read the ledger rows behind the responses, because every defect this task retires is a money effect
/// that a status code alone would hide.
/// </summary>
public sealed class CustomerSettlementTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public CustomerSettlementTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T25_01_an_unapplied_deposit_that_is_partly_applied_then_refunded_reconciles_exactly()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var payments = new PaymentApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Business-wide controls are read as a delta over what this test posts, taken before anything it creates.
        var before = await AccountBalancesAsync();
        var invoiceId = await CreateIssuedInvoiceAsync(invoices, 115m);

        // 1. A cash receipt with no allocation: the money is taken in and held for the customer. Nothing about the
        //    invoice moves, because deposit money has not settled anything yet (recipe step 3).
        var customerId = await CustomerIdAsync(invoiceId);
        var receipt = await payments.CreateCustomerPaymentAsync(
            customerId,
            new CreateCustomerPaymentRequest("cash", 100m, DateOnly.FromDateTime(DateTime.UtcNow), $"DEP-{suffix}", null),
            $"t25-01-receipt-{suffix}");

        Assert.Equal(100m, receipt.Amount);
        Assert.Equal(100m, receipt.UnallocatedAmount);
        Assert.Equal("Confirmed", receipt.Status);

        var afterReceipt = await invoices.GetAsync(invoiceId);
        Assert.Equal(115m, afterReceipt.Invoice.BalanceDue);
        Assert.Equal(0m, afterReceipt.Invoice.PaidAmount);

        // A liability grows on the credit side, so the receivable control reads debit-minus-credit and the deposit
        // account reads its negative: 100 taken in and not yet applied is a credit of 100 on customer deposits.
        var depositAfterReceipt = (await AccountBalancesAsync()).GetValueOrDefault("2105") - before.GetValueOrDefault("2105");
        Assert.Equal(-100m, depositAfterReceipt);

        var statement = await payments.GetStatementAsync(customerId);
        Assert.Equal(100m, statement.UnappliedDeposits);
        Assert.Equal(115m, statement.ClosingBalance);
        Assert.Contains(statement.Deposits!, deposit => deposit.PaymentId == receipt.Id);

        // 2. Forty of it settles the invoice: the liability falls, the receivable falls, and cash never moves again.
        var applied = await payments.AllocateAsync(
            receipt.Id,
            new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 40m)]),
            $"t25-01-apply-{suffix}");

        Assert.Equal(60m, applied.UnallocatedAmount);
        var afterApply = await invoices.GetAsync(invoiceId);
        Assert.Equal(75m, afterApply.Invoice.BalanceDue);
        Assert.Equal(40m, afterApply.Invoice.PaidAmount);
        Assert.Equal(InvoiceState.PartiallyPaid, afterApply.Invoice.Status);

        var now = await AccountBalancesAsync();
        Assert.Equal(before.GetValueOrDefault("2105") - 60m, now.GetValueOrDefault("2105"));
        Assert.Equal(before.GetValueOrDefault("1100") + 75m, now.GetValueOrDefault("1100"));

        // 3. The remaining sixty is handed back by bank settlement, with a reason and its own reversal journal.
        var refunded = await payments.RefundUnallocatedAsync(
            receipt.Id,
            new RefundUnallocatedRequest(60m, "customer asked for the overpayment back", "bankTransfer", null, $"BANK-{suffix}"),
            $"t25-01-refund-{suffix}");

        Assert.Equal(60m, refunded.Amount);
        Assert.Equal(0m, refunded.RemainingUnapplied);
        Assert.False(string.IsNullOrWhiteSpace(refunded.JournalEntryNumber));

        var final = await payments.GetStatementAsync(customerId);
        Assert.Equal(0m, final.UnappliedDeposits);
        Assert.Equal(75m, final.ClosingBalance);
        var finalDeposit = Assert.Single(final.Deposits!, deposit => deposit.PaymentId == receipt.Id);
        Assert.Equal(60m, Assert.Single(finalDeposit.Refunds).Amount);

        // The three controls tie: the liability is closed, cash in equals the bank out plus what settled the debt,
        // and the invoice's remaining balance is what the receivable account still carries.
        var closed = await AccountBalancesAsync();
        Assert.Equal(before.GetValueOrDefault("2105"), closed.GetValueOrDefault("2105"));
        Assert.Equal(100m, closed.GetValueOrDefault("1000") - before.GetValueOrDefault("1000"));
        Assert.Equal(60m, before.GetValueOrDefault("1010") - closed.GetValueOrDefault("1010"));
        Assert.Equal(75m, closed.GetValueOrDefault("1100") - before.GetValueOrDefault("1100"));

        var invoice = await invoices.GetAsync(invoiceId);
        Assert.Equal(75m, invoice.Invoice.BalanceDue);
        Assert.Equal(115m, invoice.Invoice.TotalAmount);

        // One refund row, one allocation row: nothing posted twice.
        Assert.Equal(1, await CountAsync(db => db.DepositRefunds.CountAsync(row => row.PaymentId == receipt.Id)));
        Assert.Equal(1, await CountAsync(db => db.PaymentAllocations.CountAsync(row => row.PaymentId == receipt.Id)));

        // A second refund attempt beyond the remaining deposit is refused and changes nothing.
        var over = await Assert.ThrowsAsync<ApiException>(() => payments.RefundUnallocatedAsync(
            receipt.Id, new RefundUnallocatedRequest(1m, "nothing left", "bankTransfer", null, "BANK-X"), $"t25-01-over-{suffix}"));
        Assert.True(over.IsConflict);
        Assert.Equal(1, await CountAsync(db => db.DepositRefunds.CountAsync(row => row.PaymentId == receipt.Id)));
    }

    [Fact]
    public async Task T25_02_replay_and_races_cannot_over_allocate_duplicate_cash_or_leave_an_unexplained_remainder()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var payments = new PaymentApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var invoiceId = await CreateIssuedInvoiceAsync(invoices, 115m);
        var customerId = await CustomerIdAsync(invoiceId);
        var receipt = await payments.CreateCustomerPaymentAsync(
            customerId,
            new CreateCustomerPaymentRequest("cash", 100m, DateOnly.FromDateTime(DateTime.UtcNow), $"RACE-{suffix}", null),
            $"t25-02-receipt-{suffix}");

        // The same key and the same body, sent twice: the server's replay returns the original application, so a
        // screen that lost its answer can ask again without risking a second movement (recipe step 4).
        var key = $"t25-02-apply-{suffix}";
        var body = new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 40m)]);
        var first = await payments.AllocateAsync(receipt.Id, body, key);
        var replay = await payments.AllocateAsync(receipt.Id, body, key);

        Assert.Equal(60m, first.UnallocatedAmount);
        Assert.Equal(60m, replay.UnallocatedAmount);
        Assert.Equal(1, await CountAsync(db => db.PaymentAllocations.CountAsync(row => row.PaymentId == receipt.Id)));
        Assert.Equal(40m, (await invoices.GetAsync(invoiceId)).Invoice.PaidAmount);

        // A different amount under the spent key is refused and writes nothing.
        var mismatch = await Assert.ThrowsAsync<ApiException>(() => payments.AllocateAsync(
            receipt.Id, new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 41m)]), key));
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, mismatch.Code);
        Assert.Equal(1, await CountAsync(db => db.PaymentAllocations.CountAsync(row => row.PaymentId == receipt.Id)));

        // Refund and application race for the same remaining sixty: the receipt row lock serialises them, so the
        // two together cannot take more than the deposit holds and no remainder goes unexplained.
        var tasks = new Task[]
        {
            Safe(() => payments.AllocateAsync(receipt.Id,
                new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 60m)]), $"t25-02-race-apply-{suffix}")),
            Safe(() => payments.RefundUnallocatedAsync(receipt.Id,
                new RefundUnallocatedRequest(60m, "refund raced against an application", "bankTransfer", null, $"RACE-{suffix}"),
                $"t25-02-race-refund-{suffix}")),
        };
        await Task.WhenAll(tasks);

        var settled = await payments.GetStatementAsync(customerId);
        var deposit = Assert.Single(settled.Deposits!, row => row.PaymentId == receipt.Id);
        Assert.Equal(0m, deposit.UnallocatedAmount);

        var appliedTotal = await SumAsync(db => db.PaymentAllocations
            .Where(row => row.PaymentId == receipt.Id)
            .SumAsync(row => (decimal?)row.AllocatedAmount));
        var refundedTotal = await SumAsync(db => db.DepositRefunds
            .Where(row => row.PaymentId == receipt.Id)
            .SumAsync(row => (decimal?)row.Amount));

        // Every franc of the deposit is accounted for exactly once: what settled a document plus what went back
        // equals what the customer handed over, and no remainder is left unexplained.
        Assert.Equal(100m, appliedTotal + refundedTotal);
        Assert.True(appliedTotal >= 0m && refundedTotal >= 0m, "a settlement figure can never go negative");

        // Which of the two won is the race's answer, not the test's assumption: exactly one of them may have taken
        // the last sixty, and both together may never do so.
        Assert.True(refundedTotal is 0m or 60m, $"a refund can only be nothing or the whole remainder, got {refundedTotal}");
        Assert.True(appliedTotal is 40m or 100m, $"applications can only be the settled first forty, or that plus the last sixty, got {appliedTotal}");

        var allocationRows = await CountAsync(db => db.PaymentAllocations.CountAsync(row => row.PaymentId == receipt.Id));
        var refundRows = await CountAsync(db => db.DepositRefunds.CountAsync(row => row.PaymentId == receipt.Id));
        Assert.Equal(2, allocationRows + refundRows);
        Assert.Equal(allocationRows == 2, refundRows == 0);

        var invoice = (await invoices.GetAsync(invoiceId)).Invoice;
        Assert.Equal(appliedTotal, invoice.PaidAmount);
        Assert.Equal(115m - appliedTotal, invoice.BalanceDue);
    }

    [Fact]
    public async Task T25_03_an_uncleared_cheque_settles_nothing_until_it_clears_and_a_bounce_restores_the_open_balance()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var payments = new PaymentApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var invoiceId = await CreateIssuedInvoiceAsync(invoices, 115m);
        var customerId = await CustomerIdAsync(invoiceId);

        // A cheque receipt written against the invoice: recorded, but not collected money. The receivable stays open
        // until the bank answers (recipe step 3, T25.03).
        var cheque = await payments.CreateCustomerPaymentAsync(
            customerId,
            new CreateCustomerPaymentRequest("cheque", 115m, DateOnly.FromDateTime(DateTime.UtcNow), $"CHQ-{suffix}", null),
            $"t25-03-cheque-{suffix}");
        Assert.Equal("Pending", cheque.Status);

        var pending = (await invoices.GetAsync(invoiceId)).Invoice;
        Assert.Equal(115m, pending.BalanceDue);
        Assert.Equal(0m, pending.PaidAmount);

        // Allocating from money that has not cleared is refused, and the refusal writes nothing.
        var refused = await Assert.ThrowsAsync<ApiException>(() => payments.AllocateAsync(
            cheque.Id, new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 15m)]), $"t25-03-allocate-{suffix}"));
        Assert.True(refused.IsConflict);
        Assert.Equal(0, await CountAsync(db => db.PaymentAllocations.CountAsync(row => row.PaymentId == cheque.Id)));

        // The legitimate clearing: the bank confirms the cheque, and only then does the deposit move.
        var cleared = await invoices.ClearPaymentAsync(cheque.Id, $"t25-03-clear-{suffix}");
        Assert.Equal("Confirmed", cleared.Status);

        var allocated = await payments.AllocateAsync(
            cheque.Id,
            new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 15m)]),
            $"t25-03-allocate-after-{suffix}");
        Assert.Equal(100m, allocated.UnallocatedAmount);
        Assert.Equal(15m, (await invoices.GetAsync(invoiceId)).Invoice.PaidAmount);

        // A tender the bank refuses is recorded as never collected: the balance it had reduced is restored.
        var direct = await invoices.AddPaymentAsync(
            invoiceId, new InvoicePaymentRequest("cheque", 50m, $"DIRECT-{suffix}", null), $"t25-03-direct-{suffix}");
        Assert.Equal("Pending", direct.Payment.Status);

        var bounced = await invoices.BouncePaymentAsync(
            direct.Payment.Id, new PaymentBounceRequest("the drawer was not honoured"), $"t25-03-bounce-{suffix}");
        Assert.Equal("Bounced", bounced.Status);

        var afterBounce = (await invoices.GetAsync(invoiceId)).Invoice;
        Assert.Equal(100m, afterBounce.BalanceDue);
        Assert.Equal(15m, afterBounce.PaidAmount);

        var statement = await payments.GetStatementAsync(customerId);
        Assert.Equal(100m, statement.ClosingBalance);

        // The bounced tender left no journal behind, so the ledger never claimed money it did not receive.
        Assert.Equal(0, await CountAsync(db => db.JournalEntries.CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
            && entry.SourceType == JournalSourceType.Payment && entry.SourceId == direct.Payment.Id)));
    }

    [Fact]
    public async Task T25_04_a_statement_read_as_at_an_earlier_date_excludes_the_later_receipt()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var payments = new PaymentApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The debt was raised yesterday and the money arrived today, so the two reads below differ only by the
        // receipt - which is exactly what an as-of statement has to get right.
        var invoiceId = await CreateIssuedInvoiceAsync(invoices, 115m, today.AddDays(-1));
        var customerId = await CustomerIdAsync(invoiceId);

        var receipt = await payments.CreateCustomerPaymentAsync(
            customerId,
            new CreateCustomerPaymentRequest("cash", 70m, today, $"ASOF-{suffix}", null),
            $"t25-04-receipt-{suffix}");
        await payments.AllocateAsync(
            receipt.Id, new AllocateCustomerPaymentRequest([new AllocationTarget(invoiceId, null, 70m)]), $"t25-04-apply-{suffix}");

        // As at today the settlement is visible; as at yesterday the account still shows the whole debt, because the
        // money had not arrived. A statement printed as at a date cannot be contradicted by a later posting.
        var asToday = await payments.GetStatementAsync(customerId, today);
        Assert.Equal(45m, asToday.ClosingBalance);
        Assert.Contains(asToday.Lines, line => line.Kind == "application" && line.CreditAmount == 70m);

        // As at yesterday the debt is whole: the invoice row is there, the settlement is not. A statement printed
        // as at a date cannot be contradicted by a posting that belongs to a later one.
        var asYesterday = await payments.GetStatementAsync(customerId, today.AddDays(-1));
        Assert.Equal(115m, asYesterday.ClosingBalance);
        Assert.Contains(asYesterday.Lines, line => line.Kind == "invoice" && line.DebitAmount == 115m);
        Assert.DoesNotContain(asYesterday.Lines, line => line.Kind is "application" or "receipt");
        Assert.Equal(0m, asYesterday.UnappliedDeposits);
        Assert.Empty(asYesterday.Deposits!);

        var yesterdayItems = await payments.GetOpenItemsAsync(customerId, today.AddDays(-1));
        Assert.Equal(115m, Assert.Single(yesterdayItems).RemainingAmount);

        // The running balance is the arithmetic over the rows shown, so the last row's balance is the closing one.
        Assert.Equal(asToday.Lines[^1].BalanceAfter, asToday.ClosingBalance);
        Assert.Equal(asToday.MovementDebit - asToday.MovementCredit + asToday.OpeningBalance, asToday.ClosingBalance);

        // The open-item list agrees with the statement it came from, and with the invoice's own balance.
        var openItems = await payments.GetOpenItemsAsync(customerId, today);
        var open = Assert.Single(openItems);
        Assert.Equal(45m, open.RemainingAmount);
        Assert.Equal("invoice", open.TargetKind);
        Assert.Equal(70m, open.AppliedAmount);

        var future = await Assert.ThrowsAsync<ApiException>(() => payments.GetStatementAsync(customerId, today.AddDays(3)));
        Assert.True(future.StatusCode == 400, $"a future statement date must be refused, got {(int)future.StatusCode}");
    }

    [Fact]
    public async Task T25_05_an_imported_opening_receivable_settles_from_a_deposit_without_a_duplicate_invoice_or_double_counting()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var payments = new PaymentApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // The receivable control is read as a delta over what this test posts, so an opening's settled part can be
        // proved not to be counted twice without inheriting balances another test left on the same business.
        var arBefore = await AccountBalancesAsync();
        var invoiceId = await CreateIssuedInvoiceAsync(invoices, 115m);
        var customerId = await CustomerIdAsync(invoiceId);

        // An opening receivable the native import brought in: real money owed that was never an invoice, posted to
        // the receivable control by its own journal.
        var (openingId, openingAmount) = await SeedOpeningAsync(owner, customerId, 500m, suffix);

        var invoicesBefore = await CountAsync(db => db.Invoices.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId));

        var before = await payments.GetOpenItemsAsync(customerId);
        var openingRow = Assert.Single(before, row => row.TargetKind == "opening");
        Assert.Equal(500m, openingRow.RemainingAmount);

        // A deposit settles part of it. The imported original amount is never patched, and no invoice is created
        // for the opening - the application names the opening itself (recipe step 2).
        var receipt = await payments.CreateCustomerPaymentAsync(
            customerId,
            new CreateCustomerPaymentRequest("cash", 300m, DateOnly.FromDateTime(DateTime.UtcNow), $"OPEN-{suffix}", null),
            $"t25-05-receipt-{suffix}");

        var applied = await payments.AllocateAsync(
            receipt.Id,
            new AllocateCustomerPaymentRequest([new AllocationTarget(null, openingId, 200m)]),
            $"t25-05-apply-opening-{suffix}");
        Assert.Equal(100m, applied.UnallocatedAmount);

        Assert.Equal(115m, (await invoices.GetAsync(invoiceId)).Invoice.BalanceDue);
        Assert.Equal(invoicesBefore, await CountAsync(db => db.Invoices.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId)));

        var original = await _fixture.WithFreshContextAsync(async db => (await db.OpenItems.AsNoTracking()
            .SingleAsync(row => row.Id == openingId)).OriginalAmount);
        Assert.Equal(openingAmount, original);

        var after = await payments.GetOpenItemsAsync(customerId);
        var settled = Assert.Single(after, row => row.TargetKind == "opening");
        Assert.Equal(300m, settled.RemainingAmount);
        Assert.Equal(200m, settled.AppliedAmount);
        // More than the opening holds is refused: the derived remaining is the ceiling, not the imported figure.
        var greedy = await Assert.ThrowsAsync<ApiException>(() => payments.AllocateAsync(
            receipt.Id, new AllocateCustomerPaymentRequest([new AllocationTarget(null, openingId, 400m)]), $"t25-05-greedy-{suffix}"));
        Assert.True(greedy.IsConflict);
        Assert.Equal(100m, (await payments.GetStatementAsync(customerId)).UnappliedDeposits);

        // The receivable control carries the opening's remaining figure, not its original: the money already
        // applied is not counted a second time as a debt.
        var arAfter = await AccountBalancesAsync();
        Assert.Equal(115m + 300m, arAfter.GetValueOrDefault("1100") - arBefore.GetValueOrDefault("1100"));

        // The aging report refuses to print a number it cannot trace rather than inventing one: this fixture
        // business has no configured control accounts, so the report answers with its reason (task 33 ties the
        // report itself out; what matters here is that the opening subtraction this task added is the same
        // remaining figure the control account carries, proved above).
        var aging = await owner.GetAsync("/api/v1/reports/ar-aging");
        Assert.Equal(HttpStatusCode.Conflict, aging.StatusCode);
        var agingBody = await aging.Content.ReadAsStringAsync();
        Assert.Contains("account", agingBody, StringComparison.OrdinalIgnoreCase);

        // A statement shows the opening as a charge and the application against it, and the deposit's own refund
        // history stays separate from what the customer owes.
        var statement = await payments.GetStatementAsync(customerId);
        Assert.Contains(statement.Lines, line => line.Kind == "opening" && line.DebitAmount == 500m);
        Assert.Contains(statement.Lines, line => line.Kind == "application" && line.CreditAmount == 200m);
        Assert.Equal(415m, statement.ClosingBalance);
    }

    private async Task<Guid> CreateIssuedInvoiceAsync(InvoiceApiClient invoices, decimal total, DateOnly? issueDate = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customer = await owner.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"Settlement customer {suffix}",
            email = $"settlement-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var customerBody = await customer.Content.ReadAsStringAsync();
        Assert.True(customer.IsSuccessStatusCode, $"customer create failed with {(int)customer.StatusCode}: {customerBody}");
        var customerId = JsonDocument.Parse(customerBody).RootElement.GetProperty("id").GetGuid();

        // Direct goods, one line, no tax: the document total is the figure every assertion below reads.
        var created = await invoices.CreateAsync(new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            issueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            $"task 25 settlement {suffix}",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, total, 0m, 0m)],
            InvoiceRecognitionMode.DirectGoods));

        var issued = await invoices.IssueAsync(created.Invoice.Id);
        Assert.NotEqual(InvoiceState.Draft, issued.Invoice.Status);
        Assert.Equal(total, issued.Invoice.TotalAmount);

        return issued.Invoice.Id;
    }

    private async Task<Guid> CustomerIdAsync(Guid invoiceId)
        => await _fixture.WithFreshContextAsync(async db => (await db.Invoices.AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId)).CustomerId!.Value);

    /// <summary>
    /// Seeds an imported opening receivable the way the native package leaves it: a posted journal through the
    /// ordinary accounting route (debit the receivable control, credit opening equity), then the opening row that
    /// names that journal. The opening's own identity is the import's source id, so the same import cannot post it
    /// twice.
    /// </summary>
    private async Task<(Guid OpenItemId, decimal Amount)> SeedOpeningAsync(
        HttpClient owner, Guid customerId, decimal amount, string suffix)
    {
        var sourceId = Guid.CreateVersion7();
        var (receivableId, equityId) = await _fixture.WithFreshContextAsync(async db =>
        {
            var ar = await db.Accounts.SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "1100");
            var equity = await db.Accounts.SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "3000");

            return (ar.Id, equity.Id);
        });

        var posting = new QtsBilling.Contracts.Accounting.PostJournalEntryRequest(
            DateOnly.FromDateTime(DateTime.UtcNow),
            $"Opening receivable {suffix}",
            "QAR",
            QtsBilling.Contracts.Accounting.JournalOrigin.Manual,
            SourceId: null,
            [
                new QtsBilling.Contracts.Accounting.JournalLineRequest(receivableId, amount, 0m, "Opening receivable", null),
                new QtsBilling.Contracts.Accounting.JournalLineRequest(equityId, 0m, amount, "Opening balance equity", null),
            ]);

        var response = await owner.PostAsJsonAsync("/api/v1/journal-entries", posting);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"opening journal could not be posted with {(int)response.StatusCode}: {body}");
        var entryId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var openingId = Guid.CreateVersion7();
        await _fixture.WithFreshContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.OpenItems.Add(new OpenItem
            {
                Id = openingId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Kind = OpenItemKind.Receivable,
                CustomerId = customerId,
                SourceType = "native_opening",
                SourceId = sourceId,
                JournalEntryId = entryId,
                DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
                DueDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(30),
                Currency = "QAR",
                OriginalAmount = amount,
                Reference = $"OPENING-{suffix}",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });

            await db.SaveChangesAsync();
            return true;
        });

        return (openingId, amount);
    }

    /// <summary>Net posted movement on an account code, from the journal lines that own it.</summary>
    private async Task<Dictionary<string, decimal>> AccountBalancesAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var codes = new[] { "1000", "1010", "1100", "2105" };
            var accounts = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId && codes.Contains(account.Code))
                .ToListAsync();
            var rows = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && accounts.Select(a => a.Id).Contains(line.AccountId))
                .ToListAsync();

            return accounts.ToDictionary(
                account => account.Code,
                account => decimal.Round(rows.Where(line => line.AccountId == account.Id).Sum(line => line.DebitAmount - line.CreditAmount), 4));
        });

    private Task<int> CountAsync(Func<BillingDbContext, Task<int>> query) => _fixture.WithFreshContextAsync(query);

    private Task<T> SumAsync<T>(Func<BillingDbContext, Task<T>> query) => _fixture.WithFreshContextAsync(query);

    private static async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException)
        {
            // A refused racer is the expected half of the assertion; what matters is what was written.
        }
        catch (HttpRequestException)
        {
        }
    }
}
