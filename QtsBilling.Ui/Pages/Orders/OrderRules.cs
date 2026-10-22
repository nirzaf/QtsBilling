using System.Globalization;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pos;
using QtsModels.ValueObjects;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The decisions the quote and sales-order workbenches make for themselves (specification task 22, recipe steps 1 to 4).
/// Pricing, expiry, revision snapshots, idempotent conversion and the credit limit all belong to the server; what lives
/// here is the wording an operator needs, the actions a status actually allows, the preview that is labelled a preview,
/// and the pre-checks that stop a command being sent when the screen can already see it is unfinished. No commitment
/// made here moves stock, posts revenue or claims an email was delivered.
/// </summary>
public static class OrderRules
{
    /// <summary>The commands a quote status allows, in the order the panel offers them.</summary>
    public enum QuoteAction
    {
        SaveDraft,
        Send,
        Accept,
        Reject,
        Amend,
        Convert,
        Cancel,
    }

    /// <summary>The commands an order status allows.</summary>
    public enum OrderAction
    {
        Confirm,
        Amend,
        CancelRemainder,
    }

    /// <summary>
    /// Which quote commands the server will accept for a status, derived from its own state rules (an amendment or a
    /// cancellation is impossible once the quote has produced an order, and a sent quote is never edited in place).
    /// </summary>
    public static IReadOnlyList<QuoteAction> AllowedQuoteActions(string status, bool alreadyConverted)
        => status switch
        {
            "Draft" => [QuoteAction.SaveDraft, QuoteAction.Send, QuoteAction.Cancel],
            "Sent" when alreadyConverted => [],
            "Sent" => [QuoteAction.Accept, QuoteAction.Reject, QuoteAction.Amend, QuoteAction.Cancel],
            "Accepted" when alreadyConverted => [],
            "Accepted" => [QuoteAction.Amend, QuoteAction.Convert, QuoteAction.Cancel],
            _ => [],
        };

    public static IReadOnlyList<OrderAction> AllowedOrderActions(string status)
        => status switch
        {
            // A draft order carries the terms of the revision it was converted from, so the decision here is to
            // confirm it or leave it; its lines are not silently re-editable behind the quote's back.
            "Draft" => [OrderAction.Confirm],
            "Confirmed" or "PartiallyFulfilled" => [OrderAction.Amend, OrderAction.CancelRemainder],
            _ => [],
        };

    /// <summary>Commands the server refuses without a written reason (task 22, recipe step 1: "required reason").</summary>
    public static bool QuoteActionNeedsReason(QuoteAction action)
        => action is QuoteAction.Reject or QuoteAction.Amend;

    public static bool OrderActionNeedsReason(OrderAction action)
        => action is OrderAction.Amend or OrderAction.CancelRemainder;

    /// <summary>
    /// A quote line's outstanding commitment: what was ordered less what has been delivered and less what was
    /// cancelled. An amendment may only restate this figure, never a quantity that has already gone out
    /// (specification task 22, recipe step 3; task 23 owns the deliveries).
    /// </summary>
    public static decimal Outstanding(OrderLineDto line)
        => Math.Round(line.Quantity - (line.QuantityFulfilled ?? 0m) - (line.QuantityCancelled ?? 0m), 6, MidpointRounding.AwayFromZero);

    /// <summary>True when the line still has something to commit; a settled line is shown, not editable.</summary>
    public static bool IsOpen(OrderLineDto line) => Outstanding(line) > 0m;

    /// <summary>
    /// The screen's own preview of one line, labelled a preview: the server prices the document when it is written and
    /// snapshots the result, so a figure here is never what the customer is committed to (task 22, recipe step 1).
    /// </summary>
    public static decimal PreviewLineTotal(decimal quantity, decimal unitPrice, string taxMode, decimal taxRate, decimal discount)
        => CommercialAmountCalculator.ComputeLine(
            quantity,
            unitPrice,
            ParseTaxMode(taxMode),
            taxRate,
            discount,
            field: "quotePreview").PayableAmount;

    public static CommercialTaxMode ParseTaxMode(string storedTaxMode)
        => string.Equals(storedTaxMode, "Unspecified", StringComparison.OrdinalIgnoreCase)
            ? CommercialTaxMode.Exclusive
            : CommercialAmountCalculator.ParseTaxMode(storedTaxMode, "taxMode");

    /// <summary>Money as the document reads it - two decimals when it is exact, the stored figure when it is not.</summary>
    public static string Money(decimal value) => QtsBilling.Ui.Pages.Sales.RegisterRules.Money(value);

    public static string MoneyText(decimal value, string currency) => $"{Money(value)} {currency}";

    /// <summary>What "send" is and is not (specification task 22, recipe step 2).</summary>
    public const string SendNote =
        "Sending records that this revision was issued and communicated - it is audited with who sent it, when and "
        + "the note you write here. Nothing is emailed and no delivery is claimed: this product has no mail service, "
        + "and a quote that was never communicated stays a draft.";

    /// <summary>The boundary a quote must never cross (task 22, recipe step 4).</summary>
    public const string CommitmentNote =
        "A quote is a price commitment, not a sale: it moves no stock and recognises no revenue. Neither does an "
        + "order - reserving, delivering and invoicing are the fulfillment and document workflow's own commands, each "
        + "with its own evidence.";

    /// <summary>What conversion says about the key it holds.</summary>
    public const string ConversionKeyNote =
        "Conversion keeps one key while it stands, so a retry after a lost answer returns the order that was already "
        + "created instead of a second order for the same accepted revision.";

