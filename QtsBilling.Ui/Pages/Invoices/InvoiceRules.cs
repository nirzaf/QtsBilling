using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;

namespace QtsBilling.Ui.Pages.Invoices;

/// <summary>
/// The settlement rules of the invoice workbench (specification task 24, recipe steps 1-4). One place owns the
/// sentences, the state machine, the key discipline and - deliberately - the way money and dates are read, so no
/// screen has to decide on its own how many decimals this business settles in or what "today" means for it
/// (finding F21).
/// </summary>
public static class InvoiceRules
{
    /// <summary>How long an outstanding money intention keeps its key before the operator is asked to review.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The tender methods a person at this screen may hand over. Card is absent on purpose: a card is
    /// taken by a terminal that verifies the capture, and this workbench never pretends one happened (finding F20).</summary>
    public static readonly (string Value, string Label)[] OfferedMethods =
    [
        ("cash", "Cash"),
        ("cheque", "Cheque"),
        ("bankTransfer", "Bank transfer"),
    ];

    /// <summary>Which of the offered methods the server insists on a processing reference for.</summary>
    public static bool RequiresReference(string method) => method is "cheque" or "bankTransfer";

    /// <summary>
    /// Money as this business reads it (finding F21). The value itself is never altered: only the number of
    /// decimals it is shown with, taken from the settlement scale the server projected with the document. A
    /// business that has never chosen a scale is shown the precision its values were stored with - at least two
    /// decimals, never more than the amount actually holds - instead of being silently agreed with.
    /// </summary>
    public static string Money(decimal value, int? settlementScale)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        return settlementScale switch
        {
            null => value.ToString("0.00####", culture),
            0 => value.ToString("0", culture),
            { } scale when scale is > 0 and <= 6 => value.ToString("0." + new string('0', scale), culture),
            _ => value.ToString("0.00####", culture),
        };
    }

    public static string MoneyText(decimal value, string currency, int? settlementScale)
        => $"{Money(value, settlementScale)} {currency}";

    /// <summary>
    /// A date the document owns (issue and due dates) is a business date already, so it is written in the one
    /// form that cannot be misread: ISO, in the business's own calendar. Never a machine-local format.
    /// </summary>
    public static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An instant, read in the business's own zone, because a document filed against a trading day must
    /// not claim a day the business never traded on. The zone is named so the reading is checkable.</summary>
    public static string InstantText(DateTimeOffset instant, InvoicePresentationDto? presentation)
    {
        var zone = Zone(presentation);
        var local = zone is null
            ? instant.ToUniversalTime()
            : instant.ToOffset(OffsetOrNull(zone) ?? TimeSpan.Zero);

        var label = zone?.Id ?? "UTC";
        return $"{local:yyyy-MM-dd HH:mm} ({label})";
    }

    /// <summary>
    /// The clock a form may offer as "today" when no document has been read yet. A named zone is the business's;
    /// with none, UTC is the only honest answer - a screen never falls back to the machine's local date, because a
    /// terminal in another zone would then raise a document dated on a day the business has not reached (F21).
    /// </summary>
    public static DateOnly BusinessClock(string? timeZoneId = null)
    {
        var zone = Resolve(timeZoneId);
        var instant = DateTimeOffset.UtcNow;

        return DateOnly.FromDateTime(zone is null ? instant.UtcDateTime : TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    /// <summary>How the recognition source of a document is named to an operator.</summary>
    public static string RecognitionText(InvoiceRecognitionMode mode) => mode switch
    {
        InvoiceRecognitionMode.Service => "service",
        InvoiceRecognitionMode.DirectGoods => "direct-goods",
        InvoiceRecognitionMode.SaleRepresentation => "sale representation",
        InvoiceRecognitionMode.Delivery => "delivery",
        _ => "unknown (historical)",
    };

    /// <summary>The business's own zone, or null when the business has never been given one.</summary>
    public static TimeZoneInfo? Zone(InvoicePresentationDto? presentation)
        => Resolve(presentation?.TimeZoneId);

    /// <summary>What the server calls "today" for this business, as it projected it with the document.</summary>
    public static string BusinessTodayText(InvoiceDetailDto detail)
        => detail.Presentation is { } presentation
            ? $"{DateText(presentation.BusinessToday)} ({presentation.TimeZoneId ?? "the business's calendar"})"
            : DateText(DateOnly.FromDateTime(DateTime.UtcNow));

    public static int? Scale(InvoiceDetailDto detail) => detail.Presentation?.SettlementScale;

    /// <summary>
    /// The lifecycle as the server actually enforces it: which actions a document in this state can still take,
    /// and which role owns each. A button that could only be refused is not offered (task 24, recipe step 1).
    /// </summary>
    public static IReadOnlyList<InvoiceAction> AllowedActions(InvoiceDetailDto detail, InvoiceAccess access)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var invoice = detail.Invoice;
        var actions = new List<InvoiceAction>();

        if (access.CanSettle && invoice.Status is InvoiceState.Issued or InvoiceState.PartiallyPaid)
        {
            actions.Add(InvoiceAction.RecordPayment);
        }

        if (access.CanManage && invoice.Status is InvoiceState.Issued or InvoiceState.PartiallyPaid or InvoiceState.Paid)
        {
            actions.Add(InvoiceAction.Credit);
        }

        // Only a service invoice may be voided: a document owned by a sale or a delivery must be corrected
        // through its own workflow, so that stock and cost are reversed exactly once (the server refuses here).
        if (access.CanManage
            && invoice.Status is InvoiceState.Issued or InvoiceState.PartiallyPaid
            && invoice.RecognitionMode == InvoiceRecognitionMode.Service
            && detail.AwaitingClearanceAmount == 0m
            && invoice.PaidAmount == 0m)
        {
            actions.Add(InvoiceAction.Void);
        }

        if (access.CanPost && detail.Payments.Any(IsAwaitingClearance))
        {
            actions.Add(InvoiceAction.ClearTender);
            actions.Add(InvoiceAction.BounceTender);
        }

        if (invoice.Status != InvoiceState.Draft)
        {
            actions.Add(InvoiceAction.Print);
        }

        return actions;
    }

    public static bool IsAwaitingClearance(InvoicePaymentDto payment)
        => payment.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase)
            && (payment.Method.Equals("Cheque", StringComparison.OrdinalIgnoreCase)
                || payment.Method.Equals("BankTransfer", StringComparison.OrdinalIgnoreCase));

    /// <summary>How much of one line can still be credited: what was invoiced less what earlier notes already
    /// took, computed from the server's own credit lines rather than from a remembered figure.</summary>
    public static decimal RemainingQuantity(InvoiceLineDto line, InvoiceDetailDto detail)
    {
        var credited = detail.CreditNotes?
            .SelectMany(note => note.Lines ?? [])
            .Where(credit => credit.InvoiceLineId == line.Id)
            .Sum(credit => credit.QuantityCredited) ?? 0m;

        var remaining = line.Quantity - credited;

        return remaining > 0m ? remaining : 0m;
    }

    public static decimal RemainingValue(InvoiceLineDto line, InvoiceDetailDto detail)
    {
        var remaining = RemainingQuantity(line, detail);

        return line.Quantity == 0m
            ? 0m
            : decimal.Round(line.LineTotal / line.Quantity * remaining, Math.Max(2, FractionDigits(line.LineTotal)));
    }

    /// <summary>
    /// How many decimals a stored amount actually carries. A screen needs this to keep a figure it derives from
    /// that amount at the same precision, rather than rounding money to the two places it assumed. The scale is
    /// read from the value itself, because <c>115.00</c> and <c>115</c> are the same number but different
    /// statements of precision, and a document must not lose the difference.
    /// </summary>
    public static int FractionDigits(decimal value)
    {
        var bits = decimal.GetBits(value);

        return (bits[3] >> 16) & 0xFF;
    }

    /// <summary>Why a refusal happened, in the operator's terms, with the action that answers it.</summary>
    public static string RefusalSentence(ApiException exception) => exception.Code switch
    {
        ApiErrorCodes.CashRegisterNotOpen =>
            "That register has no open drawer session, so the cash would have nowhere to be counted in. Open the drawer at the register workbench, or take the tender as a transfer.",
        ApiErrorCodes.InsufficientDrawerCash =>
            "The till cannot pay this out. The money has to be in the drawer before a refund or a paid-out can be posted against it.",
        ApiErrorCodes.InvoiceNotIssued =>
            "This document has not been issued, so there is nothing to settle. Issue it first, or keep editing the draft.",
        ApiErrorCodes.InvoiceHasPayments =>
            "An invoice that already holds money, or a tender that is still on its way, cannot be voided. Clear or bounce the pending tender first, or correct the document with a credit note.",
        ApiErrorCodes.InvoiceAlreadyCredited =>
            "This document has already been credited in full. A second credit would take back money that is no longer there.",
        ApiErrorCodes.AccountingPeriodClosed =>
            "The accounting period this action would post into is closed. Closed periods are not reopened by a screen: review the document, then post the correction in the current period.",
        ApiErrorCodes.PaymentUnconfirmed =>
            "A card tender can only be recorded by a terminal that verifies the capture, and this workbench never claims one on its own.",
        ApiErrorCodes.CreditLimitExceeded =>
            "The customer's credit limit refuses this. It is not overridable from this screen.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            "Someone else posted against this document while you were working. Its current figures are read back before anything is attempted again.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            "This intention was already sent with different figures, and the key you hold belongs to that earlier one. Nothing new was posted: review the document and start a fresh intention if the first was wrong.",
        ApiErrorCodes.IdempotencyKeyReused =>
            "That key has already been spent on another posting. The original posting stands; this attempt wrote nothing.",
        ApiErrorCodes.Unauthenticated =>
            "The session no longer answers, so nothing was sent. Sign in again, then read the document back before repeating the action.",
        ApiErrorCodes.Forbidden or ApiErrorCodes.PermissionDenied =>
            $"This role may not take that action: {Reason(exception)}",
        ApiErrorCodes.NotFound =>
            "This business has no such document, so there is nothing to print or post. The invoice may belong to another business or branch you are not acting in.",
        ApiErrorCodes.ValidationFailed =>
            Reason(exception),
        _ => exception.Message,
    };

    /// <summary>
    /// Codes that prove the server committed nothing, so the same intention may be sent again under the same key.
    /// Anything else - a timeout, a transport failure, a 500 - leaves the outcome unknown: the key and the body
    /// stay exactly as they were and the same request is re-sent, which is how the original post is recovered
    /// instead of a duplicate (task 24, recipe step 2, T24.03).
    /// </summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.Forbidden
        or ApiErrorCodes.PermissionDenied
        or ApiErrorCodes.Unauthenticated
        or ApiErrorCodes.NotFound
        or ApiErrorCodes.CashRegisterNotOpen
        or ApiErrorCodes.InsufficientDrawerCash
        or ApiErrorCodes.InvoiceNotIssued
        or ApiErrorCodes.InvoiceHasPayments
        or ApiErrorCodes.InvoiceAlreadyCredited
        or ApiErrorCodes.AccountingPeriodClosed
        or ApiErrorCodes.PaymentUnconfirmed
        or ApiErrorCodes.CreditLimitExceeded
        or ApiErrorCodes.OptimisticConcurrencyFailure
        or ApiErrorCodes.SameKeyDifferentPayload
        or ApiErrorCodes.IdempotencyKeyReused;

    /// <summary>A stable key per intention: named after the document and the action, so a replayed attempt is
    /// recognisable in the ledger and never a new one.</summary>
    public static string NewKey(string what, string? documentNumber)
    {
        var slug = Shorten(documentNumber ?? "invoice");

        return Shorten($"{what}-{slug}-{Guid.NewGuid():N}", MaxKeyLength);
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

    /// <summary>What an uncleared tender is, said once and in the same words wherever a figure appears.</summary>
    public const string AwaitingClearanceRule =
        "A cheque or transfer that has not cleared is money the customer has promised, not money this business holds. "
        + "It never reduces the balance due until it is confirmed; confirming it posts the journal, and bouncing it records that the bank refused.";

    /// <summary>What a cash tender at this screen means for a drawer (D07).</summary>
    public const string CashDrawerRule =
        "Cash taken against an invoice belongs to a drawer session: choosing the register counts it into that till, and the "
        + "server refuses a register whose session is closed, so the money cannot land in a drawer nobody is accountable for.";

    /// <summary>The credit note's own rule (task 03): a credit follows the document's original tax and cost.</summary>
    public const string CreditRule =
        "A credit reverses the original lines with their own tax composition and, where stock comes back, the cost the sale left behind. "
        + "Nothing here re-prices history: what a document said when it was issued is what the credit takes back.";

    /// <summary>Why printing goes through the host and never through a link.</summary>
    public const string PrintRule =
        "The printed page is the stored snapshot, fetched with this session's own credentials and handed to the host's local "
        + "print dialogue. No address is navigated, so no token can ride in a URL, nothing is uploaded and no copy is written to disk.";

    /// <summary>Why an invoice can never be raised for an unnamed buyer: the server's own rule, stated where the
    /// operator would otherwise discover it as a refusal after typing the whole document.</summary>
    public const string NeedsCustomer =
        "An invoice names the customer it asks for money from: the amount owed is tracked against them, and the "
        + "aging, the statement and the credit limit all read that link. A counter sale to someone who is not on "
        + "the file is made at the point of sale, not as an invoice.";

    /// <summary>The label for a document that carries no customer - a fact about history, not an option.</summary>
    public const string NoCustomerRecorded = "No customer is recorded on this document";

    public static string RestockSentence(bool restock) => restock
        ? "The goods come back to stock at the cost the delivery left behind."
        : "The goods are written off where they stand: no stock movement, only the money and the tax.";

    private static string Shorten(string value, int max = 24)
    {
        var slug = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());

        return slug.Length <= max ? slug : slug[..max];
    }

    private static TimeZoneInfo? Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static TimeSpan? OffsetOrNull(TimeZoneInfo zone)
    {
        try
        {
            return zone.GetUtcOffset(DateTimeOffset.UtcNow);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>An action the settlement workbench can offer for the document as it stands.</summary>
public enum InvoiceAction
{
    RecordPayment,
    Credit,
    Void,
    ClearTender,
    BounceTender,
    Print,
}

/// <summary>
/// What the signed-in session may do to this document. The screen derives it from the session's own permission
/// list - the same list the server checks - so a button is never offered on a guess, and a refusal is explained
/// by naming the authority that is missing.
/// </summary>
public sealed record InvoiceAccess(bool CanSettle, bool CanManage, bool CanPost, bool CanReadRegisters)
{
    public static string Missing(InvoiceAction action) => action switch
    {
        InvoiceAction.RecordPayment => "Recording a tender needs the invoice-issuing authority of this business.",
        InvoiceAction.Credit => "Crediting a document needs the invoice-managing authority; ask an owner to review your role.",
        InvoiceAction.Void => "Voiding needs the invoice-managing authority, and only a document that holds no money can be voided.",
        InvoiceAction.ClearTender or InvoiceAction.BounceTender => "Clearing or bouncing a tender posts to the ledger, so it needs the accounting-posting authority.",
        _ => "This action is not available to your current role.",
    };
}
