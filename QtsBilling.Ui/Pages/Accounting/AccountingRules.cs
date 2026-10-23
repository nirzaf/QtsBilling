using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;

namespace QtsBilling.Ui.Pages.Accounting;

/// <summary>
/// What the accounting screens are allowed to say and to offer (specification task 32). The figures on those screens
/// are the server's: this type reads them, names them at the business's own settlement scale, states the rules an
/// accountant is bound by, and turns a refusal into the next act rather than a status code. It never computes a
/// balance a ledger should own and never decides whether a period is open - the posting path does (D05, R05).
/// </summary>
public static class AccountingRules
{
    /// <summary>Command keys the server validates; an act word, an anchor, and a short suffix.</summary>
    public const int MaxKeyLength = 128;

    public const int MaxReasonLength = 500;

    /// <summary>The mapping a posting uses is the mapping this screen shows - never a second answer per surface.</summary>
    public static readonly string ChartRule =
        "A control account is chosen from this business's own chart, by name, and the server insists on its kind and tenant. An account with posted activity is never deleted: it is taken out of service, and its history stays readable.";

    public static readonly string PostingRule =
        "A journal is balanced to the stored scale or nothing is written, the period the effective date falls in is the server's decision, and an unposted or closed window is refused rather than backdated.";

    public static readonly string ReversalRule =
        "A posted entry is never edited or deleted. A reversal is a new entry that names the original, is recorded on a date in an open period, and carries the reason an auditor will read.";

    public static readonly string CloseRule =
        "Closing a period shows what it leaves behind: the balances it carries forward, and the unfinished acts - a draft journal, an unconfirmed tender, a till still holding cash - that must be answered first. A confirmation on screen cannot bypass a server gate.";

    public static readonly string FiscalCloseRule =
        "A fiscal close transfers the year's revenue and expense result into retained earnings and locks the year's periods. It is an accounting act on one year, not an application shutdown, and it cannot be undone by editing rows: a wrong close is answered by a new audited entry.";

    public static readonly string StatementRule =
        "A trial balance and a general ledger are financial statements derived from posted lines only. A cash summary answers a different question - what came in and went out - and is never shown as either of them.";

    /// <summary>The acts a period in this state offers. A closed or locked window is not reopened by a screen.</summary>
    public static IReadOnlyList<PeriodAction> AllowedPeriodActions(AccountingPeriodState status)
        => status switch
        {
            AccountingPeriodState.Open => [PeriodAction.Close],
            AccountingPeriodState.Closed => [],
            AccountingPeriodState.Locked => [],
            _ => [],
        };

    public enum PeriodAction
    {
        Close,
    }

    public static bool CanPost(bool mayPost, bool hasOpenPeriod, int lineCount)
        => mayPost && hasOpenPeriod && lineCount >= 2;

    /// <summary>Only an entry with two lines and equal totals can be offered for posting; the server decides again.</summary>
    public static bool LooksBalanced(IReadOnlyList<(decimal Debit, decimal Credit)> lines)
        => lines.Count >= 2
            && lines.Sum(line => line.Debit) == lines.Sum(line => line.Credit)
            && lines.Sum(line => line.Debit) > 0m;

