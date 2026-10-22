using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Banking;

namespace QtsBilling.Ui.Pages.Finance;

/// <summary>
/// What the bank-reconciliation screen is allowed to say and to do (specification task 31). The figures themselves
/// are the server's: this type reads them, names them at the settlement scale the statement was composed in, and
/// states the rules a reviewer is bound by - a reference string is not proof of settlement, a correction keeps the
/// match it replaces, a fee is a journal rather than a write-off, and an exception is shown instead of finalised
/// away (requirements R05, R07, R08).
/// </summary>
public static class BankingRules
{
    /// <summary>Keys the server validates: an act word, an anchor, and a short suffix.</summary>
    public const int MaxKeyLength = 64;

    public static readonly string MatchRule =
        "A statement line is reconciled to one posted journal line on this bank account - same date, same currency, same signed amount. A reference the bank typed is a hint, never proof.";

    public static readonly string CorrectionRule =
        "A mistaken match is corrected with a reason, and the match it replaces stays on the record. Nothing about the statement's own figures is rewritten.";

    public static readonly string MovementRule =
        "A bank fee or interest line the business has not booked is posted as a reviewed journal - the bank leg against this account, the other leg against an account you choose. A difference is never written off into a clearing account.";

    public static readonly string FinaliseRule =
        "A statement is finalised only when every line is answered and the booked bank account ties to what was reconciled. Unmatched lines stay visible as exceptions instead of becoming a balancing figure.";

    public static readonly string PeriodRule =
        "Reopening a reconciliation reopens the review, not the accounting period. A closed period is answered by new events in an open one.";

    /// <summary>The acts a statement in this state offers. A finalised review corrects nothing until it is reopened.</summary>
    public static IReadOnlyList<BankReconciliationAction> AllowedActions(BankReconciliationState status)
        => status == BankReconciliationState.Open
            ? [BankReconciliationAction.Match, BankReconciliationAction.RecordMovement, BankReconciliationAction.Finalise]
            : [BankReconciliationAction.Reopen];

    /// <summary>The kinds of act the screen can perform, named the way the operator names them.</summary>
    public enum BankReconciliationAction
    {
        Match,
        RecordMovement,
        Finalise,
        Reopen,
    }

    /// <summary>A negative movement is the bank taking money - a charge. A positive one is interest received.</summary>
    public static bool IsCharge(BankStatementLineDto line) => line.Amount < 0m;

    public static string MovementTitle(BankStatementLineDto line)
        => IsCharge(line) ? "Record the bank charge" : "Record the interest received";

    /// <summary>Which side of the ledger the counter leg must sit on, in the words the refusal uses too.</summary>
    public static string CounterAccountKind(BankStatementLineDto line)
        => IsCharge(line) ? "an expense account" : "a revenue account";

    public static bool CanFinalise(BankStatementReviewDto? review)
        => review is not null
            && review.Statement.Import.Status == BankReconciliationState.Open
            && review.Arithmetic.StatementAddsUp
            && review.Arithmetic.UnmatchedCount == 0
            && review.Arithmetic.TiesToLedger;

    /// <summary>What the reviewer must answer before finalising, in the server's own figures.</summary>
    public static string FinaliseBlocker(BankStatementReviewDto? review)
    {
        if (review is null)
        {
            return "Read the statement before deciding anything.";
        }

        if (!review.Arithmetic.StatementAddsUp)
        {
            return $"The bank's own figures do not add up: {Money(review.Arithmetic.CalculatedClosing, review)} against the {Money(review.Arithmetic.StatedClosing, review)} the statement printed.";
        }

        if (review.Arithmetic.UnmatchedCount > 0)
        {
            return $"{review.Arithmetic.UnmatchedCount} line(s) are still unmatched, worth {Money(review.Arithmetic.UnmatchedTotal, review)} in total. Match them, or record the fee or interest they are.";
        }

        if (!review.Arithmetic.TiesToLedger)
        {
            return $"The lines this statement is matched to carry {Money(review.Arithmetic.LedgerSideTotal, review)} in the ledger against {Money(review.Arithmetic.MatchedTotal, review)} on the statement - a difference of {Money(review.Arithmetic.Difference, review)} that has to be answered by a line.";
        }

        return string.Empty;
    }

    /// <summary>Whether the ledger and the documents agree, said as a fact rather than a colour.</summary>
    public static string ReconciliationSentence(BankStatementReviewDto? review)
        => review is null
            ? "Nothing read yet."
            : review.Arithmetic.TiesToLedger
                ? $"Reconciled: {Money(review.Arithmetic.MatchedTotal, review)} matched against {Money(review.Arithmetic.LedgerSideTotal, review)} in the ledger, every line answered, and the statement's own figures add up. The booked bank account moved {Money(review.Arithmetic.BookedBankMovement, review)} in the period and holds {Money(review.Arithmetic.BookedBankBalance, review)} at the statement date."
                : $"Not reconciled: {Money(review.Arithmetic.MatchedTotal, review)} matched against {Money(review.Arithmetic.LedgerSideTotal, review)} in the ledger, with {Money(review.Arithmetic.UnmatchedTotal, review)} across {review.Arithmetic.UnmatchedCount} line(s) still unanswered and a difference of {Money(review.Arithmetic.Difference, review)}.";

