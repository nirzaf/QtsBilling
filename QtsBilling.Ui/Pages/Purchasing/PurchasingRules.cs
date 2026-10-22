using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// The rules of the procurement workbench (specification task 26). They exist in one place so the three screens
/// agree about what an order in a given state may still do, and so the difference between a promise to a
/// supplier, a delivery into a store, and a bill from the supplier is stated in the same words everywhere a
/// person meets it.
/// </summary>
public static class PurchasingRules
{
    public const int MaxKeyLength = 64;

    /// <summary>What placing an order is, and what it is not - the distinction recipe step 1 insists on.</summary>
    public const string CommitmentRule =
        "Placing an order commits this business to a supplier. It moves no stock and posts nothing: quantities arrive "
        + "only when a delivery is recorded, and a cost becomes a payable only when the supplier's bill is matched against it.";

    /// <summary>What a goods received note does, in stock and in the ledger (recipe step 2).</summary>
    public const string ReceiptRule =
        "A goods received note is the moment the delivery becomes real: the quantities land in the named branch's stock at the "
        + "cost the order recorded, and the offsetting liability is recognised. Partial deliveries are ordinary, and receiving "
        + "more than was ordered is refused rather than quietly accepted.";

    /// <summary>What a return is, and what it is not.</summary>
    public const string ReturnRule =
        "A return sends away quantities this branch actually received, at the cost they came in at, as its own linked document. "
        + "It is never a negative receipt and never an edit of a stock count: what was received stays on the record.";

    /// <summary>Where supplier billing belongs, so no screen invents a bill here.</summary>
    public const string BillingRule =
        "The supplier's own invoice is matched against received lines in the payables workbench (task 27). Nothing on this "
        + "screen types a bill into existence, because a bill that does not match a delivery is exactly the double payment that "
        + "separation is here to prevent.";

    /// <summary>Why a line's received figure cannot be typed (recipe step 3).</summary>
    public const string NoManualStockRule =
        "Received and returned quantities are the sum of the documents that moved them, so no field here lets a person overwrite "
        + "one: a corrected quantity is a new receipt or return, and the wrong document stays on the record.";

    /// <summary>The lifecycle as the server enforces it: which actions a document in this state can still take.</summary>
    public static IReadOnlyList<PurchaseOrderAction> AllowedActions(PurchaseOrderState status) => status switch
    {
        PurchaseOrderState.Draft =>
        [
            PurchaseOrderAction.Edit,
            PurchaseOrderAction.Delete,
            PurchaseOrderAction.Place,
        ],
        PurchaseOrderState.Ordered or PurchaseOrderState.PartiallyReceived =>
        [
            PurchaseOrderAction.Receive,
            PurchaseOrderAction.Return,
            PurchaseOrderAction.CancelRemainder,
            PurchaseOrderAction.Print,
        ],
        PurchaseOrderState.Received => [PurchaseOrderAction.Print],
        _ => [PurchaseOrderAction.Print],
    };

    /// <summary>What is still owed by the supplier on one line, never below zero.</summary>
    public static decimal Outstanding(PurchaseOrderLineDto line)
    {
        var remaining = line.QuantityOrdered - line.QuantityReceived;

        return remaining > 0m ? remaining : 0m;
    }

    /// <summary>What may still be sent back: the quantity this branch holds from the line, nothing more.</summary>
    public static decimal Returnable(PurchaseOrderLineDto line)
        => line.QuantityReceived > 0m ? line.QuantityReceived : 0m;

    public static bool IsOpen(PurchaseOrderLineDto line)
        => Outstanding(line) > 0m;

    /// <summary>Why a refusal happened, with the action that answers it, in the operator's words.</summary>
    public static string RefusalSentence(ApiException exception, string document) => exception.Code switch
    {
        ApiErrorCodes.PurchaseOrderNotDraft =>
            $"The {document} is no longer a draft, so its terms cannot be edited. Cancel what will not arrive, or correct the next delivery instead.",
        ApiErrorCodes.PurchaseOrderNotOpen =>
            $"The {document} is not open for deliveries. Only a placed order can receive or return goods; a cancelled order keeps whatever was already delivered.",
        ApiErrorCodes.OverReceipt =>
            $"{exception.Message} The screen only offers what is still outstanding, so this figure came from somewhere else - read the order back before trying again.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            "This delivery was already sent with different quantities, and the key held belongs to that earlier one. Nothing new was posted: read the order back, then start a fresh intention if the first was wrong.",
        ApiErrorCodes.IdempotencyKeyReused =>
            "That key has already recorded a document. The original stands; this attempt wrote nothing.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            "Someone else changed this order while the work was in progress. Its current quantities and cost snapshots are read back before anything is attempted again.",
        ApiErrorCodes.AccountingPeriodClosed =>
            "The period this delivery would post into is closed. Closed periods are not reopened by a screen: review the order, then record the movement in the period that is open.",
        ApiErrorCodes.ValidationFailed =>
            Reason(exception),
        ApiErrorCodes.Forbidden or ApiErrorCodes.PermissionDenied =>
            $"This role may not perform that purchasing action: {Reason(exception)}",
        ApiErrorCodes.NotFound =>
            "This business has no such order, so nothing was posted against it. It may belong to another business or branch you are not acting in.",
        ApiErrorCodes.Unauthenticated =>
            "The session no longer answers, so nothing was sent. Sign in again and read the order back before repeating the action.",
        _ => exception.Message,
    };