    public static string StatusText(AccountingPeriodState status) => status switch
    {
        AccountingPeriodState.Open => "open",
        AccountingPeriodState.Closed => "closed",
        AccountingPeriodState.Locked => "locked by the year close",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string EntryStateText(JournalEntryState status)
        => status == JournalEntryState.Posted ? "posted" : "not posted";

    /// <summary>Which side of the ledger an account kind normally grows on, said so a chooser can explain itself.</summary>
    public static string NormalSide(AccountKind kind) => kind switch
    {
        AccountKind.Asset or AccountKind.Expense => "debit",
        AccountKind.Liability or AccountKind.Equity or AccountKind.Revenue => "credit",
        _ => "either side",
    };

    public static bool CanHoldMapping(AccountKind actual, AccountKind expected) => actual == expected;

    /// <summary>One outstanding item, in the words the close screen shows: a balance or an unfinished act.</summary>
    public static string OutstandingSentence(PeriodOutstandingDto item, int scale)
    {
        var amount = Money(item.Amount, scale);
        var quantity = item.Count == 1 ? "one" : item.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var money = item.Kind is "open-sessions" or "coverage" or "retained-earnings" ? string.Empty : $" {amount}";
        return item.BlocksClose
            ? $"{item.Title}: {quantity}{money} still open. {item.WhatItMeans}"
            : $"{item.Title}: {amount} carried across the boundary ({quantity} item(s)). {item.WhatItMeans}";
    }

    /// <summary>
    /// What must be answered before a close can be offered at all. An item only counts as unanswered when it really
    /// holds something - a draft, a tender, a session - because a period with nothing unfinished in it is closeable,
    /// and a list of zeroes must not hold a close shut forever.
    /// </summary>
    public static IReadOnlyList<PeriodOutstandingDto> Blocking(PeriodCloseEvidenceDto? evidence)
        => evidence is null ? [] : evidence.Outstanding.Where(item => item.BlocksClose && Unanswered(item)).ToList();

    private static bool Unanswered(PeriodOutstandingDto item) => item.Count > 0 || item.Amount > 0m;

    public static bool CanClose(PeriodCloseEvidenceDto? evidence, bool mayClose)
        => mayClose
            && evidence is { PeriodOpen: true }
            && Blocking(evidence).Count == 0;

    public static string CloseBlocker(PeriodCloseEvidenceDto? evidence)
    {
        if (evidence is null)
        {
            return "Read the period before deciding anything.";
        }

        if (!evidence.PeriodOpen)
        {
            return $"'{evidence.Period.Name}' is already {StatusText(evidence.Period.Status)}; a period is not reopened by this screen.";
        }

        var blockers = Blocking(evidence);
        return blockers.Count == 0
            ? string.Empty
            : string.Join(' ', blockers.Select(item => OutstandingSentence(item, evidence.SettlementScale)));
    }

    /// <summary>The year-end sentence: what a close would move, and what it refuses to do.</summary>
    public static string YearSentence(FiscalYearEvidenceDto? evidence)
    {
        if (evidence is null)
        {
            return "Choose the year to read its close.";
        }

        if (evidence.AlreadyClosed)
        {
            return $"The {evidence.FiscalYear} close is already on the record with {evidence.ClosingEntries.Count} closing entries. A second transfer would move the same result twice.";
        }

        if (!evidence.CoversWholeYear)
        {
            return evidence.GapFrom is { Length: > 0 } gap
                ? $"The periods for {evidence.FiscalYear} do not cover the whole year; nothing is closed before {gap} is covered."
                : $"{evidence.FiscalYear} has no accounting periods yet, so there is nothing to close.";
        }

        if (!evidence.RetainedEarningsReady)
        {
            return "The retained earnings account is not configured as an active equity account, so the year's result has nowhere to go.";
        }

        return evidence.OpenPeriodCount > 0
            ? $"{evidence.OpenPeriodCount} of {evidence.PeriodCount} periods are still open. Each is closed on its own evidence first; the year transfers nothing until then."
            : $"Closing {evidence.FiscalYear} transfers a net result of {Money(evidence.NetIncome, evidence.SettlementScale)} into {(evidence.RetainedEarningsCode is { Length: > 0 } code ? code : "retained earnings")} and locks the year's periods.";
    }

    public static bool CanCloseYear(FiscalYearEvidenceDto? evidence, bool mayClose)
        => mayClose
            && evidence is { AlreadyClosed: false, CoversWholeYear: true, RetainedEarningsReady: true, OpenPeriodCount: 0 };

    /// <summary>
    /// The document a journal entry came from, where this product has a screen for it. Drill-through is offered only
    /// where the source identifier is actually the key of a page that exists - a purchase or settlement entry names
    /// its origin without a link rather than sending an operator to a route that cannot read it.
    /// </summary>
    public static (string Label, string Href)? SourceDocumentLink(JournalOrigin origin, Guid? sourceId) => sourceId switch
    {
        null => null,
        { } id when origin == JournalOrigin.Invoice => ("The invoice it posted", $"invoices/{id:D}"),
        { } id when origin == JournalOrigin.Sale => ("The sale it posted", $"sales/{id:D}"),
        { } id when origin == JournalOrigin.Expense => ("The expense it posted", $"expenses/{id:D}"),
        _ => null,
    };

    /// <summary>Money at the scale the business settles in - never at a scale this screen chose.</summary>
    public static string Money(decimal amount, int scale)
        => amount.ToString(
            "N" + Math.Clamp(scale, 0, 4).ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);

    public static string MoneyText(decimal amount, int scale, string currency)
        => $"{Money(amount, scale)} {currency}";

    /// <summary>Only codes that prove nothing was written release a held intention.</summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.OptimisticConcurrencyFailure
        or ApiErrorCodes.AccountingPeriodClosed
        or ApiErrorCodes.AccountingPeriodMissing
        or ApiErrorCodes.AccountingPeriodOverlap
        or ApiErrorCodes.UnbalancedJournal
        or ApiErrorCodes.Conflict
        or ApiErrorCodes.NotFound;

    public static string NewKey(string act, Guid anchor)
    {
        var key = $"{act}-{anchor:N}";
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    public static string NewYearKey(string act, int fiscalYear)
    {
        var key = $"{act}-{fiscalYear.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    /// <summary>A refusal names the rule and the next act, not a status code.</summary>
    public static string RefusalSentence(ApiException exception, string what) => exception.Code switch
    {
        ApiErrorCodes.AccountingPeriodClosed =>
            $"'{what}' was refused because the accounting period that date falls in is closed. Post or reverse inside an open period - the ledger will not accept a silent backdate.",
        ApiErrorCodes.AccountingPeriodMissing =>
            $"'{what}' found no accounting period covering that date: {exception.Message}",
        ApiErrorCodes.AccountingPeriodOverlap =>
            $"'{what}' overlaps a period that already exists. Windows are contiguous and never overlap: {exception.Message}",
        ApiErrorCodes.UnbalancedJournal =>
            $"'{what}' is not balanced, so nothing was written: {exception.Message}",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            $"'{what}' met a record that had already moved. Read it back before the same intention is repeated.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            $"'{what}' was sent before under this key with different figures. Nothing new was written; send the exact act again or abandon it.",
        ApiErrorCodes.IdempotencyKeyReused =>
            $"'{what}' cannot reuse a key that already recorded an act. A posted entry is reversed at most once.",
        ApiErrorCodes.Conflict =>
            $"'{what}' was refused: {exception.Message}",
        ApiErrorCodes.ValidationFailed =>
            $"'{what}' was not accepted as it stands: {exception.Message}",
        ApiErrorCodes.NotFound =>
            $"'{what}' named a record this business does not hold, so nothing was changed against it.",
        _ => $"'{what}' could not be confirmed: {exception.Message}. Nothing was assumed about the outcome.",
    };
}
