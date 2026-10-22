using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Finance;

/// <summary>
/// The rules of the receivables workbench (specification task 25): what may be done to a customer's money, in
/// which order, and in whose words. The figures themselves are the server's - this file never computes a balance,
/// it only refuses an action that could not be taken and explains the ones that were.
/// </summary>
public static class ReceivablesRules
{
    /// <summary>What an unapplied deposit is, said wherever a screen shows one (task 25, recipe step 3).</summary>
    public const string DepositRule =
        "Money a customer has left on deposit is held for them until it settles a document. It is not revenue, it does not "
        + "reduce what they owe until it is applied, and it is never allowed to hide a receivable as 'paid' before the money moves.";

    /// <summary>Why a statement is read at a date, and what that date does to the balance (recipe steps 1 and 5).</summary>
    public const string AsOfRule =
        "A statement is read as at one business date: every row is a posted event on or before that date, and the balance beside "
        + "it is the arithmetic over those events. Money that arrived later cannot appear, so a statement handed to a customer "
        + "as at last month cannot be contradicted by this month's ledger.";

    /// <summary>Why an imported opening sits beside invoices in the same list (recipe step 2, finding F26).</summary>
    public const string OpenItemRule =
        "An opening receivable the native import brought in is real money owed, so it is settled from the same deposit and aged "
        + "in the same list as an invoice. What the import recorded is never rewritten: the remaining figure is derived from the "
        + "applications made against it, and no fake invoice is raised for it, because that would charge the customer twice.";

    /// <summary>The one target rule, said to the operator rather than left for a refusal to explain.</summary>
    public const string ExactlyOneTargetRule =
        "Each application settles exactly one document - an invoice or an opening balance. Naming both would count one lot of money twice.";

    /// <summary>What may leave the drawer or the bank when a deposit is handed back (recipe step 4).</summary>
    public const string RefundRule =
        "A refund of unapplied deposit money needs a reason, the amount still unapplied, and a settlement route that can be traced: "
        + "cash out of a drawer that holds it, or a bank movement with its reference. The reversal journal is written with the refund, "
        + "and neither row is ever edited - a mistake is answered by a new authorised event.";

    /// <summary>Which actions this session may take on the account.</summary>
    public static bool CanRecordReceipt(bool mayIssueInvoice) => mayIssueInvoice;

    public static bool CanApplyDeposit(bool mayPostAccounting) => mayPostAccounting;

    public static bool CanRefundDeposit(bool mayPostAccounting) => mayPostAccounting;

    /// <summary>
    /// A refusal explained by the rule behind it. Over-allocation and a lost answer are deliberately different
    /// sentences: the first invites a corrected figure, the second must not invite one at all until the original
    /// command has been resolved (task 25, recipe step 4, T25.02).
    /// </summary>
    public static string RefusalSentence(ApiException exception, string what) => exception.Code switch
    {
        ApiErrorCodes.Conflict =>
            $"The server refused to {what}: {exception.Message} Nothing was posted, and the balances have been read back.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            "Someone else moved this account while the work was in progress. Its current figures are read back before anything is attempted again.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            "This intention was already sent with different figures and the key held belongs to that earlier one. Nothing new was posted: read the account back, then start a fresh intention if the first was wrong.",
        ApiErrorCodes.CashRegisterNotOpen =>
            "That drawer is closed, so cash cannot leave it. Open a session at the register workbench, or settle the refund as a bank movement.",
        ApiErrorCodes.InsufficientDrawerCash =>
            "The drawer does not hold the money this refund would pay out. A shortage is never invented to let money leave a till.",
        ApiErrorCodes.AccountingPeriodClosed =>
            "The period this would post into is closed. Closed periods are not reopened here: the correction belongs in the current period.",
        ApiErrorCodes.Unauthenticated =>
            "The session no longer answers, so nothing was sent. Sign in again and read the account back before repeating the action.",
        ApiErrorCodes.Forbidden or ApiErrorCodes.PermissionDenied =>
            InvoiceAccess.Missing(InvoiceAction.ClearTender),
        ApiErrorCodes.NotFound =>
            "This business has no such record on this account, so nothing was posted against it.",
        ApiErrorCodes.ValidationFailed =>
            InvoiceRules.Reason(exception),
        _ => exception.Message,
    };

    /// <summary>Codes that prove nothing was written, so the key is released and a fresh intention may be made.</summary>
    public static bool CommittedNothing(string? code) => InvoiceRules.CommittedNothing(code);

    /// <summary>A stable key per intention, named after the action and the receipt it belongs to.</summary>
    public static string NewKey(string what, Guid receiptId)
        => InvoiceRules.NewKey(what, receiptId.ToString("N")[..8]);

    /// <summary>Money as the customer's own business reads it; the statement carries the scale it was composed in.</summary>
    public static string Money(decimal value, CustomerStatementDto? statement)
        => InvoiceRules.Money(value, statement?.SettlementScale);

    public static string MoneyText(decimal value, CustomerStatementDto? statement)
        => $"{Money(value, statement)} {statement?.Currency ?? string.Empty}";

    /// <summary>The document a statement row or an open item names, in the words a customer would use.</summary>
    public static string DocumentText(CustomerOpenItemDto item) => item.TargetKind switch
    {
        "invoice" => $"Invoice {item.Reference}",
        "opening" => $"Opening balance {item.Reference}",
        _ => item.Reference,
    };

    public static string KindText(string kind) => kind switch
    {
        "invoice" => "Invoice raised",
        "opening" => "Opening balance brought in",
        "receipt" => "Tender taken against the invoice",
        "application" => "Deposit applied",
        "credit_note" => "Credit note issued",
        "refund" => "Deposit handed back",
        _ => kind,
    };

    /// <summary>The heading a screen shows when an account has genuinely nothing outstanding.</summary>
    public static string EmptyAccountText(string customerName)
        => $"{customerName} owes nothing on this date and holds no unapplied deposit.";
}
