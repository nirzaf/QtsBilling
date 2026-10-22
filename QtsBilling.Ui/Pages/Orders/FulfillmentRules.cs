using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Orders;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The decisions the fulfillment workbench makes for itself (specification task 23, recipe steps 1 to 4). The
/// quantities, the stock consumption, the reservation expiry and the recognition rule all belong to the server;
/// what lives here is the wording, the three separate keys a reservation, a delivery and an invoice each need, and
/// the pre-checks that stop a command being sent when the server has already said this line cannot move.
/// </summary>
public static class FulfillmentRules
{
    /// <summary>What a line may still be dispatched, in the operator's words.</summary>
    public static string DeliverableSentence(FulfillmentLineDto line)
        => line.BlockReason is { Length: > 0 } reason
            ? reason
            : $"{Money(line.Outstanding)} still outstanding"
                + (line.ReservedOutstanding > 0m ? $" - {Money(line.ReservedOutstanding)} of it is reserved" : string.Empty)
                + (line.TrackStock && line.OnHand is { } onHand ? $" - {Money(onHand)} on hand at this branch." : ".");

    public static string Money(decimal value) => QtsBilling.Ui.Pages.Sales.RegisterRules.Money(value);

    public static string MoneyText(decimal value, string currency) => QtsBilling.Ui.Pages.Sales.RegisterRules.MoneyText(value, currency);

    /// <summary>
    /// The three kinds of command key. A reservation, a delivery and an invoice are separate intentions with
    /// separate receipts at the server, so they never share one key (specification D06, task 23 step 2).
    /// </summary>
    public static string NewKey(string what, string documentNumber)
    {
        var slug = new string(documentNumber.Where(char.IsLetterOrDigit).ToArray());

        if (slug.Length > 16)
        {
            slug = slug[..16];
        }

        var key = $"{what}-{slug}-{Guid.NewGuid():N}";

        return key.Length <= 64 ? key : key[..64];
    }

    /// <summary>The refusal codes that prove nothing was committed, so a new intention may be started.</summary>
    public static bool CommittedNothing(string? code)
        => code is ApiErrorCodes.ValidationFailed
            or ApiErrorCodes.InsufficientStock
            or ApiErrorCodes.SalesOrderNotOpen
            or ApiErrorCodes.OptimisticConcurrencyFailure
            or ApiErrorCodes.PermissionDenied
            or ApiErrorCodes.Forbidden
            or ApiErrorCodes.Unauthenticated
            or ApiErrorCodes.NotFound
            or ApiErrorCodes.Conflict
            or ApiErrorCodes.SameKeyDifferentPayload
            or ApiErrorCodes.AccountingPeriodClosed;

    /// <summary>What each fulfillment refusal means for the operator (task 23, recipe step 4).</summary>
    public static string RefusalSentence(ApiException exception, string orderNumber)
        => exception.Code switch
        {
            ApiErrorCodes.InsufficientStock =>
                $"The branch's stock does not cover that quantity ({Reason(exception)}). Nothing was moved: transfer it in or count it, then deliver what is really there.",
            ApiErrorCodes.OptimisticConcurrencyFailure =>
                $"Order {orderNumber} changed since this screen loaded, so the command was refused before anything moved. Reload it and apply the intention again against the version the server holds.",
            ApiErrorCodes.SalesOrderNotOpen =>
                $"Order {orderNumber} is not open for that command - a cancelled remainder and an already-delivered quantity are never dispatched later ({Reason(exception)}).",
            ApiErrorCodes.AccountingPeriodClosed =>
                $"The accounting period covering that date is closed, so the delivery or invoice cannot post ({Reason(exception)}). Nothing was recorded; an adjustment in an open period is the only route.",
            ApiErrorCodes.Conflict =>
                $"The fulfillment command conflicts with what is already recorded: {Reason(exception)}",
            ApiErrorCodes.SameKeyDifferentPayload =>
                "That command key already carries a different delivery or invoice. The stored document stands - a different intention needs a new key.",
            ApiErrorCodes.ValidationFailed =>
                $"The server did not accept the payload: {Reason(exception)}",
            ApiErrorCodes.PermissionDenied or ApiErrorCodes.Forbidden =>
                "This account may not fulfill at this branch, or holds neither the dispatch nor the order authority.",
            _ => Reason(exception),
        };

    private static string Reason(ApiException exception)
    {
        var notes = exception.Errors.Values
            .SelectMany(values => values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return notes.Length == 0 ? exception.Message : string.Join(" ", notes);
    }

    /// <summary>The three sentences the panel states before any click (task 23, recipe steps 1 and 4).</summary>
    public const string SeparationNote =
        "A reservation is a promise about stock, a delivery is stock that has physically left, and an invoice is "
        + "the financial recognition of what was delivered. They are three separate commands with three separate "
        + "keys: reserving moves no stock and posts nothing, and invoicing never invents a quantity that was not delivered.";

    public const string ImmutabilityNote =
        "Dispatched stock is never taken back by this screen. A wrong delivery is corrected by an explicit, linked "
        + "return or reversal, and an unfulfilled commitment is released - not deleted.";

    public const string RecognitionNote =
        "One delivered quantity is recognised once: an invoice may only consume delivered-but-not-yet-invoiced "
        + "quantities, and the original cost and revenue snapshots travel with it.";

    public const string DeliveryNoteCopy =
        "This is a delivery note composed from the committed delivery snapshot - its number, date, quantities and "
        + "cost values are the server's. Printing it changes nothing, and it is marked a copy.";

    /// <summary>Whether an invoice line may ask for that quantity: never more than delivered and not yet invoiced.</summary>
    public static decimal InvoiceableQuantity(SalesOrderDeliveryLineDto line)
        => Math.Max(0m, Math.Round(line.Quantity - line.QuantityInvoiced, 6, MidpointRounding.AwayFromZero));

    /// <summary>Whether the reserve command may be sent: stock-managed, not already reserved, nothing unresolved.</summary>
    public static bool CanReserve(OrderFulfillmentDto view, bool busy, bool awaitingAnswer, DateTimeOffset expiresAt, DateTimeOffset now)
        => !busy
            && !awaitingAnswer
            && view.Lines.Any(line => line.TrackStock && line.Outstanding > 0m)
            && expiresAt > now
            && (view.Reservation is null || view.Reservation.Status != "Active");

    /// <summary>Whether a delivery of the chosen quantities may be sent.</summary>
    public static bool CanDeliver(
        OrderFulfillmentDto view,
        IReadOnlyDictionary<Guid, decimal> quantities,
        bool busy,
        bool awaitingAnswer)
        => !busy
            && !awaitingAnswer
            && quantities.Any(entry => entry.Value > 0m)
            && view.Lines.All(line => quantities.GetValueOrDefault(line.OrderLineId) <= line.Outstanding)
            && view.Lines.Where(line => quantities.GetValueOrDefault(line.OrderLineId) > 0m)
                .All(line => line.Deliverable);
}
