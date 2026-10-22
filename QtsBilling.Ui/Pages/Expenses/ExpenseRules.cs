using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Ui.Pages.Sales;

namespace QtsBilling.Ui.Pages.Expenses;

/// <summary>
/// The rules of the expense workbench (specification task 28). They exist in one place so the list and the
/// editor agree about what a posted expense is, what a reversal does, and why LocalCore never pretends to
/// take a card payment: an expense is a classified cash or bank movement with one balanced ledger entry.
/// </summary>
public static class ExpenseRules
{
    public const int MaxKeyLength = 64;

    /// <summary>What posting an expense is - one balanced entry, not a card acquisition.</summary>
    public const string PostingRule =
        "Posting an expense records money that has already left the business: one balanced ledger entry, expense "
        + "account against the cash or bank account the payment method names. LocalCore never calls a payment "
        + "provider and never stores a card number; recording an externally paid expense is a bookkeeping fact.";

    /// <summary>What a reversal is, and what it never does.</summary>
    public const string ReversalRule =
        "A reversal is a new reasoned entry that mirrors the original - the original expense and its journal both "
        + "stay on the record. Each expense can be reversed only once, and a closed period is not rewritten: the "
        + "correction posts in an open period with its own date.";

    /// <summary>Why tax is entered in percent and converted once at the boundary.</summary>
    public const string TaxRule =
        "The rate is entered as a percentage (15 means 15%) and reaches the server as a fraction exactly once. "
        + "The net, tax and paid total shown here are an estimate until the server confirms them.";

    public static bool IsReversed(ExpenseDto expense) => expense.ReversedAtUtc is not null;

    public static bool CanReverse(ExpenseDto expense) => expense.JournalEntryId is not null && !IsReversed(expense);

    /// <summary>The paid-via values the server accepts, in the order the editor offers them.</summary>
    public static readonly IReadOnlyList<string> PaidViaOptions =
    [
        "cash",
        "cheque",
        "bankTransfer",
        "card",
    ];

    public static string PaidViaText(string? paidVia) => paidVia?.Trim().ToLowerInvariant() switch
    {
        "cash" => "Cash",
        "cheque" => "Cheque",
        "banktransfer" => "Bank transfer",
        "card" => "Card (settled through the bank)",
        _ => string.IsNullOrWhiteSpace(paidVia) ? "not named" : paidVia,
    };

    /// <summary>The account the paid-via method credits, so the editor can state it before posting.</summary>
    public static string CreditAccountText(string? paidVia) => paidVia?.Trim().ToLowerInvariant() switch
    {
        "cash" => "1000 cash",
        "cheque" or "banktransfer" or "card" => "1010 bank",
        _ => "the configured cash or bank account",
    };

    public static string TaxModeText(string? taxMode) => taxMode?.Trim() switch
    {
        "Inclusive" => "inclusive of tax",
        "Exempt" => "exempt",
        _ => "exclusive of tax",
    };

    /// <summary>
    /// The tax figures the editor previews before the server confirms them. Entered as percent; the server
    /// receives the fraction; the paid total is amount for inclusive and amount plus tax for exclusive.
    /// </summary>
    public static (decimal Net, decimal Tax, decimal Paid) Preview(decimal amount, decimal taxPercent, string? taxMode)
    {
        if (amount <= 0m || taxPercent <= 0m)
        {
            return (amount, 0m, amount);
        }

        var rate = taxPercent / 100m;
        return taxMode?.Trim() switch
        {
            "Inclusive" => (amount - LedgerRound(amount * rate / (1m + rate)), LedgerRound(amount * rate / (1m + rate)), amount),
            "Exempt" => (amount, 0m, amount),
            _ => (amount, LedgerRound(amount * rate), LedgerRound(amount + amount * rate)),
        };
    }

    public static string Money(decimal amount) => RegisterRules.Money(amount);

    public static string MoneyText(decimal amount, string? currency) => RegisterRules.MoneyText(amount, currency ?? string.Empty);

    /// <summary>Why a refusal happened, with the action that answers it, in the operator's words.</summary>
    public static string RefusalSentence(ApiException error, string action) => error.Code switch
    {
        ApiErrorCodes.AlreadyReversed =>
            $"This expense is already reversed, so {action} is refused - the original and its mirror both stay on the record. Nothing new was posted.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            $"This key already recorded a different request, so {action} was not posted twice. Start a new intention instead of changing the body under the old key.",
        ApiErrorCodes.IdempotencyKeyReused =>
            $"A concurrent request with the same key committed first, so {action} returned the stored expense rather than posting a second one.",
        ApiErrorCodes.NotFound =>
            $"That expense is not in this business, so {action} had nothing to act on. Nothing was posted.",
        ApiErrorCodes.AccountingPeriodClosed =>
            $"The period for that date is closed, so {action} was refused. Choose a correction date in an open period; no history was rewritten.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            $"Someone else changed this record, so {action} was refused. Read the expense back and decide again.",
        _ => $"The server refused {action}: {error.Message}",
    };

    /// <summary>
    /// True only for codes that prove nothing was written. A lost answer keeps the key and the body so the
    /// same intention can be re-sent; an internal error is never treated as a clean refusal.
    /// </summary>
    public static bool CommittedNothing(string? code) => code switch
    {
        ApiErrorCodes.NotFound => true,
        ApiErrorCodes.AlreadyReversed => true,
        ApiErrorCodes.SameKeyDifferentPayload => true,
        ApiErrorCodes.IdempotencyKeyReused => true,
        ApiErrorCodes.AccountingPeriodClosed => true,
        ApiErrorCodes.OptimisticConcurrencyFailure => true,
        ApiErrorCodes.ValidationFailed => true,
        null => false,
        _ => false,
    };

    public static string NewKey(string intent, string seed)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var key = $"{intent}-{seed}-{suffix}";
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    private static decimal LedgerRound(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