    /// <summary>Why an amendment is a new revision rather than an edit.</summary>
    public const string RevisionNote =
        "A quote that has been sent is never rewritten. An amendment records a reason and becomes a new revision the "
        + "customer then accepts or rejects; the revision that was accepted stays as the evidence of what was agreed.";

    /// <summary>How the refusal of a command is described when the screen cannot know the cause.</summary>
    public static string RefusalSentence(ApiException exception, string document)
        => exception.Code switch
        {
            ApiErrorCodes.QuoteExpired =>
                $"The {document} has passed its expiry date, so it can no longer be accepted or converted. Re-quote the customer instead.",
            ApiErrorCodes.QuoteNotAccepted =>
                $"The current revision of this {document} has not been accepted, so there is nothing to convert yet.",
            ApiErrorCodes.QuoteNotDraft =>
                $"This {document} is in a state that does not allow that command. Reload it to see the actions it does allow.",
            ApiErrorCodes.OptimisticConcurrencyFailure =>
                $"Someone else changed this {document} since it was loaded. Nothing was written - read the current version and apply the intention again.",
            ApiErrorCodes.SameKeyDifferentPayload =>
                $"That command key was already used for a different {document} request. The stored result stands; a new intention needs a new key.",
            ApiErrorCodes.CreditLimitExceeded =>
                $"The customer's credit limit refuses this commitment: {Reason(exception)}. It is not overridable from this screen - a limit change is a decision on the customer record.",
            ApiErrorCodes.ValidationFailed =>
                $"The server did not accept the {document} payload: {Reason(exception)}",
            ApiErrorCodes.PermissionDenied or ApiErrorCodes.Forbidden =>
                $"This account may not carry out that {document} command.",
            _ => exception.Message,
        };

    /// <summary>The server's own field notes when it sent them, otherwise its sentence.</summary>
    public static string Reason(ApiException exception)
    {
        var notes = exception.Errors.Values
            .SelectMany(values => values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return notes.Length == 0 ? exception.Message : string.Join(" ", notes);
    }

    /// <summary>The sentence shown while an order command has no answer.</summary>
    public const string UnresolvedOrderNote =
        "An order command whose answer never arrived is not repeated blindly: reload the order and see whether the "
        + "state moved. Confirmation and cancellation are idempotent by state, so a re-read decides - not a second click.";

    /// <summary>The commands whose refusal proves nothing was written, so the operator may start a new intention.</summary>
    public static bool CommittedNothing(string? code)
        => code is ApiErrorCodes.ValidationFailed
            or ApiErrorCodes.QuoteExpired
            or ApiErrorCodes.QuoteNotAccepted
            or ApiErrorCodes.QuoteNotDraft
            or ApiErrorCodes.OptimisticConcurrencyFailure
            or ApiErrorCodes.SalesOrderNotDraft
            or ApiErrorCodes.SalesOrderNotOpen
            or ApiErrorCodes.CreditLimitExceeded
            or ApiErrorCodes.Forbidden
            or ApiErrorCodes.PermissionDenied
            or ApiErrorCodes.Unauthenticated
            or ApiErrorCodes.NotFound
            or ApiErrorCodes.SameKeyDifferentPayload;

    /// <summary>A conversion intention's key: one per intention, reused for every retry of that same intention (D06).</summary>
    public static string NewConversionKey(string quoteNumber)
    {
        var slug = Shorten(quoteNumber);

        // "convert-<quote>-<guid>" is always inside the 64-character header bound the server validates.
        return $"convert-{slug}-{Guid.NewGuid():N}";
    }

    /// <summary>The key for any other keyed command (a draft creation), held while the intention stands.</summary>
    public static string NewCommandKey(string what) => Shorten($"{what}-{Guid.NewGuid():N}");

    private static string Shorten(string value)
    {
        var slug = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());

        return slug.Length <= 24 ? slug : slug[..24];
    }

    /// <summary>Whether the quote command may be sent at all: a reason where one is required, and nothing unresolved.</summary>
    public static bool CanSendQuoteCommand(
        QuoteAction action,
        string? reason,
        bool busy,
        bool awaitingAnswer,
        long version)
        => !busy
            && !awaitingAnswer
            && version > 0L
            && (!QuoteActionNeedsReason(action) || !string.IsNullOrWhiteSpace(reason));

    /// <summary>The list's own search advice, so a term is never guessed at.</summary>
    public static string SearchHelp(string document)
        => $"Searches the {document} number on the server, so the answer is not limited to the page in front of you.";

    /// <summary>How a status is read by a person rather than a state machine.</summary>
    public static string QuoteStatusText(string status, DateOnly expiry, DateOnly today)
        => status switch
        {
            "Draft" => "Draft - not yet communicated",
            "Sent" when expiry < today => "Expired since its date passed",
            "Sent" => $"Sent - expires {expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            "Accepted" => "Accepted by the customer",
            "Rejected" => "Rejected",
            "Expired" => "Expired",
            "Cancelled" => "Cancelled",
            _ => status,
        };

    public static string OrderStatusText(string status)
        => status switch
        {
            "Draft" => "Draft - not committed yet",
            "Confirmed" => "Confirmed - outstanding to fulfil",
            "PartiallyFulfilled" => "Partly fulfilled",
            "Fulfilled" => "Fulfilled",
            "Closed" => "Closed",
            _ => status,
        };
}