    /// <summary>The period the statement falls in, with the honest consequence for a correction.</summary>
    public static string PeriodSentence(BankStatementReviewDto review)
        => review.AccountingPeriodName is not { Length: > 0 } name
            ? "No accounting period covers this statement date, so nothing new can be posted against it."
            : review.AccountingPeriodClosed
                ? $"'{name}' is closed: the movements already booked stay as they are, and a correction is answered by new events in an open period."
                : $"'{name}' is open, so a reviewed fee or interest line can still be posted.";

    /// <summary>A statement named the way an operator recognises it - its date and its file, never its key.</summary>
    public static string StatementLabel(BankStatementImportDto import)
        => $"{import.StatementDate:yyyy-MM-dd} · {import.FileName} · {import.LineCount} line(s)";

    public static string StatusText(BankReconciliationState status)
        => status == BankReconciliationState.Finalised ? "finalised" : "open for correction";

    public static string KindText(string kind) => kind switch
    {
        "matched" => "matched to a posted journal line",
        "unmatched" => "correction - the match was released",
        "fee" => "bank charge posted as a reviewed journal",
        "interest" => "interest received posted as a reviewed journal",
        "finalised" => "reconciliation finalised",
        "reopened" => "reconciliation reopened",
        _ => kind,
    };

    /// <summary>One line of the history, with both sides named by their entry numbers.</summary>
    public static string EventSentence(BankReconciliationEventDto evt)
    {
        var position = evt.LineNo > 0 ? $"Line {evt.LineNo}" : "Statement";
        var entries = (evt.PriorEntryNumber, evt.NewEntryNumber) switch
        {
            ({ } prior, { } next) => $"{prior} gave way to {next}",
            (null, { } next) => $"{next}",
            ({ } prior, null) => $"{prior} released",
            _ => "no entry named",
        };

        return evt.Reason is { Length: > 0 } reason
            ? $"{position}: {KindText(evt.Kind)} - {entries}. Reason: {reason}"
            : $"{position}: {KindText(evt.Kind)} - {entries}";
    }

    /// <summary>Money is read at the scale the statement was composed in, never at a scale the screen invented.</summary>
    public static string Money(decimal amount, BankStatementReviewDto? review)
    {
        var scale = review?.Arithmetic.SettlementScale ?? 2;
        return amount.ToString("N" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string MoneyText(decimal amount, BankStatementReviewDto? review)
        => review is null
            ? Money(amount, null)
            : $"{Money(amount, review)} {review.Account.Currency}";

    /// <summary>Only codes that prove nothing was written release a held intention.</summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.OptimisticConcurrencyFailure
        or ApiErrorCodes.BankStatementFinalised
        or ApiErrorCodes.BankStatementNotReconciled
        or ApiErrorCodes.BankCorrectionReasonRequired
        or ApiErrorCodes.Conflict
        or ApiErrorCodes.NotFound
        or ApiErrorCodes.AccountingPeriodClosed
        or ApiErrorCodes.AccountingPeriodMissing;

    /// <summary>One key per intention, bounded the way the server validates it.</summary>
    public static string NewKey(string act, Guid anchor)
    {
        var key = $"{act}-{anchor:N}";
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    /// <summary>A refusal names the rule and the next act, not just a status code.</summary>
    public static string RefusalSentence(ApiException exception, string what) => exception.Code switch
    {
        ApiErrorCodes.BankStatementFinalised =>
            $"'{what}' was refused because the statement has been finalised. Reopen the review with a reason first - the period behind it stays closed.",
        ApiErrorCodes.BankStatementNotReconciled =>
            $"'{what}' was refused while exceptions remain: {exception.Message}. They are answered by a line, not by a balancing figure.",
        ApiErrorCodes.BankCorrectionReasonRequired =>
            $"'{what}' needs the reason an auditor will read.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            $"'{what}' met a statement that had already moved. Read it back before the same intention is repeated.",
        ApiErrorCodes.Conflict =>
            $"'{what}' was refused: {exception.Message}",
        ApiErrorCodes.SameKeyDifferentPayload =>
            $"'{what}' was sent before under this key with different figures. Nothing new was posted; send the exact document again or abandon it.",
        ApiErrorCodes.IdempotencyKeyReused =>
            $"'{what}' cannot reuse a key that already recorded a document. The screen will ask for a fresh one.",
        ApiErrorCodes.AccountingPeriodClosed or ApiErrorCodes.AccountingPeriodMissing =>
            $"'{what}' was refused because the accounting period will not accept a posting: {exception.Message}",
        ApiErrorCodes.NotFound =>
            $"'{what}' named a statement or line this business does not hold, so nothing was changed against it.",
        ApiErrorCodes.ValidationFailed =>
            $"'{what}' was not accepted as it stands: {exception.Message}",
        _ => $"'{what}' could not be confirmed: {exception.Message}. Nothing was assumed about the outcome.",
    };
}
