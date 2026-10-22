using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// The rules of the payables workbench (specification task 27). They are kept in one place so the three things a
/// payable can be - a matched bill, a credit that reverses part of one, a settlement that closes either - are
/// described in the same words everywhere an operator meets them, and so no screen invents a bill, a negative
/// payable, or a figure the ledger has not agreed to.
/// </summary>
public static class PayablesRules
{
    public const int MaxKeyLength = 64;

    /// <summary>What a supplier bill is allowed to say, in the ledger's terms.</summary>
    public const string MatchRule =
        "A supplier bill is matched to the goods receipt lines it pays for. The bill may charge a different price - "
        + "that difference is posted to the price-variance account, never folded into the stock value - and it may not "
        + "bill more than the delivery holds.";

    /// <summary>What issuing a bill does, and why a draft is not yet a payable.</summary>
    public const string IssueRule =
        "Until the bill is issued it is a draft: nothing is owed and the ledger says so. Issuing clears the "
        + "goods-received value, recognises the input tax the supplier charged, posts the price difference, and opens "
        + "the payable.";

    /// <summary>What a credit may and may not do (recipe step 3).</summary>
    public const string CreditRule =
        "A supplier credit reverses part of an issued bill at that bill's own price, with its reason and, where the "
        + "goods went back, the return that proves it. A credit never removes stock by itself - the return already did "
        + "- and a credit larger than the line can still yield is refused whole.";

    /// <summary>Why a credit balance is its own figure rather than a negative payable (T27.04).</summary>
    public const string HeldCreditRule =
        "A credit the bill cannot absorb stays as the supplier credit balance owed to this business, with its own "
        + "ledger entry. It is never shown as money received, never written off, and never left as an unexplained "
        + "negative payable.";

    /// <summary>What a settlement is allowed to do (recipe step 4).</summary>
    public const string SettlementRule =
        "A settlement pays named documents: an open bill or an imported opening payable, never both at once in one "
        + "allocation, and never more than each still owes. Cash and bank are recorded as the methods the server "
        + "accepts for a supplier; a card is never implied.";

    /// <summary>Why an imported opening needs no bill to be settled (recipe step 2, D17).</summary>
    public const string OpeningRule =
        "An opening payable the native import brought in is a real document owed that was never a supplier bill. It is "
        + "settled by naming itself, its imported amount is never rewritten, and what remains is derived from the "
        + "settlements and credits against it.";

    /// <summary>The account read is as at one date, and money that arrived later cannot appear in it.</summary>
    public const string AsOfRule =
        "Money that arrived later cannot appear in an account read as at an earlier date: every figure below is the "
        + "server's own, assembled from the documents dated on or before that day.";

    /// <summary>The reconciliation the workbench quotes rather than asserts (recipe step 5).</summary>
    public const string ReconciliationRule =
        "Payables reconcile when the control account carries exactly the open documents less the credits held, and the "
        + "goods-received clearing account holds nothing for what has been billed. The figures beside this line are "
        + "the ledger's own, read at the same date.";

    /// <summary>The lifecycle as the server enforces it: which actions a bill in this state can still take.</summary>
    public static IReadOnlyList<PayableAction> AllowedActions(SupplierBillState status) => status switch
    {
        SupplierBillState.Draft => [PayableAction.Issue],
        SupplierBillState.Issued or SupplierBillState.PartiallyPaid =>
        [
            PayableAction.Settle,
            PayableAction.Credit,
            PayableAction.ApplyCredit,
        ],
        SupplierBillState.Paid => [PayableAction.Credit, PayableAction.ApplyCredit],
        _ => [],
    };

    public static bool CanMatchBill(bool mayManagePurchasing) => mayManagePurchasing;

    public static bool CanIssueBill(bool mayPostAccounting) => mayPostAccounting;

    public static bool CanCreditBill(bool mayPostAccounting) => mayPostAccounting;

    public static bool CanSettle(bool mayPostAccounting) => mayPostAccounting;

    /// <summary>How much of a bill line may still be credited, so an over-credit is bounded before it is typed.</summary>
    public static decimal CreditableQuantity(SupplierBillLineDto line)
    {
        var remaining = line.QuantityBilled - line.QuantityCredited;

        return remaining > 0m ? remaining : 0m;
    }

    public static bool IsOpen(SupplierBillLineDto line) => CreditableQuantity(line) > 0m;

    /// <summary>The document named the way an operator knows it; an opening is never dressed up as a bill.</summary>
    public static string DocumentText(SupplierOpenItemDto item) => item.TargetKind switch
    {
        "bill" => $"Bill {item.Reference}",
        "opening" => $"Opening payable {item.Reference}",
        _ => item.Reference,
    };

    public static string KindText(string kind) => kind switch
    {
        "bill" => "bill issued",
        "opening" => "imported opening payable",
        "payment" => "settled",
        "credit" => "supplier credit",
        _ => kind,
    };

    public static string StatusText(SupplierBillState status) => status switch
    {
        SupplierBillState.Draft => "draft, nothing owed yet",
        SupplierBillState.Issued => "issued, unpaid",
        SupplierBillState.PartiallyPaid => "partly settled",
        SupplierBillState.Paid => "settled",
        _ => "void",
    };

