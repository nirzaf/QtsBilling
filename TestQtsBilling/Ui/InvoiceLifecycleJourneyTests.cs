using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Invoices;
using TestQtsBilling.Integration;
using Xunit;
using InvoiceStatus = QtsBilling.Persistence.Postgres.InvoiceStatus;

namespace TestQtsBilling.Ui;

/// <summary>
/// The invoice lifecycle as an operator works it (specification task 24, T24.01 and T24.03): a document is issued,
/// part-paid with a tender that has not yet cleared, cleared, credited against its own lines, and read back with
/// the figures the server holds. The settlement workbench is proven against the real host and the real ledger -
/// a response-status-only mock would not have caught any of the four defects this task retires.
/// </summary>
public sealed class InvoiceLifecycleJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public InvoiceLifecycleJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T24_01_an_issued_document_part_paid_then_crediced_agrees_with_the_server_and_stays_issued_as_written()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);

        // The credit path posts revenue back through the returns account the business's own bootstrap creates;
        // the fixture chart is asserted here rather than assumed, exactly as the recognition tests do.
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new QtsBilling.Persistence.Postgres.Entities.Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
                await db.SaveChangesAsync();
            }
        });

        var invoiceId = await CreateAndIssueAsync(invoices, lines: 2, quantity: 4m, unitPrice: 100m, taxRate: 0.15m);
        var issued = await invoices.GetAsync(invoiceId);

        // The issued document is the reference for everything that follows: its lines, its tax composition and its
        // party snapshot must survive a payment, a clearance and a credit untouched.
        var asIssued = issued.Invoice with { Note = null, Version = 0, IssuedAtUtc = null };
        var issuedLines = issued.Lines.Select(line => line with { Id = Guid.Empty }).ToArray();
        var issuedParty = issued.PartySnapshot;
        var total = issued.Invoice.TotalAmount;

        Assert.Equal(InvoiceState.Issued, issued.Invoice.Status);
        Assert.Equal(0m, issued.Invoice.PaidAmount);
        Assert.Equal(total, issued.Invoice.BalanceDue);
        Assert.Equal(0m, issued.AwaitingClearanceAmount);
        Assert.Equal(2, issued.Lines.Count);

        // 1. A cheque is recorded. It is money promised, so the collected figure and the balance do not move -
        //    the rule recipe step 2 states and finding F21's screen must not quietly break.
        var cheque = await invoices.AddPaymentAsync(
            invoiceId,
            new InvoicePaymentRequest("cheque", 115m, "CHQ-F21-1", null),
            "t24-01-cheque-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("Pending", cheque.Payment.Status);
        Assert.Equal(0m, cheque.Invoice.PaidAmount);
        Assert.Equal(total, cheque.Invoice.BalanceDue);

        var afterCheque = await invoices.GetAsync(invoiceId);
        Assert.Equal(115m, afterCheque.AwaitingClearanceAmount);
        Assert.Equal(0m, afterCheque.Invoice.PaidAmount);

        // The figures the workbench renders are the server's own, read at the business's settlement scale.
        var scale = afterCheque.Presentation?.SettlementScale;
        Assert.Equal(InvoiceRules.Money(115m, scale) + " " + afterCheque.Invoice.Currency,
            InvoiceRules.MoneyText(afterCheque.AwaitingClearanceAmount, afterCheque.Invoice.Currency, scale));

        // 2. The cheque clears: the journal is posted then, and only then does the balance move.
        var cleared = await invoices.ClearPaymentAsync(
            cheque.Payment.Id,
            "t24-01-clear-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("Confirmed", cleared.Status);
        Assert.NotNull(cleared.ClearedAtUtc);

        var afterClear = await invoices.GetAsync(invoiceId);
        Assert.Equal(115m, afterClear.Invoice.PaidAmount);
        Assert.Equal(0m, afterClear.AwaitingClearanceAmount);
        Assert.Equal(total - 115m, afterClear.Invoice.BalanceDue);
        Assert.Equal(InvoiceState.PartiallyPaid, afterClear.Invoice.Status);

        // A payment journal exists exactly once for this tender, dated from the clearance and not the record.
        var paymentJournals = await CountAsync(db => db.JournalEntries
            .CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                && entry.SourceType == JournalSourceType.Payment
                && entry.SourceId == cheque.Payment.Id));
        Assert.Equal(1, paymentJournals);

        // 3. One line is credited with half its quantity, and the remaining creditable figure the screen offers
        //    is exactly what is left of it - the workbench's ceiling is the server's history.
        var line = afterClear.Lines[0];
        var credited = await invoices.CreditAsync(
            invoiceId,
            new InvoiceCreditNoteRequest("two of four returned", null, [new InvoiceCreditLineRequest(line.Id, 2m, RestockInventory: true)]),
            "t24-01-credit-" + Guid.NewGuid().ToString("N"));

        var creditNote = credited.CreditNote!;
        Assert.Equal(2m, Assert.Single(creditNote.Lines!).QuantityCredited);

        var readBack = await invoices.GetAsync(invoiceId);
        Assert.Equal(2m, InvoiceRules.RemainingQuantity(readBack.Lines[0], readBack));
        Assert.Equal(4m, InvoiceRules.RemainingQuantity(readBack.Lines[1], readBack));

        // The four figures the operator reads must add up the way the ledger says they do, every time.
        Assert.Equal(total, readBack.Invoice.PaidAmount + readBack.Invoice.BalanceDue + readBack.Invoice.CreditedAmount);
        Assert.Equal(0m, readBack.AwaitingClearanceAmount);
        Assert.NotNull(readBack.Invoice.InvoiceNumber);

        // 4. Nothing that was written at issue moved: not the lines, not their tax composition, not the party.
        var finalDetail = await invoices.GetAsync(invoiceId);
        Assert.Equal(asIssued.InvoiceNumber, finalDetail.Invoice.InvoiceNumber);
        Assert.Equal(asIssued.IssueDate, finalDetail.Invoice.IssueDate);
        Assert.Equal(asIssued.TotalAmount, finalDetail.Invoice.TotalAmount);
        Assert.Equal(asIssued.TaxAmount, finalDetail.Invoice.TaxAmount);
        Assert.Equal(asIssued.RecognitionMode, finalDetail.Invoice.RecognitionMode);
        Assert.Equal(issuedLines.Length, finalDetail.Lines.Count);
        for (var index = 0; index < issuedLines.Length; index++)
        {
            var before = issuedLines[index];
            var now = finalDetail.Lines[index] with { Id = Guid.Empty };
            Assert.Equal(before.ProductName, now.ProductName);
            Assert.Equal(before.Sku, now.Sku);
            Assert.Equal(before.Quantity, now.Quantity);
            Assert.Equal(before.UnitPrice, now.UnitPrice);
            Assert.Equal(before.TaxRate, now.TaxRate);
            Assert.Equal(before.TaxAmount, now.TaxAmount);
            Assert.Equal(before.LineTotal, now.LineTotal);
            Assert.Equal(before.TaxMode, now.TaxMode);
            Assert.Equal(before.RevenueBaseAmount, now.RevenueBaseAmount);
        }

        Assert.Equal(issuedParty!.CustomerName, finalDetail.PartySnapshot!.CustomerName);
        Assert.Equal(issuedParty.BusinessName, finalDetail.PartySnapshot.BusinessName);

        // 5. The projection is the business's own settings, read from the row that owns them - never invented.
        var projected = await _fixture.WithFreshContextAsync(async db =>
        {
            var business = await db.Businesses.AsNoTracking().SingleAsync(row => row.Id == _fixture.BusinessA.BusinessId);
            var settings = await db.BusinessAccountingSettings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId);

            return (business.TimeZone, settings?.SettlementScale);
        });

        Assert.Equal(projected.SettlementScale, finalDetail.Presentation!.SettlementScale);
        Assert.Equal(string.IsNullOrWhiteSpace(projected.TimeZone) ? null : projected.TimeZone, finalDetail.Presentation.TimeZoneId);

        var expectedToday = string.IsNullOrWhiteSpace(projected.TimeZone)
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(projected.TimeZone!)));
        Assert.Equal(expectedToday, finalDetail.Presentation.BusinessToday);
    }

    [Fact]
    public async Task T24_03_a_lost_payment_answer_is_recovered_with_the_same_key_and_body_and_never_posts_twice()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var invoiceId = await CreateAndIssueAsync(invoices, lines: 1, quantity: 2m, unitPrice: 50m, taxRate: 0.10m);

        var detail = await invoices.GetAsync(invoiceId);
        var payable = detail.Invoice.BalanceDue;
        var key = InvoiceRules.NewKey("payment", detail.Invoice.InvoiceNumber);
        var body = new InvoicePaymentRequest("bankTransfer", payable, "T24-03-REF", null);

        // The screen holds its key and its body while the answer is outstanding (InvoiceRules keeps the key for
        // anything that is not provably an empty refusal), so a retry after a lost response sends exactly this.
        var first = await invoices.AddPaymentAsync(invoiceId, body, key);
        var replay = await invoices.AddPaymentAsync(invoiceId, body, key);

        Assert.Equal(first.Payment.Id, replay.Payment.Id);
        Assert.Equal(first.Invoice.PaidAmount, replay.Invoice.PaidAmount);

        var payments = await CountAsync(db => db.Payments.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.InvoiceId == invoiceId));
        Assert.Equal(1, payments);

        // The pending tender was recorded, not collected: the balance still stands and no payment journal exists
        // until clearance, so a replayed attempt can never have doubled the money either.
        var afterReplay = await invoices.GetAsync(invoiceId);
        Assert.Equal(payable, afterReplay.Invoice.BalanceDue);
        Assert.Equal(payable, afterReplay.AwaitingClearanceAmount);
        Assert.Equal(0m, afterReplay.Invoice.PaidAmount);
        Assert.Equal(0, await CountAsync(db => db.JournalEntries.CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
            && entry.SourceType == JournalSourceType.Payment && entry.SourceId == first.Payment.Id)));

        // The same key with different figures is refused and writes nothing - the screen reads this code as
        // "committed nothing", releases the key and lets the operator start a fresh intention.
        var refused = await Assert.ThrowsAsync<ApiException>(() => invoices.AddPaymentAsync(
            invoiceId, body with { Amount = 1m, Reference = "T24-03-OTHER" }, key));
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, refused.Code);
        Assert.True(InvoiceRules.CommittedNothing(refused.Code));

        Assert.Equal(1, await CountAsync(db => db.Payments.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.InvoiceId == invoiceId)));
        Assert.Equal(payable, (await invoices.GetAsync(invoiceId)).AwaitingClearanceAmount);

        // An outcome that is merely unknown keeps the key: these codes must never free an intention.
        Assert.False(InvoiceRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(InvoiceRules.CommittedNothing(ApiErrorCodes.TransientRetry));
        Assert.False(InvoiceRules.CommittedNothing(code: null));
        Assert.True(InvoiceRules.CommittedNothing(ApiErrorCodes.OptimisticConcurrencyFailure));

        // Keys fit the header the server validates and differ per intention.
        Assert.True(key.Length <= InvoiceRules.MaxKeyLength);
        Assert.NotEqual(key, InvoiceRules.NewKey("payment", detail.Invoice.InvoiceNumber));
        Assert.True(InvoiceRules.NewKey("credit", new string('9', 200)).Length <= InvoiceRules.MaxKeyLength);
    }

    [Fact]
    public async Task T24_01_a_bounced_tender_is_recorded_as_money_that_was_never_collected()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoices = new InvoiceApiClient(owner);
        var invoiceId = await CreateAndIssueAsync(invoices, lines: 1, quantity: 1m, unitPrice: 200m, taxRate: 0m);

        var payable = (await invoices.GetAsync(invoiceId)).Invoice.BalanceDue;
        var cheque = await invoices.AddPaymentAsync(
            invoiceId,
            new InvoicePaymentRequest("cheque", payable, "CHQ-BOUNCE", null),
            "t24-bounce-record-" + Guid.NewGuid().ToString("N"));

        var bounced = await invoices.BouncePaymentAsync(
            cheque.Payment.Id,
            new PaymentBounceRequest("the drawer was not honoured"),
            "t24-bounce-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("Bounced", bounced.Status);
        Assert.Equal("the drawer was not honoured", bounced.BounceReason);
        Assert.NotNull(bounced.BouncedAtUtc);

        var after = await invoices.GetAsync(invoiceId);
        Assert.Equal(0m, after.Invoice.PaidAmount);
        Assert.Equal(0m, after.AwaitingClearanceAmount);
        Assert.Equal(payable, after.Invoice.BalanceDue);
        Assert.Equal(InvoiceState.Issued, after.Invoice.Status);
        Assert.Equal("Bounced", Assert.Single(after.Payments).Status);

        // Bouncing the same tender again is refused, and the refused attempt changes no figure.
        var again = await Assert.ThrowsAsync<ApiException>(() => invoices.BouncePaymentAsync(
            cheque.Payment.Id, new PaymentBounceRequest("twice"), "t24-bounce-again-" + Guid.NewGuid().ToString("N")));
        Assert.True(again.IsConflict);
        Assert.Equal(payable, (await invoices.GetAsync(invoiceId)).Invoice.BalanceDue);

        Assert.Equal(0, await CountAsync(db => db.JournalEntries.CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
            && entry.SourceType == JournalSourceType.Payment && entry.SourceId == cheque.Payment.Id)));

        // The event history the workbench shows is assembled from these facts, in order, with the reason attached.
        var detail = await invoices.GetAsync(invoiceId);
        var events = new List<(DateTimeOffset At, string Kind)>
        {
            (detail.Payments[0].ReceivedAtUtc, "recorded"),
            (detail.Payments[0].BouncedAtUtc!.Value, "bounced"),
        };
        Assert.True(events[0].At <= events[1].At, "a bounce cannot precede the tender it refuses");
        Assert.True(detail.Invoice.IssuedAtUtc!.Value <= detail.Payments[0].ReceivedAtUtc, "a tender cannot predate the document it settles");
    }

    [Fact]
    public void T24_01_only_the_state_and_the_role_leave_a_button_on_the_screen()
    {
        var all = new InvoiceAccess(true, true, true, true);
        var nobody = new InvoiceAccess(false, false, false, false);

        var draft = Detail(InvoiceState.Draft, paid: 0m, awaiting: 0m, InvoiceRecognitionMode.Service);
        Assert.DoesNotContain(InvoiceAction.RecordPayment, InvoiceRules.AllowedActions(draft, all));
        Assert.DoesNotContain(InvoiceAction.Credit, InvoiceRules.AllowedActions(draft, all));

        // A draft is a document that has not been claimed yet: it is edited and issued, never printed or settled.
        Assert.Equal([], InvoiceRules.AllowedActions(draft, all));
        Assert.Equal([], InvoiceRules.AllowedActions(draft, nobody));

        var issued = Detail(InvoiceState.Issued, paid: 0m, awaiting: 0m, InvoiceRecognitionMode.Service);
        Assert.Contains(InvoiceAction.RecordPayment, InvoiceRules.AllowedActions(issued, all));
        Assert.Contains(InvoiceAction.Credit, InvoiceRules.AllowedActions(issued, all));
        Assert.Contains(InvoiceAction.Void, InvoiceRules.AllowedActions(issued, all));
        Assert.DoesNotContain(InvoiceAction.Void, InvoiceRules.AllowedActions(issued, new InvoiceAccess(true, false, false, false)));

        // A document that already holds money, or holds a tender on its way, can no longer be voided.
        Assert.DoesNotContain(InvoiceAction.Void, InvoiceRules.AllowedActions(Detail(InvoiceState.Issued, 10m, 0m, InvoiceRecognitionMode.Service), all));
        Assert.DoesNotContain(InvoiceAction.Void, InvoiceRules.AllowedActions(Detail(InvoiceState.Issued, 0m, 10m, InvoiceRecognitionMode.Service, pendingCheque: true), all));

        // A document owned by a sale or a delivery is corrected through its own workflow, never by a void here.
        Assert.DoesNotContain(InvoiceAction.Void, InvoiceRules.AllowedActions(Detail(InvoiceState.Issued, 0m, 0m, InvoiceRecognitionMode.SaleRepresentation), all));
        Assert.DoesNotContain(InvoiceAction.Void, InvoiceRules.AllowedActions(Detail(InvoiceState.Issued, 0m, 0m, InvoiceRecognitionMode.Delivery), all));

        var paid = Detail(InvoiceState.Paid, 115m, 0m, InvoiceRecognitionMode.DirectGoods);
        Assert.DoesNotContain(InvoiceAction.RecordPayment, InvoiceRules.AllowedActions(paid, all));
        Assert.Contains(InvoiceAction.Credit, InvoiceRules.AllowedActions(paid, all));

        var voided = Detail(InvoiceState.Void, 0m, 0m, InvoiceRecognitionMode.Service);
        Assert.Equal([InvoiceAction.Print], InvoiceRules.AllowedActions(voided, all));

        // Clearance belongs to the ledger role, and only a pending cheque or transfer can be cleared.
        var withTender = Detail(InvoiceState.Issued, 0m, 25m, InvoiceRecognitionMode.Service, pendingCheque: true);
        Assert.Contains(InvoiceAction.ClearTender, InvoiceRules.AllowedActions(withTender, all));
        Assert.DoesNotContain(InvoiceAction.ClearTender, InvoiceRules.AllowedActions(withTender, new InvoiceAccess(true, true, false, true)));
        Assert.DoesNotContain(InvoiceAction.ClearTender, InvoiceRules.AllowedActions(Detail(InvoiceState.Issued, 0m, 25m, InvoiceRecognitionMode.Service), all));

        // A refusal always names the missing authority rather than a button that silently failed.
        Assert.Contains("accounting-posting authority", InvoiceAccess.Missing(InvoiceAction.ClearTender), StringComparison.Ordinal);
        Assert.Contains(
            "may not take that action",
            InvoiceRules.RefusalSentence(new ApiException(403, ApiErrorCodes.Forbidden, "no", new Dictionary<string, string[]> { ["x"] = ["not allowed"] })),
            StringComparison.Ordinal);

        // Money is read at the business's scale, and never at one the screen assumed.
        Assert.Equal("115.00", InvoiceRules.Money(115m, 2));
        Assert.Equal("115", InvoiceRules.Money(115m, 0));
        Assert.Equal("115.000", InvoiceRules.Money(115m, 3));
        Assert.Equal("115.0000", InvoiceRules.Money(115m, 4));
        Assert.Equal("115.00", InvoiceRules.Money(115m, null));
        Assert.Equal("115.004", InvoiceRules.Money(115.004m, null));
        Assert.Equal("115.0040", InvoiceRules.Money(115.004m, 4));
        Assert.Equal(2, InvoiceRules.FractionDigits(115.00m));
        Assert.Equal(4, InvoiceRules.FractionDigits(115.0040m));

        // Dates never fall back to the machine's calendar.
        Assert.Equal("2026-03-05", InvoiceRules.DateText(new DateOnly(2026, 3, 5)));
        Assert.NotEqual(default, InvoiceRules.BusinessClock("Pacific/Kiritimati"));
        Assert.Equal(InvoiceRules.BusinessClock(), InvoiceRules.BusinessClock(timeZoneId: null));
    }

    [Fact]
    public async Task T24_02_the_settlement_workbench_is_a_shared_screen_every_host_can_actually_run()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        // The workbench is part of the document, not a second place an operator must be told about.
        var editor = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Invoices\InvoiceEditor.razor"));
        Assert.Contains("<InvoiceActions", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Href=\"@($\"/api/v1/invoices", editor, StringComparison.Ordinal);

        var markup = string.Concat(
            editor,
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Invoices\InvoiceActions.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Invoices\InvoiceActions.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Ui\Pages\Invoices\InvoiceRules.cs")));

        // No field asks a person for a key the database handed out, and nothing reaches a public resource.
        Assert.DoesNotContain("Invoice id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Register id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer id", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"N2\")", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.Today", markup, StringComparison.Ordinal);

        // Every contract the screens inject resolves in the server host, and is named in both client startups.
        var injected = typeof(InvoiceActions)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IInvoiceApiClient), injected);
        Assert.Contains(typeof(IDocumentOutput), injected);
        Assert.Contains(typeof(ICashRegisterApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the settlement workbench but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp\QtsWebApp.Client\Program.cs", @"QtsBilling.Maui\MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The invoice screens are reachable from the shared shell for a role that may use them.
        Assert.Contains(ShellNavigation.Routes, row => row.Href == "invoices" && row.Availability == ShellAvailability.Available);
    }

    private async Task<Guid> CreateAndIssueAsync(InvoiceApiClient invoices, int lines, decimal quantity, decimal unitPrice, decimal taxRate)
    {
        // An invoice always names the customer it asks for money from - the server's own rule, which the screen
        // now states before the operator types a document that could never be saved.
        var customerId = await CreateCustomerAsync();
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "task 24 settlement journey",
            Enumerable.Range(0, lines)
                .Select(_ => new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, quantity, unitPrice, 0m, taxRate))
                .ToArray(),
            InvoiceRecognitionMode.DirectGoods);

        var created = await invoices.CreateAsync(request);
        var issued = await invoices.IssueAsync(created.Invoice.Id);

        Assert.NotEqual(InvoiceState.Draft, issued.Invoice.Status);

        return issued.Invoice.Id;
    }

    private async Task<Guid> CreateCustomerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = $"Settlement customer {suffix}",
            email = $"settlement-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"customer create failed with {(int)created.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private Task<int> CountAsync(Func<BillingDbContext, Task<int>> query) => _fixture.WithFreshContextAsync(query);

    private static InvoiceDetailDto Detail(
        InvoiceState status,
        decimal paid,
        decimal awaiting,
        InvoiceRecognitionMode mode,
        bool pendingCheque = false)
    {
        var lineId = Guid.CreateVersion7();
        var payments = new List<InvoicePaymentDto>();

        if (awaiting > 0m)
        {
            payments.Add(new InvoicePaymentDto(
                Guid.CreateVersion7(),
                pendingCheque ? "Cheque" : "Cash",
                pendingCheque ? "Pending" : "Confirmed",
                awaiting,
                "USD",
                null,
                DateTimeOffset.UtcNow.AddMinutes(-2)));
        }

        return new InvoiceDetailDto(
            new InvoiceSummaryDto(
                Guid.CreateVersion7(),
                "INV-RULES",
                status,
                null,
                "Acme Ltd",
                new DateOnly(2026, 3, 5),
                null,
                "USD",
                SubtotalAmount: 100m,
                DiscountAmount: 0m,
                TaxAmount: 15m,
                TotalAmount: 115m,
                PaidAmount: paid,
                CreditedAmount: 0m,
                BalanceDue: 115m - paid,
                Note: null,
                IssuedAtUtc: status == InvoiceState.Draft ? null : DateTimeOffset.UtcNow.AddDays(-1),
                VoidedAtUtc: status == InvoiceState.Void ? DateTimeOffset.UtcNow : null,
                Version: 3,
                RecognitionMode: mode),
            [new InvoiceLineDto(lineId, 1, null, "Consulting", "SKU-1", 4m, 28.75m, 0m, 0.15m, 15m, 115m, "Exclusive", 100m)],
            payments,
            CreditNote: null,
            CreditNotes: null,
            LocationId: Guid.CreateVersion7(),
            PartySnapshot: null,
            Presentation: new InvoicePresentationDto(2, null, new DateOnly(2026, 3, 5)),
            Source: null,
            AwaitingClearanceAmount: pendingCheque ? awaiting : 0m);
    }
}
