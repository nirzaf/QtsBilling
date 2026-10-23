using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Accounting;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// Accounting as an operator can actually work it (specification task 32, cases T32.01 - T32.03). The three screens
/// are shipped shared pages wired into every host, and the gates they lean on are proved against the real server: an
/// unbalanced or unperioded posting is refused whole with its fields still readable, a period is closed only on the
/// version and reason the server checks - and a later correction carries its own date instead of rewriting the
/// window it answers, and a year-end transfer replayed under one key leaves exactly one closing entry with the
/// reports reconciling on both sides of it. The period-lock and transfer arithmetic themselves stay with
/// <see cref="PeriodPostingTests"/> and <see cref="FiscalYearCloseTests"/>, which this task retains unchanged.
/// </summary>
public sealed class AccountingJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public AccountingJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T32_the_accounting_screens_are_shared_pages_wired_into_every_host_and_ask_for_no_identifier()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        var pages = new[] { typeof(Accounts), typeof(Journals), typeof(Periods) };
        var routes = new[] { "accounting/accounts", "accounting/journals", "accounting/periods" };

        foreach (var (page, route) in pages.Zip(routes, (type, href) => (type, href)))
        {
            Assert.Contains(route, page
                .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
                .Select(template => template.Template.Trim('/')));
            var row = Single(route);
            Assert.Equal(ShellAvailability.Available, row.Availability);
            Assert.Null(row.ArrivalNote);
        }

        // Each row names the authority its own routes ask for, not a looser one that would hand a role a screen
        // full of refusals.
        Assert.Equal(Permissions.AccountingPost, Single("accounting/accounts").RequiredPermission);
        Assert.Equal(Permissions.ReportsView, Single("accounting/journals").RequiredPermission);
        Assert.Equal(Permissions.AccountingClose, Single("accounting/periods").RequiredPermission);

        var injected = pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IAccountingApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the accounting screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs", "QtsWebApp/QtsWebApp/Program.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        var markup = await ReadAllAsync(root, "QtsBilling.Ui/Pages/Accounting",
            ["Accounts.razor", "Journals.razor", "Periods.razor"]);
        var behaviour = await ReadAllAsync(root, "QtsBilling.Ui/Pages/Accounting",
            ["Accounts.razor.cs", "Journals.razor.cs", "Periods.razor.cs", "AccountingRules.cs"]);

        // Nothing on these screens asks a person to type a primary key, reaches for a remote resource, invents a
        // money scale, or reads a clock that is not the business's own (R07, F21).
        foreach (var forbidden in new[]
        {
            "Account id", "Period id", "Entry id", "Source id", "Guid.Parse", "Guid.TryParse", "access_token",
            "http://", "https://", "ToString(\"N2\")", "ToString(\"0.00\")", "DateTime.Today", "DateTime.Now",
            "DateTime.UtcNow", "Environment.",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, behaviour, StringComparison.Ordinal);
        }

        // Every command is reachable by name, and the figures are read back rather than retyped.
        foreach (var wanted in new[]
        {
            "search by name or code", "Read this account", "Read this entry", "Read its close",
            "Reason for closing this period", "Reason for the reversal", "Effective date",
            "Read the trial balance", "Transfer", "Settlement scale (decimal places)",
            "data-qts-state=\"unknown-outcome\"", "data-qts-region=\"evidence\"", "data-qts-region=\"year-close\"",
            "data-qts-region=\"trial-balance\"", "data-qts-region=\"controls\"", "data-qts-region=\"chart\"",
        })
        {
            Assert.Contains(wanted, markup, StringComparison.Ordinal);
        }

        // Error recovery: a lost answer keeps its key and its lines, and only a refusal that proves nothing was
        // written lets the operator start the act over.
        Assert.Contains("The server decides again", behaviour, StringComparison.Ordinal);
        Assert.Contains("intention.Release()", behaviour, StringComparison.Ordinal);
        Assert.Contains("AccountingRules.CommittedNothing(exception.Code)", behaviour, StringComparison.Ordinal);
        Assert.Contains("exception.Errors", behaviour, StringComparison.Ordinal);

        // The rules the screens recite are the rules the ledger enforces, said in the operator's words.
        Assert.Contains("the period the effective date falls in is the server's decision", AccountingRules.PostingRule, StringComparison.Ordinal);
        Assert.Contains("never edited or deleted", AccountingRules.ReversalRule, StringComparison.Ordinal);
        Assert.Contains("A confirmation on screen cannot bypass a server gate", AccountingRules.CloseRule, StringComparison.Ordinal);
        Assert.Contains("not an application shutdown", AccountingRules.FiscalCloseRule, StringComparison.Ordinal);
        Assert.Contains("A cash summary answers a different question", AccountingRules.StatementRule, StringComparison.Ordinal);
        Assert.Contains("An account with posted activity is never deleted", AccountingRules.ChartRule, StringComparison.Ordinal);

        // A closed or locked window offers no act, and nothing here edits a posted entry.
        Assert.Equal([AccountingRules.PeriodAction.Close], AccountingRules.AllowedPeriodActions(AccountingPeriodState.Open));
        Assert.Empty(AccountingRules.AllowedPeriodActions(AccountingPeriodState.Closed));
        Assert.Empty(AccountingRules.AllowedPeriodActions(AccountingPeriodState.Locked));
        Assert.Equal("locked by the year close", AccountingRules.StatusText(AccountingPeriodState.Locked));
        Assert.Equal("posted", AccountingRules.EntryStateText(JournalEntryState.Posted));
        Assert.Equal("not posted", AccountingRules.EntryStateText(JournalEntryState.Draft));

        // Drill-through is offered only where a screen exists that can read the document back (T32 recipe step 4).
        var sourceId = Guid.CreateVersion7();
        Assert.Equal("invoices", AccountingRules.SourceDocumentLink(JournalOrigin.Invoice, sourceId)?.Href.Split('/')[0]);
        Assert.Equal("sales", AccountingRules.SourceDocumentLink(JournalOrigin.Sale, sourceId)?.Href.Split('/')[0]);
        Assert.Equal("expenses", AccountingRules.SourceDocumentLink(JournalOrigin.Expense, sourceId)?.Href.Split('/')[0]);
        Assert.Null(AccountingRules.SourceDocumentLink(JournalOrigin.Purchase, sourceId));
        Assert.Null(AccountingRules.SourceDocumentLink(JournalOrigin.Invoice, null));

        // Money is named at the scale the business settles in, never at one this screen chose.
        Assert.Equal("110.00", AccountingRules.Money(110m, 2));
        Assert.Equal("110.000", AccountingRules.Money(110m, 3));
        Assert.Equal("110.00 QAR", AccountingRules.MoneyText(110m, 2, "QAR"));
        Assert.Equal("debit", AccountingRules.NormalSide(AccountKind.Asset));
        Assert.Equal("credit", AccountingRules.NormalSide(AccountKind.Equity));
    }

    [Fact]
    public async Task T32_01_an_unbalanced_or_unperioded_posting_is_refused_whole_and_its_fields_stay_reviewable()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ledger = new AccountingApiClient(http);
        var cash = (await ledger.ListAccountsAsync("1000")).Single();
        var revenue = (await ledger.ListAccountsAsync("4000")).Single();
        var inside = new DateOnly(2024, 1, 5);
        var before = await CountEntriesAsync(_fixture.BusinessA.BusinessId);

        // One line is unfinished work: the refusal names the field the operator has to complete.
        var oneLine = await CaptureAsync(() => ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                inside, "T32.01 one line", "QAR", JournalOrigin.Manual, null,
                [new JournalLineRequest(cash.Id, 50m, 0m, "only one side", null)]),
            Key("t32-01-one-line")));
        Assert.Equal(400, oneLine.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, oneLine.Code);
        Assert.True(oneLine.Errors.ContainsKey("lines"), $"the refusal named no line field: {string.Join(", ", oneLine.Errors.Keys)}");
        Assert.Contains("at least two lines", oneLine.Errors["lines"].Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not accepted as it stands", AccountingRules.RefusalSentence(oneLine, "this posting"), StringComparison.OrdinalIgnoreCase);

        // An unbalanced entry is refused whole, not trimmed to fit.
        var unbalanced = await CaptureAsync(() => ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                inside, "T32.01 unbalanced", "QAR", JournalOrigin.Manual, null,
                [
                    new JournalLineRequest(cash.Id, 100m, 0m, "debit", null),
                    new JournalLineRequest(revenue.Id, 0m, 90m, "credit", null),
                ]),
            Key("t32-01-unbalanced")));
        Assert.Equal(400, unbalanced.StatusCode);
        Assert.Equal(ApiErrorCodes.UnbalancedJournal, unbalanced.Code);
        Assert.Contains("nothing was written", AccountingRules.RefusalSentence(unbalanced, "this posting"), StringComparison.OrdinalIgnoreCase);

        // A date no period covers is refused with its reason, not filed into whichever window happens to be open.
        var outside = await CaptureAsync(() => ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                new DateOnly(1990, 1, 1), "T32.01 outside every period", "QAR", JournalOrigin.Manual, null,
                [
                    new JournalLineRequest(cash.Id, 10m, 0m, "debit", null),
                    new JournalLineRequest(revenue.Id, 0m, 10m, "credit", null),
                ]),
            Key("t32-01-outside")));
        Assert.Equal(409, outside.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodMissing, outside.Code);
        Assert.Contains("found no accounting period", AccountingRules.RefusalSentence(outside, "this posting"), StringComparison.OrdinalIgnoreCase);

        // Nothing was committed by any of the three, so the same key may be reused once the fields are fixed.
        Assert.Equal(before, await CountEntriesAsync(_fixture.BusinessA.BusinessId));
        Assert.Empty(await MemosAsync(_fixture.BusinessA.BusinessId, "T32.01 "));
        Assert.True(AccountingRules.CommittedNothing(oneLine.Code));
        Assert.True(AccountingRules.CommittedNothing(unbalanced.Code));
        Assert.True(AccountingRules.CommittedNothing(outside.Code));
        Assert.False(AccountingRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(AccountingRules.LooksBalanced([(100m, 0m), (0m, 90m)]));
        Assert.True(AccountingRules.LooksBalanced([(100m, 0m), (0m, 100m)]));
        Assert.False(AccountingRules.CanPost(mayPost: true, hasOpenPeriod: true, lineCount: 1));
        Assert.True(AccountingRules.CanPost(mayPost: true, hasOpenPeriod: true, lineCount: 2));
    }

    [Fact]
    public async Task T32_02_a_close_is_checked_on_the_version_and_reason_and_a_later_correction_keeps_its_own_date()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var ledger = new AccountingApiClient(http);
        var businessB = _fixture.BusinessB.BusinessId;
        var receivable = (await ledger.ListAccountsAsync("1100")).Single();
        var revenue = (await ledger.ListAccountsAsync("4000")).Single();

        // A window of its own, so a close can be proved on facts no other test writes into.
        var period = await ledger.CreatePeriodAsync(new SaveAccountingPeriodRequest(
            "T32 1990 review window", new DateOnly(1990, 1, 1), new DateOnly(1990, 12, 31)));

        var opened = await ledger.GetPeriodCloseEvidenceAsync(period.Id);
        Assert.True(opened.PeriodOpen);
        Assert.Equal(2, opened.SettlementScale);
        Assert.Equal("Asia/Qatar", opened.AccountingZone);
        Assert.Equal(0, opened.PostedEntryCount);
        Assert.Equal(0m, opened.PostedDebit);
        Assert.False(opened.YearComplete);
        Assert.Null(opened.YearGapFrom);
        Assert.Empty(opened.TrialBalance);
        Assert.All(opened.Outstanding.Where(item => item.BlocksClose), item => Assert.Equal(0, item.Count));
        Assert.False(string.IsNullOrWhiteSpace(opened.AccountingZone));

        // A close is an explained act under a key, and the server checks each of those before it touches anything.
        var noReason = await CaptureAsync(() => ledger.ClosePeriodAsync(
            period.Id, new CloseAccountingPeriodRequest(opened.Period.Version, " "), Key("t32-02-no-reason")));
        Assert.Equal(400, noReason.StatusCode);
        Assert.True(noReason.Errors.ContainsKey("reason"), "a close without a reason must name the reason field");
        Assert.True((await ledger.GetPeriodCloseEvidenceAsync(period.Id)).PeriodOpen);

        var noKey = await CaptureAsync(() => ledger.ClosePeriodAsync(
            period.Id, new CloseAccountingPeriodRequest(opened.Period.Version, "reviewed"), idempotencyKey: " "));
        Assert.Equal(400, noKey.StatusCode);
        Assert.True(noKey.Errors.ContainsKey("Idempotency-Key"), "a close without a key must name the header it needs");

        var stale = await CaptureAsync(() => ledger.ClosePeriodAsync(
            period.Id,
            new CloseAccountingPeriodRequest(opened.Period.Version + 1, "closed over somebody else's decision"),
            Key("t32-02-stale")));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);
        Assert.Contains("Read it back", AccountingRules.RefusalSentence(stale, "the close"), StringComparison.OrdinalIgnoreCase);
        Assert.True((await ledger.GetPeriodCloseEvidenceAsync(period.Id)).PeriodOpen,
            "a close refused on its version must leave the period open for the reviewer to read again");

        // Post inside the window; the evidence read is the same arithmetic the ledger holds.
        var posted = await ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                new DateOnly(1990, 6, 30), "T32.02 accrual", null, JournalOrigin.Manual, null,
                [
                    new JournalLineRequest(receivable.Id, 300m, 0m, "owed", null),
                    new JournalLineRequest(revenue.Id, 0m, 300m, "earned", null),
                ]),
            Key("t32-02-post"));
        Assert.Equal(JournalEntryState.Posted, posted.Status);

        var withActivity = await ledger.GetPeriodCloseEvidenceAsync(period.Id);
        Assert.Equal(1, withActivity.PostedEntryCount);
        Assert.Equal(300m, withActivity.PostedDebit);
        Assert.Equal(300m, withActivity.PostedCredit);
        Assert.Equal(300m, withActivity.ReceivableBalance);
        Assert.Equal(300m, withActivity.NetIncome);
        Assert.Equal(0m, withActivity.PayableBalance);
        Assert.Equal(0m, withActivity.DrawerCash);
        Assert.Equal(0, withActivity.DraftJournalCount);
        Assert.Equal(0, withActivity.UnconfirmedPaymentCount);
        Assert.Equal(0, withActivity.OpenCustodyCases);
        Assert.Equal(2, withActivity.TrialBalance.Count);
        Assert.Equal(300m, withActivity.TrialBalance.Single(row => row.Code == "1100").Balance);
        Assert.Equal(-300m, withActivity.TrialBalance.Single(row => row.Code == "4000").Balance);
        Assert.True(AccountingRules.CanClose(withActivity, mayClose: true), DescribeBlockers(withActivity));
        Assert.Empty(AccountingRules.CloseBlocker(withActivity));
        Assert.False(AccountingRules.CanClose(withActivity, mayClose: false));

        var closeKey = Key("t32-02-close");
        var closeBody = new CloseAccountingPeriodRequest(withActivity.Period.Version, "1990 review window closed after reading its evidence");
        var closed = await ledger.ClosePeriodAsync(period.Id, closeBody, closeKey);
        Assert.Equal(AccountingPeriodState.Closed, closed.Status);
        Assert.True(closed.Version > withActivity.Period.Version,
            "a closed period must carry the version the next act is checked against");

        // A replayed answer about the same act returns the same close instead of closing twice.
        var replay = await ledger.ClosePeriodAsync(period.Id, closeBody, closeKey);
        Assert.Equal(closed.Version, replay.Version);

        // The same key with a different act is refused whole, and the close on the record is still the one reason.
        var repurposed = await CaptureAsync(() => ledger.ClosePeriodAsync(
            period.Id, new CloseAccountingPeriodRequest(withActivity.Period.Version, "a different reason under the same key"), closeKey));
        Assert.Equal(ApiErrorCodes.SameKeyDifferentPayload, repurposed.Code);
        Assert.Equal(1, await CountAuditAsync(businessB, period.Id, "closed"));
        Assert.Contains("closed after reading its evidence", await CloseAuditSummaryAsync(businessB, period.Id), StringComparison.Ordinal);

        // Posting into the closed window is refused - and it is not quietly dated into the open window instead.
        var intoClosed = await CaptureAsync(() => ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                new DateOnly(1990, 7, 1), "T32.02 must not land", null, JournalOrigin.Manual, null,
                [
                    new JournalLineRequest(receivable.Id, 20m, 0m, "debit", null),
                    new JournalLineRequest(revenue.Id, 0m, 20m, "credit", null),
                ]),
            Key("t32-02-into-closed")));
        Assert.Equal(409, intoClosed.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodClosed, intoClosed.Code);
        Assert.Contains("will not accept a silent backdate", AccountingRules.RefusalSentence(intoClosed, "this posting"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["T32.02 accrual"], await MemosAsync(businessB, "T32.02 "));
        Assert.Equal(0, await CountEntriesDatedAsync(businessB, new DateOnly(1990, 7, 1)));

        // The screen offers no second close, and its blocker says why.
        var afterClose = await ledger.GetPeriodCloseEvidenceAsync(period.Id);
        Assert.False(afterClose.PeriodOpen);
        Assert.False(AccountingRules.CanClose(afterClose, mayClose: true));
        Assert.Contains("is already closed", AccountingRules.CloseBlocker(afterClose), StringComparison.OrdinalIgnoreCase);

        // A correction whose date defaults into the closed window is refused.
        var sameDay = await CaptureAsync(() => ledger.ReverseJournalAsync(
            posted.Id, new ReverseJournalRequest(null, "posted in error"), Key("t32-02-default-date")));
        Assert.Equal(409, sameDay.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodClosed, sameDay.Code);

        // Dated on the business's own day - read from the server, not from this machine's clock - the reversal lands
        // in the open window, names the entry it answers, and leaves the closed window exactly as it was (D10).
        var today = afterClose.BusinessToday;
        var reversal = await ledger.ReverseJournalAsync(
            posted.Id, new ReverseJournalRequest(today, "reversed after the window closed"), Key("t32-02-correction"));

        Assert.Equal(JournalOrigin.Reversal, reversal.Origin);
        Assert.Equal(posted.Id, reversal.ReversalOfEntryId);
        Assert.Equal(today, reversal.EntryDate);
        Assert.Equal(300m, reversal.TotalDebit);
        Assert.Equal(300m, reversal.TotalCredit);
        Assert.Equal(revenue.Id, reversal.Lines.Single(line => line.Debit == 300m).AccountId);
        Assert.Equal(receivable.Id, reversal.Lines.Single(line => line.Credit == 300m).AccountId);

        // A posted entry is reversed at most once, and the refusal is the screen's sentence about it.
        var twice = await CaptureAsync(() => ledger.ReverseJournalAsync(
            posted.Id, new ReverseJournalRequest(today, "twice"), Key("t32-02-twice")));
        Assert.Equal(ApiErrorCodes.IdempotencyKeyReused, twice.Code);
        Assert.Contains("reversed at most once", AccountingRules.RefusalSentence(twice, "this reversal"), StringComparison.OrdinalIgnoreCase);

        // The entry it answers is untouched, and the closed window's own statement still reads as it did.
        var original = await ledger.GetJournalAsync(posted.Id);
        Assert.Equal(new DateOnly(1990, 6, 30), original.EntryDate);
        Assert.Equal(300m, original.TotalDebit);
        Assert.Equal(300m, original.TotalCredit);
        Assert.Equal(receivable.Id, original.Lines.Single(line => line.Debit == 300m).AccountId);

        var closedWindow = await ledger.GetTrialBalanceAsync(new DateOnly(1990, 1, 1), new DateOnly(1990, 12, 31));
        Assert.Equal(300m, closedWindow.TotalDebit);
        Assert.Equal(300m, closedWindow.TotalCredit);
        Assert.Equal(300m, closedWindow.Rows.Single(row => row.Code == "1100").Balance);

        var correctionWindow = await ledger.GetTrialBalanceAsync(today, today);
        Assert.Equal(300m, correctionWindow.TotalDebit);
        Assert.Equal(receivable.Id, correctionWindow.Rows.Single(row => row.Code == "1100").AccountId);
        Assert.Equal(-300m, correctionWindow.Rows.Single(row => row.Code == "1100").Balance);

        // Every entry on the books still balances, and the reversal is not allowed to look like an edit.
        await _fixture.WithFreshContextAsync(async db =>
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, businessB));
    }

    [Fact]
    public async Task T32_03_a_year_close_replayed_under_one_key_leaves_one_transfer_and_the_reports_reconcile()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ledger = new AccountingApiClient(http);
        var businessA = _fixture.BusinessA.BusinessId;
        const int fiscalYear = 2024;
        var yearStart = new DateOnly(fiscalYear, 1, 1);
        var yearEnd = new DateOnly(fiscalYear, 12, 31);

        var receivable = (await ledger.ListAccountsAsync("1100")).Single();
        var revenue = (await ledger.ListAccountsAsync("4000")).Single();
        var cogs = (await ledger.ListAccountsAsync("5000")).Single();
        var cash = (await ledger.ListAccountsAsync("1000")).Single();

        await ledger.PostJournalAsync(new PostJournalEntryRequest(
            new DateOnly(fiscalYear, 6, 30), "T32.03 year revenue", "QAR", JournalOrigin.Manual, null,
            [new JournalLineRequest(receivable.Id, 300m, 0m, "owed", null), new JournalLineRequest(revenue.Id, 0m, 300m, "earned", null)]),
            Key("t32-03-revenue"));
        await ledger.PostJournalAsync(new PostJournalEntryRequest(
            new DateOnly(fiscalYear, 7, 31), "T32.03 year cost", "QAR", JournalOrigin.Manual, null,
            [new JournalLineRequest(cogs.Id, 120m, 0m, "consumed", null), new JournalLineRequest(cash.Id, 0m, 120m, "paid", null)]),
            Key("t32-03-cost"));

        var before = await ledger.GetTrialBalanceAsync(yearStart, yearEnd);
        Assert.Equal(420m, before.TotalDebit);
        Assert.Equal(420m, before.TotalCredit);
        Assert.Equal(300m, before.Rows.Single(row => row.Code == "4000").Credit);
        Assert.Equal(120m, before.Rows.Single(row => row.Code == "5000").Debit);
        Assert.DoesNotContain(before.Rows, row => row.Code == "3000");

        // The year-end read is the same arithmetic the transfer itself will use: revenue, expense, and the result
        // between them - not the two added together.
        var open = await ledger.GetYearCloseEvidenceAsync(fiscalYear);
        Assert.Equal(300m, open.Revenue);
        Assert.Equal(120m, open.Expenses);
        Assert.Equal(180m, open.NetIncome);
        Assert.Equal(1, open.PeriodCount);
        Assert.Equal(1, open.OpenPeriodCount);
        Assert.True(open.CoversWholeYear);
        Assert.False(open.AlreadyClosed);
        Assert.True(open.RetainedEarningsReady);
        Assert.Equal("3000", open.RetainedEarningsCode);
        Assert.Equal("Asia/Qatar", open.AccountingZone);
        Assert.Equal(2, open.SettlementScale);
        Assert.Null(open.GapFrom);
        Assert.Empty(open.ClosingEntries);
        Assert.False(AccountingRules.CanCloseYear(open, mayClose: true));
        Assert.Contains("still open", AccountingRules.YearSentence(open), StringComparison.OrdinalIgnoreCase);

        // The gate is the server's: a caller holding the authority and a key is still refused while a period is open.
        var early = await CaptureAsync(() => ledger.CloseYearAsync(fiscalYear, Key("t32-03-early")));
        Assert.Equal(409, early.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodClosed, early.Code);

        // Close the period the year lives in, on its own evidence, and the transfer becomes offerable.
        var periods = await ledger.ListPeriodsAsync();
        var period = Assert.Single(periods, row => row.Id == _fixture.BusinessA.AccountingPeriodId);
        var evidence = await ledger.GetPeriodCloseEvidenceAsync(period.Id);
        Assert.True(AccountingRules.CanClose(evidence, mayClose: true), DescribeBlockers(evidence));
        Assert.Equal(180m, evidence.NetIncome);
        Assert.Equal(open.NetIncome, evidence.NetIncome);
        await ledger.ClosePeriodAsync(
            period.Id,
            new CloseAccountingPeriodRequest(evidence.Period.Version, "the window covering this year is reviewed and closed"),
            Key("t32-03-close-period"));

        var ready = await ledger.GetYearCloseEvidenceAsync(fiscalYear);
        Assert.Equal(0, ready.OpenPeriodCount);
        Assert.True(AccountingRules.CanCloseYear(ready, mayClose: true),
            $"{ready.FiscalYear}: open={ready.OpenPeriodCount} covers={ready.CoversWholeYear} retained={ready.RetainedEarningsReady} already={ready.AlreadyClosed} gap={ready.GapFrom} "
                + string.Join("; ", ready.Outstanding.Select(item => $"{item.Kind}={item.Count} blocks={item.BlocksClose}")));
        Assert.Contains("180.00", AccountingRules.YearSentence(ready), StringComparison.Ordinal);
        Assert.Contains("3000", AccountingRules.YearSentence(ready), StringComparison.Ordinal);

        var yearKey = Key("t32-03-close-year");
        var first = await ledger.CloseYearAsync(fiscalYear, yearKey);
        Assert.Equal(fiscalYear, first.FiscalYear);
        Assert.Equal(yearEnd, first.ClosingDate);
        Assert.Equal(180m, first.NetIncome);
        Assert.Equal(1, first.LockedPeriodCount);
        Assert.False(string.IsNullOrWhiteSpace(first.JournalEntryNumber));

        // The same key answers the same act: one transfer, the same entry number, no second move of the result.
        var replay = await ledger.CloseYearAsync(fiscalYear, yearKey);
        Assert.Equal(first.JournalEntryNumber, replay.JournalEntryNumber);
        Assert.Equal(first.NetIncome, replay.NetIncome);

        var freshKey = await CaptureAsync(() => ledger.CloseYearAsync(fiscalYear, Key("t32-03-fresh-key")));
        Assert.Equal(409, freshKey.StatusCode);
        Assert.Equal(ApiErrorCodes.Conflict, freshKey.Code);
        Assert.Contains("year-end close has already completed", freshKey.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await CountClosingEntriesAsync(businessA, yearStart, yearEnd));

        // The reports reconcile on both sides of the close: the year's result has left revenue and expense and
        // stands in retained earnings, and nothing is out of balance anywhere on the books.
        var after = await ledger.GetTrialBalanceAsync(yearStart, yearEnd);
        Assert.Equal(720m, after.TotalDebit);
        Assert.Equal(720m, after.TotalCredit);
        Assert.Equal(0m, after.Rows.Single(row => row.Code == "4000").Balance);
        Assert.Equal(0m, after.Rows.Single(row => row.Code == "5000").Balance);
        Assert.Equal(-180m, after.Rows.Single(row => row.Code == "3000").Balance);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var locked = await db.AccountingPeriods.AsNoTracking().SingleAsync(row => row.Id == period.Id);
            Assert.Equal(QtsBilling.Persistence.Postgres.AccountingPeriodStatus.Locked, locked.Status);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, businessA);
        });

        // A locked year is not reopened by trying to post into it; the only way back is a new audited entry.
        var intoLocked = await CaptureAsync(() => ledger.PostJournalAsync(
            new PostJournalEntryRequest(
                new DateOnly(fiscalYear, 11, 30), "T32.03 after the lock", "QAR", JournalOrigin.Manual, null,
                [new JournalLineRequest(receivable.Id, 5m, 0m, "debit", null), new JournalLineRequest(revenue.Id, 0m, 5m, "credit", null)]),
            Key("t32-03-into-locked")));
        Assert.Equal(409, intoLocked.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodClosed, intoLocked.Code);
        Assert.Equal(1, await CountClosingEntriesAsync(businessA, yearStart, yearEnd));

        // The evidence read after the close is the record of the act, and the screen says so instead of offering it
        // a second time.
        var done = await ledger.GetYearCloseEvidenceAsync(fiscalYear);
        Assert.True(done.AlreadyClosed);
        Assert.Single(done.ClosingEntries);
        Assert.Equal(1, done.LockedPeriodCount);
        Assert.Equal(first.JournalEntryNumber, done.ClosingEntries.Single().EntryNumber);
        Assert.False(AccountingRules.CanCloseYear(done, mayClose: true));
        Assert.Contains("already on the record", AccountingRules.YearSentence(done), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A second transfer would move the same result twice", AccountingRules.YearSentence(done), StringComparison.Ordinal);

        var periodEvidence = await ledger.GetPeriodCloseEvidenceAsync(period.Id);
        Assert.False(periodEvidence.PeriodOpen);
        Assert.True(periodEvidence.YearAlreadyClosed);
    }

    // ------------------------------------------------------------------ helpers

    private static string Key(string act)
    {
        var key = $"{act}-{Guid.NewGuid():N}";
        return key.Length <= 64 ? key : key[^64..];
    }

    private static string DescribeBlockers(PeriodCloseEvidenceDto evidence)
        => $"{evidence.Period.Name} blockers: {AccountingRules.CloseBlocker(evidence)} | outstanding: "
            + string.Join("; ", evidence.Outstanding.Select(item => $"{item.Kind}={item.Count}/{item.Amount} blocks={item.BlocksClose}"));

    private static ShellRoute Single(string href)
        => Assert.Single(ShellNavigation.Routes, route => route.Href == href);

    private static async Task<string> ReadAllAsync(string root, string directory, string[] names)
    {
        var texts = new System.Text.StringBuilder();
        foreach (var name in names)
        {
            texts.Append(await File.ReadAllTextAsync(Path.Combine(root, directory, name)));
        }

        return texts.ToString();
    }

    private async Task<int> CountEntriesAsync(Guid businessId)
        => await _fixture.WithFreshContextAsync(db => Task.FromResult(
            db.JournalEntries.AsNoTracking().Count(entry => entry.BusinessId == businessId)));

    private async Task<int> CountEntriesDatedAsync(Guid businessId, DateOnly entryDate)
        => await _fixture.WithFreshContextAsync(db => Task.FromResult(
            db.JournalEntries.AsNoTracking().Count(entry => entry.BusinessId == businessId && entry.EntryDate == entryDate)));

    private async Task<int> CountClosingEntriesAsync(Guid businessId, DateOnly start, DateOnly end)
        => await _fixture.WithFreshContextAsync(db => Task.FromResult(
            db.JournalEntries.AsNoTracking().Count(entry => entry.BusinessId == businessId
                && entry.SourceType == QtsBilling.Persistence.Postgres.JournalSourceType.FiscalYearClose
                && entry.EntryDate >= start && entry.EntryDate <= end)));

    private async Task<IReadOnlyList<string?>> MemosAsync(Guid businessId, string prefix)
        => await _fixture.WithFreshContextAsync(async db => await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.Memo != null && entry.Memo.StartsWith(prefix))
            .OrderBy(entry => entry.EntryNumber)
            .Select(entry => entry.Memo)
            .ToListAsync());

    private async Task<string> CloseAuditSummaryAsync(Guid businessId, Guid periodId)
        => await _fixture.WithFreshContextAsync(async db => (await db.AuditEvents.AsNoTracking()
            .SingleAsync(row => row.BusinessId == businessId && row.EntityId == periodId && row.Action == "closed")).Summary ?? string.Empty);

    private async Task<int> CountAuditAsync(Guid businessId, Guid entityId, string action)
        => await _fixture.WithFreshContextAsync(async db => await db.AuditEvents.AsNoTracking()
            .CountAsync(row => row.BusinessId == businessId && row.EntityId == entityId && row.Action == action));

    private static async Task<ApiException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException failure)
        {
            return failure;
        }

        throw new Xunit.Sdk.XunitException("the request should have been refused, but it succeeded");
    }
}