    /// <summary>Why a refusal happened, with the action that answers it, in the operator's words.</summary>
    public static string RefusalSentence(ApiException exception, string document) => exception.Code switch
    {
        ApiErrorCodes.SupplierBillNotIssued =>
            $"The {document} is not an issued payable, so it cannot be credited. Issue the bill first: a draft carries no obligation to reverse.",
        ApiErrorCodes.OverCredit =>
            $"{exception.Message} This screen only offers what the line can still yield, so the credit was changed elsewhere - read the bill back before trying again.",
        ApiErrorCodes.DuplicateSupplierReference =>
            $"The supplier's own invoice number is already on file: {Reason(exception)} Credit the bill that holds it rather than entering the same invoice twice.",
        ApiErrorCodes.OverReceipt =>
            $"{exception.Message} Matched quantities come from the deliveries this business recorded, so read the receipt back rather than raising it.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            "This document was already sent with different figures, and the key held belongs to that earlier one. Nothing new was posted: read the account back, then start a fresh intention if the first was wrong.",
        ApiErrorCodes.IdempotencyKeyReused =>
            "That key has already recorded a document. The original stands; this attempt wrote nothing.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            "Someone else changed this account while the work was in progress. Its current balances are read back before anything is attempted again.",
        ApiErrorCodes.AccountingPeriodClosed =>
            "The period this document would post into is closed. Closed periods are not reopened by a screen: review the figures, then record the correction in the period that is open.",
        ApiErrorCodes.Conflict =>
            $"{exception.Message} The balances on this screen are re-read before the same intention is sent again.",
        ApiErrorCodes.ValidationFailed =>
            Reason(exception),
        ApiErrorCodes.Forbidden or ApiErrorCodes.PermissionDenied =>
            $"This role may not perform that payables action: {Reason(exception)}",
        ApiErrorCodes.NotFound =>
            "This business holds no such document, so nothing was posted against it. It may belong to another business or branch you are not acting in.",
        ApiErrorCodes.Unauthenticated =>
            "The session no longer answers, so nothing was sent. Sign in again and read the account back before repeating the action.",
        _ => exception.Message,
    };

    /// <summary>
    /// Codes that prove the server committed nothing, so the same intention may be sent again with a fresh key.
    /// Anything else - a lost response, a 500, a timeout - leaves the outcome unknown: the key and the exact figures
    /// stay as they were and the same document is re-sent, because a second payment for one cheque is the error this
    /// rule exists to prevent.
    /// </summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.Forbidden
        or ApiErrorCodes.PermissionDenied
        or ApiErrorCodes.Unauthenticated
        or ApiErrorCodes.NotFound
        or ApiErrorCodes.Conflict
        or ApiErrorCodes.OverReceipt
        or ApiErrorCodes.OverCredit
        or ApiErrorCodes.SupplierBillNotIssued
        or ApiErrorCodes.DuplicateSupplierReference
        or ApiErrorCodes.OptimisticConcurrencyFailure
        or ApiErrorCodes.AccountingPeriodClosed
        or ApiErrorCodes.SameKeyDifferentPayload
        or ApiErrorCodes.IdempotencyKeyReused;

    /// <summary>One stable key per intention, named after the action so a replay is recognisable on the ledger.</summary>
    public static string NewKey(string what, Guid documentId)
    {
        var token = documentId.ToString("N")[..12];

        return Shorten($"{what}-{token}-{Guid.NewGuid():N}", MaxKeyLength);
    }

    public static string Reason(ApiException exception)
    {
        var notes = exception.Errors.Values
            .SelectMany(values => values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return notes.Length > 0 ? string.Join(" ", notes) : exception.Message;
    }

    /// <summary>Money at the scale the statement was composed in, never at a scale the screen guessed (F21).</summary>
    public static string Money(decimal value, SupplierStatementDto? statement)
        => InvoiceRules.Money(value, statement?.SettlementScale);

    public static string MoneyText(decimal value, SupplierStatementDto? statement)
        => $"{Money(value, statement)} {statement?.Currency ?? "QAR"}";

    /// <summary>
    /// The reconciliation stated as the figures rather than as a claim: the control account, the open documents, and
    /// the credits held. A screen may not say "reconciled" until the arithmetic says it too.
    /// </summary>
    public static string ReconciliationText(SupplierStatementDto statement)
    {
        var documents = statement.OpenItems.Sum(item => item.RemainingAmount);
        var expected = documents - statement.UnappliedCredits;
        var tie = Math.Round(expected - statement.PayableControlBalance, 4, MidpointRounding.AwayFromZero) == 0m;

        return tie
            ? $"Reconciled: the payable control carries {Money(statement.PayableControlBalance, statement)}, which is the open documents {Money(documents, statement)} less the credits held {Money(statement.UnappliedCredits, statement)}. The goods-received clearing account stands at {Money(statement.GrniBalance, statement)}."
            : $"Not reconciled: the payable control carries {Money(statement.PayableControlBalance, statement)} but the documents and credits give {Money(expected, statement)}. Nothing here adjusts the difference - read the documents, and raise the mismatch with whoever posted them.";
    }

    private static string Shorten(string value, int max)
    {
        var slug = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());

        return slug.Length <= max ? slug : slug[..max];
    }
}

/// <summary>An action the payables screen can offer for a bill as it stands.</summary>
public enum PayableAction
{
    Issue,
    Settle,
    Credit,
    ApplyCredit,
}
