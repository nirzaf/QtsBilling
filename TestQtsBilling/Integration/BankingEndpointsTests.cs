using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Banking;
using QtsBilling.Persistence.Postgres;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Bank-account registration, statement import, and exact posted-line reconciliation, then the reconciliation an
/// accountant actually finishes (specification task 31, T31.01 - T31.03): a statement whose figures and ledger side
/// tie and that closes only when every line is answered, duplicates and double claims refused, and a corrected match
/// that keeps the evidence of the match it replaced while leaving the historic journal untouched.
/// </summary>
public sealed class BankingEndpointsTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;
    private static int _nextLedgerCode;

    public BankingEndpointsTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Statement_import_is_idempotent_and_only_exact_posted_bank_lines_can_be_matched()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var bankLedgerId = await LedgerAccountIdAsync("1010");
        var revenueLedgerId = await LedgerAccountIdAsync("4000");
        var accountRequest = new CreateBankAccountRequest(
            "Operating account",
            "****1234",
            "QA5888888888888888888888",
            "QAR",
            bankLedgerId);

        var createAccount = await client.PostAsJsonAsync("/api/v1/banking/accounts", accountRequest);
        var createAccountBody = await createAccount.Content.ReadAsStringAsync();
        Assert.True(createAccount.StatusCode == HttpStatusCode.Created,
            $"Bank account registration returned {(int)createAccount.StatusCode}: {createAccountBody}");
        var bankAccount = JsonSerializer.Deserialize<BankAccountDto>(createAccountBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal("••••8888", bankAccount.IbanMasked);
        Assert.DoesNotContain(accountRequest.Iban!, createAccountBody, StringComparison.Ordinal);

        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Card settlement, daily\",SETTLE-1,125.0000\n";
        var import = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-1");
        var importBody = await import.Content.ReadAsStringAsync();
        Assert.True(import.StatusCode == HttpStatusCode.Created,
            $"Statement import returned {(int)import.StatusCode}: {importBody}");
        var detail = JsonSerializer.Deserialize<BankStatementDetailDto>(importBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal(1, detail.Import.LineCount);
        Assert.Equal("Card settlement, daily", detail.Lines.Single().Description);
        Assert.Equal("SETTLE-1", detail.Lines.Single().Reference);
        Assert.Null(detail.Lines.Single().MatchedJournalLineId);

        var replay = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayedDetail = await replay.Content.ReadFromJsonAsync<BankStatementDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(detail.Import.Id, replayedDetail!.Import.Id);

        var alteredReplay = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_001m, 1_125m, "bank-import-1");
        Assert.Equal(HttpStatusCode.BadRequest, alteredReplay.StatusCode);
        Assert.Contains("bank-statement balances do not reconcile", await alteredReplay.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var duplicateFile = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-2");
        Assert.Equal(HttpStatusCode.Conflict, duplicateFile.StatusCode);

        var journal = await client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = valueDate,
            memo = "bank reconciliation test",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = bankLedgerId, debit = 125m, credit = 0m, description = "bank receipt", locationId = (Guid?)null },
                new { accountId = revenueLedgerId, debit = 0m, credit = 125m, description = "counter entry", locationId = (Guid?)null },
            },
        });
        var journalBody = await journal.Content.ReadAsStringAsync();
        Assert.True(journal.StatusCode == HttpStatusCode.Created,
            $"Journal posting returned {(int)journal.StatusCode}: {journalBody}");
        using var journalJson = JsonDocument.Parse(journalBody);
        var journalId = journalJson.RootElement.GetProperty("id").GetGuid();
        var journalLineIds = await _fixture.WithFreshContextAsync(async db => await db.JournalLines.AsNoTracking()
            .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.JournalEntryId == journalId)
            .Select(line => new { line.Id, line.AccountId })
            .ToListAsync());

        var wrongAccountLine = journalLineIds.Single(line => line.AccountId == revenueLedgerId);
        var wrongMatch = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(wrongAccountLine.Id));
        Assert.Equal(HttpStatusCode.Conflict, wrongMatch.StatusCode);

        var bankLine = journalLineIds.Single(line => line.AccountId == bankLedgerId);
        var match = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(bankLine.Id));
        var matched = await match.Content.ReadFromJsonAsync<BankStatementLineDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Equal(bankLine.Id, matched!.MatchedJournalLineId);
        Assert.NotNull(matched.ReconciledAtUtc);

        var matchReplay = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(bankLine.Id));
        Assert.Equal(HttpStatusCode.OK, matchReplay.StatusCode);
        var differentMatch = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(wrongAccountLine.Id));
        Assert.Equal(HttpStatusCode.Conflict, differentMatch.StatusCode);

        var otherBusinessClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var crossTenantRead = await otherBusinessClient.GetAsync($"/api/v1/banking/statements/{detail.Import.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantRead.StatusCode);

        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_lines SET description = 'altered' WHERE id = {detail.Lines[0].Id}")));
        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_imports SET opening_balance = 0 WHERE id = {detail.Import.Id}")));
    }

    [Fact]
    public async Task T31_01_a_statement_closes_only_when_every_line_is_answered_and_the_ledger_side_ties()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var banking = new BankingApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var bankLedgerId = await CreateBankLedgerAsync(http);
        var revenueLedgerId = await LedgerAccountIdAsync("4000");
        var expenseLedgerId = await LedgerAccountIdAsync("5000");

        var account = await banking.CreateAccountAsync(new CreateBankAccountRequest(
            $"Reconciliation account {suffix}", "****4321", null, "QAR", bankLedgerId));

        // The bank shows a settlement in and a charge out, and the statement's own figures add up.
        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Card settlement\",SET-{suffix},125.00\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Bank charge\",FEE-{suffix},-15.00\n";
        var detail = await banking.ImportStatementAsync(
            account.Id, $"statement-{suffix}.csv", valueDate, 1_000m, 1_110m,
            Encoding.UTF8.GetBytes(csv), $"t31-01-import-{suffix}");
        var importId = detail.Import.Id;
        var lines = detail.Lines.OrderBy(row => row.LineNo).ToArray();
        Assert.Equal(2, lines.Length);

        // Both movements are booked in the ledger first: a settlement received and a charge taken.
        var receipt = await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, revenueLedgerId, 125m, debitBank: true);
        var charge = await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, expenseLedgerId, 15m, debitBank: false);

        var review = await banking.GetReviewAsync(importId);
        Assert.Equal($"Reconciliation account {suffix}", review.Account.Name);
        Assert.Equal(BankReconciliationState.Open, review.Statement.Import.Status);
        Assert.Equal(110m, review.Arithmetic.MovementTotal);
        Assert.Equal(1_110m, review.Arithmetic.CalculatedClosing);
        Assert.Equal(1_110m, review.Arithmetic.StatedClosing);
        Assert.True(review.Arithmetic.StatementAddsUp, "the bank's own figures add up, so that is not the exception here");
        Assert.Equal(2, review.Arithmetic.UnmatchedCount);
        Assert.Equal(110m, review.Arithmetic.UnmatchedTotal);
        Assert.Equal(0m, review.Arithmetic.MatchedTotal);
        Assert.False(review.Arithmetic.TiesToLedger, "nothing has been reconciled yet, so the statement cannot be closed");
        Assert.Equal(2, review.Arithmetic.SettlementScale);
        Assert.True(review.AccountingPeriodName is { Length: > 0 }, "the reviewer is told which period this statement falls in");

        // Each line is offered the posted movement it could be, named by its entry number rather than keyed.
        Assert.Contains(review.Lines.Single(row => row.LineNo == 1).Candidates, candidate => candidate.EntryNumber == receipt);
        Assert.Contains(review.Lines.Single(row => row.LineNo == 2).Candidates, candidate => candidate.EntryNumber == charge);

        // Finalising is refused while a line is unanswered, and the refusal names the lines - the exception is shown,
        // not written off, and the statement stays open.
        var refused = await Assert.ThrowsAsync<ApiException>(() => banking.FinaliseStatementAsync(
            importId, new FinaliseStatementRequest(review.Statement.Import.ExpectedVersion), $"t31-01-finalise-early-{suffix}"));
        Assert.Equal(ApiErrorCodes.BankStatementNotReconciled, refused.Code);
        Assert.Contains("unmatched", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BankReconciliationState.Open, (await banking.GetStatementAsync(importId)).Import.Status);

        // Both lines reconciled: the matched total and the ledger side of the statement are the same money.
        var first = await banking.MatchLineAsync(lines[0].Id, new MatchStatementLineRequest(
            review.Lines.Single(row => row.LineNo == 1).Candidates.Single(candidate => candidate.EntryNumber == receipt).JournalLineId,
            lines[0].ExpectedVersion));
        var refreshed = await banking.GetReviewAsync(importId);
        var second = refreshed.Lines.Single(row => row.LineNo == 2).Candidates.Single(candidate => candidate.EntryNumber == charge);
        await banking.MatchLineAsync(lines[1].Id, new MatchStatementLineRequest(
            second.JournalLineId, refreshed.Statement.Lines.Single(row => row.LineNo == 2).ExpectedVersion));

        var tied = await banking.GetReviewAsync(importId);
        Assert.Equal(110m, tied.Arithmetic.MatchedTotal);
        Assert.Equal(110m, tied.Arithmetic.LedgerSideTotal);
        Assert.Equal(0m, tied.Arithmetic.Difference);
        Assert.Equal(0, tied.Arithmetic.UnmatchedCount);
        Assert.True(tied.Arithmetic.TiesToLedger);
        Assert.Equal(first.MatchedEntryNumber, tied.Statement.Lines.Single(row => row.LineNo == 1).MatchedEntryNumber);

        // It closes, and the close is recorded as an act by a named person at a moment.
        var finalised = await banking.FinaliseStatementAsync(
            importId, new FinaliseStatementRequest(tied.Statement.Import.ExpectedVersion), $"t31-01-finalise-{suffix}");
        Assert.Equal(BankReconciliationState.Finalised, finalised.Import.Status);
        Assert.NotNull(finalised.Import.FinalisedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(finalised.Import.FinalisedByUserName));
        var closedReview = await banking.GetReviewAsync(importId);
        Assert.Contains(closedReview.Events, row => row.Kind == "finalised");

        // A second close is refused: the review state is a decision, not a counter.
        Assert.Equal(ApiErrorCodes.Conflict, (await Assert.ThrowsAsync<ApiException>(() => banking.FinaliseStatementAsync(
            importId, new FinaliseStatementRequest(finalised.Import.ExpectedVersion), $"t31-01-finalise-again-{suffix}"))).Code);
        Assert.Equal(BankReconciliationState.Finalised, (await banking.GetStatementAsync(importId)).Import.Status);

        // Reopening needs the closing authority and a reason; it reopens the review, not the accounting period.
        var periodBefore = await PeriodStateAsync(importId);
        var reopened = await banking.ReopenStatementAsync(
            importId, new ReopenStatementRequest(finalised.Import.ExpectedVersion, $"the charge was queried by head office {suffix}"),
            $"t31-01-reopen-{suffix}");
        Assert.Equal(BankReconciliationState.Open, reopened.Import.Status);
        Assert.Null(reopened.Import.FinalisedAtUtc);
        Assert.Null(reopened.Import.FinalisedByUserName);
        Assert.Equal(periodBefore, await PeriodStateAsync(importId));

        var history = await banking.GetReviewAsync(importId);
        Assert.Contains(history.Events, row => row.Kind == "reopened" && row.Reason!.Contains(suffix, StringComparison.Ordinal));
        Assert.Equal(2, history.Events.Count(row => row.Kind == "matched"));

        // A close read against the version this review has already moved past is refused as stale, and nothing
        // closes again: two accountants cannot both finalise one statement by working from different readings.
        var stale = await Assert.ThrowsAsync<ApiException>(() => banking.FinaliseStatementAsync(
            importId, new FinaliseStatementRequest(finalised.Import.ExpectedVersion), $"t31-01-finalise-stale-{suffix}"));
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);
        Assert.Equal(BankReconciliationState.Open, (await banking.GetStatementAsync(importId)).Import.Status);

        // The current reading closes it, and the version the screen read is the version that was honoured.
        var closed = await banking.FinaliseStatementAsync(
            importId, new FinaliseStatementRequest(history.Statement.Import.ExpectedVersion), $"t31-01-finalise-twice-{suffix}");
        Assert.Equal(BankReconciliationState.Finalised, closed.Import.Status);
    }

    [Fact]
    public async Task T31_02_a_duplicate_file_a_double_claim_and_an_amount_mismatch_are_refused_without_writing_anything()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var banking = new BankingApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var bankLedgerId = await CreateBankLedgerAsync(http);
        var revenueLedgerId = await LedgerAccountIdAsync("4000");

        var account = await banking.CreateAccountAsync(new CreateBankAccountRequest(
            $"Duplicate claim {suffix}", "****5678", null, "QAR", bankLedgerId));
        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Settlement one\",ONE-{suffix},125.00\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Settlement two\",TWO-{suffix},125.00\n";
        var detail = await banking.ImportStatementAsync(
            account.Id, "twice.csv", valueDate, 0m, 250m, Encoding.UTF8.GetBytes(csv), $"t31-02-import-{suffix}");

        // The same file again under a fresh key is still the same file: refused, and no second import exists.
        var duplicate = await Assert.ThrowsAsync<ApiException>(() => banking.ImportStatementAsync(
            account.Id, "twice.csv", valueDate, 0m, 250m, Encoding.UTF8.GetBytes(csv), $"t31-02-import-other-{suffix}"));
        Assert.Equal(ApiErrorCodes.IdempotencyKeyReused, duplicate.Code);
        Assert.Equal(1, (await banking.ListStatementsAsync(account.Id)).Count(row => row.BankAccountId == account.Id));

        // One posted movement, two statement lines of the same amount: the second claim is refused.
        var entryNumber = await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, revenueLedgerId, 125m, debitBank: true);
        var review = await banking.GetReviewAsync(detail.Import.Id);
        var lines = review.Statement.Lines.OrderBy(row => row.LineNo).ToArray();
        var claimable = review.Lines.Single(row => row.LineNo == 1).Candidates.Single(candidate => candidate.EntryNumber == entryNumber);
        await banking.MatchLineAsync(lines[0].Id, new MatchStatementLineRequest(claimable.JournalLineId, lines[0].ExpectedVersion));

        var afterFirst = await banking.GetReviewAsync(detail.Import.Id);
        var doubleClaim = await Assert.ThrowsAsync<ApiException>(() => banking.MatchLineAsync(
            lines[1].Id, new MatchStatementLineRequest(claimable.JournalLineId,
                afterFirst.Statement.Lines.Single(row => row.LineNo == 2).ExpectedVersion)));
        Assert.Equal(ApiErrorCodes.Conflict, doubleClaim.Code);
        Assert.Contains("has not claimed", doubleClaim.Message, StringComparison.OrdinalIgnoreCase);

        var stillOpen = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Null(stillOpen.Statement.Lines.Single(row => row.LineNo == 2).MatchedJournalLineId);
        Assert.Equal(1, stillOpen.Arithmetic.UnmatchedCount);
        Assert.Single(stillOpen.Events, row => row.Kind == "matched");

        // The database is the last word: two rows naming one posted line are refused by the unique index itself,
        // even by direct SQL that skips every service rule. The SQLSTATE is what distinguishes that index from the
        // evidence guard, so the assertion names it rather than accepting any refusal.
        var actor = (await CaptureActorAsync()).UserId;
        var direct = await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_lines SET matched_journal_line_id = {claimable.JournalLineId}, reconciled_at_utc = now(), reconciled_by_user_id = {actor} WHERE id = {lines[1].Id}")
                .ConfigureAwait(false)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, direct.SqlState);

        // An amount the statement does not carry is not a match, and a correction without a reason is not an act.
        // A posted bank movement of a different figure exists now, so the refusal is about the amount and not about
        // the claim - the two rules are proved apart rather than collapsed into one conflict.
        await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, revenueLedgerId, 999m, debitBank: true, memo: $"unrelated transfer {suffix}");
        var unread = (await banking.GetStatementAsync(detail.Import.Id)).Lines.Single(row => row.LineNo == 2);
        var wrongBankLine = await BankLineOfAmountAsync(bankLedgerId, 999m);
        var wrongAmount = await Assert.ThrowsAsync<ApiException>(() => banking.MatchLineAsync(
            lines[1].Id, new MatchStatementLineRequest(wrongBankLine, unread.ExpectedVersion)));
        Assert.Equal(ApiErrorCodes.Conflict, wrongAmount.Code);
        Assert.Contains("same value date", wrongAmount.Message, StringComparison.OrdinalIgnoreCase);

        var noReason = await Assert.ThrowsAsync<ApiException>(() => banking.UnmatchLineAsync(
            lines[0].Id,
            new UnmatchStatementLineRequest(
                stillOpen.Statement.Lines.Single(row => row.LineNo == 1).ExpectedVersion, claimable.JournalLineId, "   "),
            $"t31-02-unmatch-noreason-{suffix}"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, noReason.Code);
        Assert.Contains("reason", noReason.Errors.Keys, StringComparer.Ordinal);
        Assert.NotNull((await banking.GetStatementAsync(detail.Import.Id)).Lines.Single(row => row.LineNo == 1).MatchedJournalLineId);
    }

    [Fact]
    public async Task T31_03_a_corrected_match_keeps_its_evidence_and_leaves_the_historic_journal_untouched()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var banking = new BankingApiClient(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var bankLedgerId = await CreateBankLedgerAsync(http);
        var revenueLedgerId = await LedgerAccountIdAsync("4000");

        var account = await banking.CreateAccountAsync(new CreateBankAccountRequest(
            $"Correction {suffix}", "****9021", null, "QAR", bankLedgerId));
        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Wire in\",WIRE-{suffix},300.00\n";
        var detail = await banking.ImportStatementAsync(
            account.Id, "correct.csv", valueDate, 0m, 300m, Encoding.UTF8.GetBytes(csv), $"t31-03-import-{suffix}");
        var line = Assert.Single(detail.Lines);

        // Two posted movements of the same amount on the statement's own value date: the reviewer picks the wrong
        // one, then corrects it. A movement dated on another day is not a match for this line at all, so the value
        // date is the authority - which is why both entries carry it.
        var firstEntry = await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, revenueLedgerId, 300m, debitBank: true, memo: $"wrong choice {suffix}");
        var secondEntry = await PostJournalAsync(http, valueDate, "QAR", bankLedgerId, revenueLedgerId, 300m, debitBank: true, memo: $"right choice {suffix}");
        var review = await banking.GetReviewAsync(detail.Import.Id);
        var wrong = review.Lines.Single(row => row.LineNo == 1).Candidates.Single(candidate => candidate.EntryNumber == firstEntry);
        await banking.MatchLineAsync(line.Id, new MatchStatementLineRequest(wrong.JournalLineId, line.ExpectedVersion));

        var journalBefore = await JournalSnapshotAsync();
        var matched = await banking.GetReviewAsync(detail.Import.Id);
        var matchedLine = matched.Statement.Lines.Single(row => row.LineNo == 1);
        Assert.Equal(firstEntry, matchedLine.MatchedEntryNumber);

        // The correction names the match it is walking away from and gives the reason. It changes the review, not
        // the ledger: the entry it releases stays posted, balanced and with the same lines it had.
        var corrected = await banking.UnmatchLineAsync(line.Id,
            new UnmatchStatementLineRequest(matchedLine.ExpectedVersion, wrong.JournalLineId, $"the wire was the one on the {valueDate.AddDays(1):yyyy-MM-dd} value date {suffix}"),
            $"t31-03-unmatch-{suffix}");
        Assert.Null(corrected.MatchedJournalLineId);
        Assert.Equal(journalBefore, await JournalSnapshotAsync());

        var afterCorrect = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Equal(2, afterCorrect.Events.Count);
        Assert.Contains(afterCorrect.Events, row => row.Kind == "matched" && row.NewEntryNumber == firstEntry);
        var evidence = Assert.Single(afterCorrect.Events, row => row.Kind == "unmatched");
        Assert.Equal(firstEntry, evidence.PriorEntryNumber);
        Assert.Contains(suffix, evidence.Reason, StringComparison.Ordinal);
        Assert.NotNull(afterCorrect.Events.FirstOrDefault(row => row.Kind == "matched"));

        // Reconciled to the right movement afterwards, and both decisions are still on the record.
        var right = afterCorrect.Lines.Single(row => row.LineNo == 1).Candidates.Single(candidate => candidate.EntryNumber == secondEntry);
        await banking.MatchLineAsync(line.Id, new MatchStatementLineRequest(right.JournalLineId, corrected.ExpectedVersion));
        var settled = await banking.GetReviewAsync(detail.Import.Id);
        Assert.Equal(secondEntry, settled.Statement.Lines.Single(row => row.LineNo == 1).MatchedEntryNumber);
        Assert.Equal(3, settled.Events.Count);
        Assert.Equal(journalBefore, await JournalSnapshotAsync());

        // A finalised statement refuses the correction until it is reopened, and its evidence rows are append-only.
        await banking.FinaliseStatementAsync(detail.Import.Id,
            new FinaliseStatementRequest(settled.Statement.Import.ExpectedVersion), $"t31-03-finalise-{suffix}");
        var refused = await Assert.ThrowsAsync<ApiException>(() => banking.UnmatchLineAsync(line.Id,
            new UnmatchStatementLineRequest(settled.Statement.Lines.Single(row => row.LineNo == 1).ExpectedVersion, right.JournalLineId, "changed my mind"),
            $"t31-03-unmatch-after-finalise-{suffix}"));
        Assert.Equal(ApiErrorCodes.BankStatementFinalised, refused.Code);
        Assert.Contains("reopen", refused.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_reconciliation_events SET reason = 'rewritten' WHERE statement_import_id = {detail.Import.Id}")
                .ConfigureAwait(false)));
        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bank_statement_reconciliation_events WHERE statement_import_id = {detail.Import.Id}")
                .ConfigureAwait(false)));
    }

    /// <summary>The owner's own user identifier, read from the fixture database for a guard-proving write.</summary>
    private async Task<(Guid UserId, string DisplayName)> CaptureActorAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var user = await db.Users.AsNoTracking().FirstAsync(row => row.DisplayName.Length > 0);
            return (user.Id, user.DisplayName);
        });

    /// <summary>Posts a two-line journal on the bank account and answers with its entry number.</summary>
    private async Task<string> PostJournalAsync(
        HttpClient http, DateOnly entryDate, string currency, Guid bankLedgerId, Guid otherLedgerId, decimal amount, bool debitBank, string? memo = null)
    {
        var bankSide = debitBank ? amount : 0m;
        var otherSide = debitBank ? 0m : amount;
        var response = await http.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate,
            memo = memo ?? $"bank movement {Guid.NewGuid():N}"[..32],
            currency,
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = bankLedgerId, debit = bankSide, credit = amount - bankSide, description = "bank leg", locationId = (Guid?)null },
                new { accountId = otherLedgerId, debit = otherSide, credit = amount - otherSide, description = "counter leg", locationId = (Guid?)null },
            },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the journal could not be posted: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("entryNumber").GetString()!;
    }

    private async Task<Guid> BankLineOfAmountAsync(Guid bankLedgerId, decimal amount)
        => await _fixture.WithFreshContextAsync(async db => await db.JournalLines.AsNoTracking()
            .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == bankLedgerId
                && line.DebitAmount - line.CreditAmount == amount)
            .Select(line => line.Id)
            .FirstAsync());

    /// <summary>The period the statement falls in, so a reopening can be shown not to have touched it.</summary>
    private async Task<string> PeriodStateAsync(Guid importId)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var statementDate = await db.BankStatementImports.AsNoTracking()
                .Where(row => row.Id == importId).Select(row => row.StatementDate).SingleAsync();
            var period = await db.AccountingPeriods.AsNoTracking()
                .SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                    && row.StartDate <= statementDate && row.EndDate >= statementDate);
            return $"{period.Name}:{period.Status}";
        });

    /// <summary>
    /// Every posted journal in the business, as one sorted string: a number, its totals, its state and the moment it
    /// was posted. A correction that touched a historic entry would change this snapshot. Read inside its own fresh
    /// context so a projection that returns nothing cannot look like an unchanged ledger.
    /// </summary>
    private async Task<string> JournalSnapshotAsync()
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var rows = await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.BusinessId == _fixture.BusinessA.BusinessId)
                .OrderBy(entry => entry.EntryNumber)
                .Select(entry => new { entry.EntryNumber, entry.TotalDebit, entry.TotalCredit, entry.Status, entry.PostedAtUtc })
                .ToListAsync();
            Assert.NotEmpty(rows);
            return string.Join('|', rows.Select(row =>
                $"{row.EntryNumber}#{row.TotalDebit}/{row.TotalCredit}#{row.Status}#{row.PostedAtUtc:O}"));
        });

    /// <summary>
    /// A bank account is registered against an asset account of its own - the chart allows one bank source per
    /// ledger account - so each fact registers the account it uses instead of contending for the fixture's shared
    /// 1010, which the import fact already owns.
    /// </summary>
    private async Task<Guid> CreateBankLedgerAsync(HttpClient http)
    {
        var code = (1900 + Interlocked.Increment(ref _nextLedgerCode)).ToString(CultureInfo.InvariantCulture);
        var response = await http.PostAsJsonAsync("/api/v1/accounts", new SaveAccountRequest(code, $"Bank account {code}", AccountKind.Asset, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the bank ledger account could not be registered: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> LedgerAccountIdAsync(string code)
        => await _fixture.WithFreshContextAsync(async db => await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == code)
            .Select(account => account.Id)
            .SingleAsync());

    private static async Task<HttpResponseMessage> ImportAsync(
        HttpClient client,
        Guid bankAccountId,
        string csv,
        DateOnly statementDate,
        decimal openingBalance,
        decimal closingBalance,
        string idempotencyKey)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(statementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "statementDate");
        form.Add(new StringContent(openingBalance.ToString(CultureInfo.InvariantCulture)), "openingBalance");
        form.Add(new StringContent(closingBalance.ToString(CultureInfo.InvariantCulture)), "closingBalance");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "statement.csv");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/banking/accounts/{bankAccountId:D}/statements")
        {
            Content = form,
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }
}
