using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Banking;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Finance;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The bank-reconciliation workbench as a shipped screen (specification task 31, cases T31.01 - T31.03 from the
/// accountant's side): the page exists in the shared assembly, every host that renders it can resolve what it
/// injects, its markup asks for no identifier a person could not know, and the reads it relies on answer with the
/// names, entry numbers, period and figures needed to decide anything. The recognition arithmetic itself belongs to
/// <see cref="BankingEndpointsTests"/>; this file proves the screen is reachable, honest about what it can do, and
/// unable to reach another business's statement.
/// </summary>
public sealed class BankingJourneyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public BankingJourneyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T31_the_rules_keep_a_match_a_correction_a_movement_and_a_finalisation_apart()
    {
        // A finalised review offers reopening and nothing else; an open one can be matched, moved and closed.
        Assert.Equal([BankingRules.BankReconciliationAction.Match, BankingRules.BankReconciliationAction.RecordMovement, BankingRules.BankReconciliationAction.Finalise],
            BankingRules.AllowedActions(BankReconciliationState.Open));
        Assert.Equal([BankingRules.BankReconciliationAction.Reopen], BankingRules.AllowedActions(BankReconciliationState.Finalised));

        // The sign decides the act: the bank taking money is a charge closing against an expense, the bank paying it
        // is interest closing against revenue. The screen never asks an operator to choose the bank leg.
        Assert.True(BankingRules.IsCharge(Line(-15m)));
        Assert.False(BankingRules.IsCharge(Line(125m)));
        Assert.Equal("Record the bank charge", BankingRules.MovementTitle(Line(-15m)));
        Assert.Equal("Record the interest received", BankingRules.MovementTitle(Line(125m)));
        Assert.Equal("an expense account", BankingRules.CounterAccountKind(Line(-15m)));
        Assert.Equal("a revenue account", BankingRules.CounterAccountKind(Line(125m)));

        // The rules are stated in the screen's own words, before anything is loaded.
        Assert.Contains("never proof", BankingRules.MatchRule, StringComparison.Ordinal);
        Assert.Contains("stays on the record", BankingRules.CorrectionRule, StringComparison.Ordinal);
        Assert.Contains("never written off", BankingRules.MovementRule, StringComparison.Ordinal);
        Assert.Contains("stay visible as exceptions", BankingRules.FinaliseRule, StringComparison.Ordinal);
        Assert.Contains("not the accounting period", BankingRules.PeriodRule, StringComparison.Ordinal);

        // Money is read at the scale the statement was composed in, with the account's currency beside it.
        var review = Review(scale: 2, matched: 110m, ledgerSide: 110m, unmatched: 0m, unmatchedCount: 0, addsUp: true);
        Assert.Equal("110.00", BankingRules.Money(110m, review));
        Assert.Equal("110.00 QAR", BankingRules.MoneyText(110m, review));
        Assert.Equal("110.00", BankingRules.Money(110m, null));
        Assert.Contains("Reconciled", BankingRules.ReconciliationSentence(review), StringComparison.Ordinal);
        Assert.Contains("999.000", BankingRules.Money(999m, Review(scale: 3, matched: 0m, ledgerSide: 0m, unmatched: 0m, unmatchedCount: 0, addsUp: true)), StringComparison.Ordinal);

        // A statement that does not tie is said so, and the blocker names what is missing rather than a colour.
        var held = Review(scale: 2, matched: 95m, ledgerSide: 95m, unmatched: 15m, unmatchedCount: 1, addsUp: true);
        Assert.Contains("Not reconciled", BankingRules.ReconciliationSentence(held), StringComparison.Ordinal);
        Assert.Contains("still unmatched", BankingRules.FinaliseBlocker(held), StringComparison.Ordinal);
        Assert.False(BankingRules.CanFinalise(held));
        Assert.True(BankingRules.CanFinalise(review));
        Assert.Empty(BankingRules.FinaliseBlocker(review));
        Assert.Contains("do not add up",
            BankingRules.FinaliseBlocker(Review(scale: 2, matched: 0m, ledgerSide: 0m, unmatched: 0m, unmatchedCount: 0, addsUp: false)),
            StringComparison.Ordinal);

        // The period the statement falls in is reported with its consequence, in both directions.
        Assert.Contains("is open", BankingRules.PeriodSentence(review with { AccountingPeriodName = "October 2026" }), StringComparison.Ordinal);
        Assert.Contains("stay as they are", BankingRules.PeriodSentence(review with { AccountingPeriodName = "October 2026", AccountingPeriodClosed = true }), StringComparison.Ordinal);
        Assert.Contains("No accounting period", BankingRules.PeriodSentence(review with { AccountingPeriodName = null }), StringComparison.Ordinal);

        // A statement is named by its date and file, and a decision by both sides of it plus the reason.
        Assert.Contains("2026-03-31", BankingRules.StatementLabel(review.Statement.Import), StringComparison.Ordinal);
        Assert.Contains("statement.csv", BankStatements.Import().FileName, StringComparison.Ordinal);
        Assert.Equal("finalised", BankingRules.StatusText(BankReconciliationState.Finalised));
        Assert.Equal("open for correction", BankingRules.StatusText(BankReconciliationState.Open));
        var corrected = new BankReconciliationEventDto(Guid.NewGuid(), 2, "unmatched", -15m, "head office queried it", "JE-2026-0041", null, DateTimeOffset.UtcNow, "Amina");
        Assert.Contains("JE-2026-0041 released", BankingRules.EventSentence(corrected), StringComparison.Ordinal);
        Assert.Contains("head office queried it", BankingRules.EventSentence(corrected), StringComparison.Ordinal);
        Assert.Contains("matched to a posted journal line", BankingRules.KindText("matched"), StringComparison.Ordinal);
        Assert.Contains("reviewed journal", BankingRules.KindText("fee"), StringComparison.Ordinal);
        Assert.Contains("Line 2", BankingRules.EventSentence(corrected), StringComparison.Ordinal);
        Assert.Contains("Statement:", BankingRules.EventSentence(corrected with { LineNo = 0, Kind = "finalised" }), StringComparison.Ordinal);

        // Only refusals that prove nothing was written release a held intention; an unknown answer keeps it.
        Assert.True(BankingRules.CommittedNothing(ApiErrorCodes.BankStatementFinalised));
        Assert.True(BankingRules.CommittedNothing(ApiErrorCodes.BankStatementNotReconciled));
        Assert.True(BankingRules.CommittedNothing(ApiErrorCodes.BankCorrectionReasonRequired));
        Assert.True(BankingRules.CommittedNothing(ApiErrorCodes.OptimisticConcurrencyFailure));
        Assert.False(BankingRules.CommittedNothing(ApiErrorCodes.InternalError));
        Assert.False(BankingRules.CommittedNothing(code: null));

        // One key per intention, bounded the way the server validates it, distinct per act and per statement.
        var importId = Guid.CreateVersion7();
        var key = BankingRules.NewKey("finalise", importId);
        Assert.True(key.Length <= BankingRules.MaxKeyLength);
        Assert.StartsWith("finalise-", key, StringComparison.Ordinal);
        Assert.NotEqual(key, BankingRules.NewKey("reopen", importId));
        Assert.NotEqual(key, BankingRules.NewKey("finalise", Guid.CreateVersion7()));

        // A refusal names the rule and the next act, not a status code.
        string Sentence(string code, string message = "refused") => BankingRules.RefusalSentence(
            new ApiException(409, code, message, new Dictionary<string, string[]>()), "this correction");

        Assert.Contains("reopen the review", Sentence(ApiErrorCodes.BankStatementFinalised), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("answered by a line", Sentence(ApiErrorCodes.BankStatementNotReconciled), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reason an auditor", Sentence(ApiErrorCodes.BankCorrectionReasonRequired), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read it back", Sentence(ApiErrorCodes.OptimisticConcurrencyFailure), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing new was posted", Sentence(ApiErrorCodes.SameKeyDifferentPayload), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not accept a posting", Sentence(ApiErrorCodes.AccountingPeriodClosed), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was changed against it", Sentence(ApiErrorCodes.NotFound), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not accepted as it stands", Sentence(ApiErrorCodes.ValidationFailed), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was assumed", Sentence(ApiErrorCodes.InternalError), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T31_the_bank_screen_is_wired_into_every_host_that_can_show_it()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        Assert.Contains("finance/banking", typeof(Banking)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Select(route => route.Template.Trim('/')));

        var row = Assert.Single(ShellNavigation.Routes, route => route.Href == "finance/banking");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.Equal(Permissions.AccountingPost, row.RequiredPermission);
        Assert.Null(row.ArrivalNote);

        var injected = typeof(Banking)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IBankingApiClient), injected);
        Assert.Contains(typeof(IAccountApiClient), injected);
        Assert.Contains(typeof(QtsBilling.Contracts.Accounting.IAccountingApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the bank screen but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs", "QtsWebApp/QtsWebApp/Program.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} does not name: {string.Join(", ", unnamed)}");
        }

        // The shipped markup asks for no key, reaches for no public resource, and hardcodes no scale or clock.
        var markup = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Finance/Banking.razor")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Finance/Banking.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Finance/BankingRules.cs")));

        foreach (var forbidden in new[]
        {
            "Bank account id", "Statement id", "Line id", "Journal line id", "Import id", "access_token",
            "http://", "https://", "ToString(\"N2\")", "DateTime.Today", "Guid.Parse", "new Blob",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        Assert.Contains("search by name", markup, StringComparison.Ordinal);
        Assert.Contains("Read the statement back", markup, StringComparison.Ordinal);
        Assert.Contains("data-qts-state", markup, StringComparison.Ordinal);

        // A statement is read locally, from the file the bank gave: no provider, no remote download, and the
        // ceilings the server enforces are the ceilings the screen offers.
        Assert.Contains("Import a bank statement", markup, StringComparison.Ordinal);
        Assert.Contains("No bank provider", markup, StringComparison.Ordinal);
        Assert.Contains("accept=\".csv\"", markup, StringComparison.Ordinal);
        Assert.Contains("Date, Description, Reference, Amount", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenReadStream(0)", markup, StringComparison.Ordinal);
        Assert.Contains("MaxStatementBytes = 5 * 1024 * 1024", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T31_the_review_names_the_account_the_period_and_the_candidates_and_another_business_cannot_reach_it()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var banking = new BankingApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var bankLedger = (await new QtsBilling.Contracts.Accounting.AccountingApiClient(http)
            .ListAccountsAsync("1010")).Single();
        var revenue = (await new QtsBilling.Contracts.Accounting.AccountingApiClient(http)
            .ListAccountsAsync("4000")).Single();

        var account = await banking.CreateAccountAsync(new CreateBankAccountRequest(
            $"Screen account {suffix}", "****1122", null, "QAR", bankLedger.Id));
        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate:yyyy-MM-dd},\"Card settlement\",CARD-{suffix},210.00\n";
        var detail = await banking.ImportStatementAsync(
            account.Id, $"march-{suffix}.csv", valueDate, 0m, 210m, System.Text.Encoding.UTF8.GetBytes(csv),
            $"t31-screen-import-{suffix}");

        // A statement list is enough to open a review: the operator picks the file, never the identifier.
        var statements = await banking.ListStatementsAsync(account.Id);
        var listed = Assert.Single(statements, row => row.Id == detail.Import.Id);
        Assert.Equal($"march-{suffix}.csv", listed.FileName);
        Assert.Equal(210m, listed.ClosingBalance);
        Assert.Equal(BankReconciliationState.Open, listed.Status);
        Assert.True(listed.ExpectedVersion > 0, "a statement the screen can close must carry the version a close is checked against");

        await PostSettlementAsync(http, valueDate, bankLedger.Id, revenue.Id, 210m);

        var review = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Equal(account.Id, review.Account.Id);
        Assert.Equal($"Screen account {suffix}", review.Account.Name);
        Assert.Equal(bankLedger.Code, review.Account.LedgerAccountCode);
        Assert.Equal("QAR", review.Account.Currency);
        Assert.Equal(2, review.Arithmetic.SettlementScale);
        Assert.False(string.IsNullOrWhiteSpace(review.Arithmetic.TimeZoneId));
        Assert.False(string.IsNullOrWhiteSpace(review.AccountingPeriodName));
        Assert.False(review.AccountingPeriodClosed);
        Assert.Equal(0m, review.Arithmetic.MatchedTotal);
        Assert.Equal(210m, review.Arithmetic.UnmatchedTotal);
        Assert.Single(review.Lines.Single(row => row.LineNo == 1).Candidates, candidate => candidate.Amount == 210m);
        var candidate = review.Lines.Single(row => row.LineNo == 1).Candidates.Single();
        Assert.False(string.IsNullOrWhiteSpace(candidate.EntryNumber));
        Assert.False(string.IsNullOrWhiteSpace(candidate.AccountName));

        // Reconciled, then read back: the entry number is on the line, and the two sides carry the same money.
        var line = review.Statement.Lines.Single();
        await banking.MatchLineAsync(line.Id, new MatchStatementLineRequest(candidate.JournalLineId, line.ExpectedVersion));
        var tied = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Equal(210m, tied.Arithmetic.MatchedTotal);
        Assert.Equal(210m, tied.Arithmetic.LedgerSideTotal);
        Assert.Equal(0m, tied.Arithmetic.Difference);
        Assert.True(tied.Arithmetic.TiesToLedger);
        Assert.Equal(candidate.EntryNumber, tied.Statement.Lines.Single().MatchedEntryNumber);
        Assert.Contains(tied.Events, row => row.Kind == "matched" && row.NewEntryNumber == candidate.EntryNumber);
        Assert.Contains("Reconciled", BankingRules.ReconciliationSentence(tied), StringComparison.Ordinal);

        // Another business cannot reach this statement by guessing its identifier, and nothing is disclosed about it.
        var other = new BankingApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB));
        var foreignReview = await CaptureAsync(() => other.GetReviewAsync(detail.Import.Id));
        Assert.Equal(404, foreignReview.StatusCode);
        var foreignList = await CaptureAsync(() => other.ListStatementsAsync(account.Id));
        Assert.Equal(404, foreignList.StatusCode);

        // A role that may read reports but may not post is refused the reconciliation, and the statement is as it was.
        var viewer = new BankingApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer));
        var refused = await CaptureAsync(() => viewer.MatchLineAsync(
            line.Id, new MatchStatementLineRequest(candidate.JournalLineId, tied.Statement.Lines.Single().ExpectedVersion)));
        Assert.True(refused.StatusCode is 401 or 403, $"the reconciliation command answered {refused.StatusCode}, expected a refusal");
        var unchanged = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Equal(210m, unchanged.Arithmetic.MatchedTotal);
        Assert.Single(unchanged.Events, row => row.Kind == "matched");
    }

    private static BankStatementLineDto Line(decimal amount)
        => new(Guid.CreateVersion7(), 1, new DateOnly(2026, 3, 31), "Bank charge", "FEE-1", amount, null, null, null, 1);

    private static BankStatementReviewDto Review(
        int scale, decimal matched, decimal ledgerSide, decimal unmatched, int unmatchedCount, bool addsUp)
    {
        var arithmetic = new BankStatementArithmeticDto(
            1_000m, matched + unmatched, addsUp ? 1_110m : 1_200m, 1_110m, matched, unmatched, unmatchedCount,
            ledgerSide, matched + unmatched, 1_110m, matched - ledgerSide, addsUp,
            unmatchedCount == 0 && matched == ledgerSide && addsUp, scale, "Asia/Qatar");
        return new BankStatementReviewDto(
            new BankAccountDto(Guid.CreateVersion7(), "Operating", "****1234", null, "QAR", Guid.CreateVersion7(), "1010", "Bank", true),
            new BankStatementDetailDto(BankStatements.Import(), [Line(-15m)]),
            arithmetic,
            [new BankStatementLineReviewDto(Guid.CreateVersion7(), 1, [])],
            [],
            "October 2026",
            false);
    }

    private static async Task PostSettlementAsync(
        HttpClient http, DateOnly valueDate, Guid bankLedgerId, Guid revenueId, decimal amount)
    {
        var response = await http.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = valueDate,
            memo = "card settlement for the screen",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = bankLedgerId, debit = amount, credit = 0m, description = "bank", locationId = (Guid?)null },
                new { accountId = revenueId, debit = 0m, credit = amount, description = "sales", locationId = (Guid?)null },
            },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the settlement could not be posted: {(int)response.StatusCode} {body}");
    }

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

/// <summary>The statement header a screen test needs, named the way an accountant would name it.</summary>
internal static class BankStatements
{
    public static BankStatementImportDto Import() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "statement.csv", new DateOnly(2026, 3, 31),
        1_000m, 1_110m, 2, new DateTimeOffset(new DateTime(2026, 3, 31, 9, 0, 0, DateTimeKind.Utc)),
        BankReconciliationState.Open, 3, null, null, "Amina", "b7c4");
}