    /// <summary>
    /// Codes that prove the server committed nothing, so the same intention may be sent again with a fresh key.
    /// Anything else - a lost response, a 500, a timeout - leaves the outcome unknown: the key and the exact
    /// quantities stay as they were and the same document is re-sent, because a second delivery note for one van
    /// is the error this rule exists to prevent (recipe step 4, T26.02).
    /// </summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.Forbidden
        or ApiErrorCodes.PermissionDenied
        or ApiErrorCodes.Unauthenticated
        or ApiErrorCodes.NotFound
        or ApiErrorCodes.PurchaseOrderNotDraft
        or ApiErrorCodes.PurchaseOrderNotOpen
        or ApiErrorCodes.OverReceipt
        or ApiErrorCodes.OptimisticConcurrencyFailure
        or ApiErrorCodes.AccountingPeriodClosed
        or ApiErrorCodes.SameKeyDifferentPayload
        or ApiErrorCodes.IdempotencyKeyReused;

    /// <summary>One stable key per intention, named after the order so a replay is recognisable on the ledger.</summary>
    public static string NewKey(string what, string? orderNumber)
    {
        var slug = Shorten(orderNumber ?? "po");

        return Shorten($"{what}-{slug}-{Guid.NewGuid():N}", MaxKeyLength);
    }

    /// <summary>The kind as the store reads it: a delivery in, or a quantity sent back.</summary>
    public static string KindText(string kind)
        => kind.Equals("Return", StringComparison.OrdinalIgnoreCase) ? "return" : "goods received note";

    public static bool IsReturn(string kind) => kind.Equals("Return", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The note the server stores. The contract carries one free-text field per document, so the reference the
    /// operator types is kept with the note instead of being dropped - it is what identifies the van or the
    /// supplier's credit note when someone reconciles the account later.
    /// </summary>
    public static string? CombineNote(string? reference, string? note)
    {
        var parts = new[] { reference, note }
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    public static string Reason(ApiException exception)
    {
        var notes = exception.Errors.Values
            .SelectMany(values => values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return notes.Length > 0
            ? string.Join(" ", notes)
            : exception.Message;
    }

    /// <summary>Money as the business reads it. The screens hold no invented two decimals.</summary>
    public static string Money(decimal value) => InvoiceRules.Money(value, null);

    /// <summary>
    /// How an instant on a purchasing document is read: in the receiving branch's own zone, named, because a
    /// delivery filed against a trading day must not claim a day the branch never traded (finding F21).
    /// </summary>
    public static InvoicePresentationDto? PresentationOf(string? branchTimeZoneId)
        => branchTimeZoneId is { Length: > 0 } zone
            ? new InvoicePresentationDto(null, zone, InvoiceRules.BusinessClock(zone))
            : null;

    /// <summary>The ledger entry a receipt produced, stated as the number and the accounts it moved.</summary>
    public static string LedgerText(PurchaseReceiptDto receipt)
        => receipt.JournalEntryNumber is { Length: > 0 } entryNumber
            ? receipt.ControlAccountCodes is { Count: > 0 } codes
                ? $"{entryNumber} · {string.Join(", ", codes)}"
                : entryNumber
            : "no value moved, so nothing posted";

    public static string MoneyText(decimal value, string currency) => $"{Money(value)} {currency}";

    public static string StatusText(PurchaseOrderState status) => status switch
    {
        PurchaseOrderState.Draft => "draft",
        PurchaseOrderState.Ordered => "placed, nothing received",
        PurchaseOrderState.PartiallyReceived => "partly received",
        PurchaseOrderState.Received => "fully received",
        _ => "cancelled",
    };

    public static string QuantityText(decimal quantity)
        => quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : quantity.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static string Shorten(string value) => Shorten(value, 24);

    private static string Shorten(string value, int max)
    {
        var slug = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());

        return slug.Length <= max ? slug : slug[..max];
    }
}

/// <summary>An action the procurement screens can offer for an order as it stands.</summary>
public enum PurchaseOrderAction
{
    Edit,
    Delete,
    Place,
    Receive,
    Return,
    CancelRemainder,
    Print,
}
