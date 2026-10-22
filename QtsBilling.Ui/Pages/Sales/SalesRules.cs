using System.Globalization;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsModels.ValueObjects;

namespace QtsBilling.Ui.Pages.Sales;

/// <summary>
/// The decisions the sales history and refund workbench makes for itself (specification task 21, recipe steps 1 to 4).
/// The remaining quantities, the refundable money, the cost a restock takes back and the cash a drawer may pay out
/// all belong to the server; what lives here is the wording an operator needs, the preview of a chosen quantity
/// labelled as a preview, and the pre-checks that stop a command being sent when the screen can already see it is
/// unfinished. Nothing here decides whether money may be paid back - the server answers that, and its sentence is
/// what the screen shows.
/// </summary>
public static class SalesRules
{
    /// <summary>Money as the drawer reads it - the same rule the register workbench states (task 19).</summary>
    public static string Money(decimal value) => RegisterRules.Money(value);

    public static string MoneyText(decimal value, string currency) => RegisterRules.MoneyText(value, currency);

    /// <summary>
    /// A row is described by the server's own two states. A sale whose payment was never confirmed is money nobody
    /// has collected, and the list says so rather than presenting it as a settled cash sale (task 21, recipe step 1).
    /// </summary>
    public static string StateText(SaleHistoryDto row)
        => row.Status switch
        {
            SaleState.NeedsReview => "Awaiting review - the money this terminal reported is not settled",
            SaleState.Reversed => row.PaymentStatus == PaymentStatus.Refunded
                ? "Fully refunded"
                : "Reversed",
            _ when row.PaymentStatus == PaymentStatus.Pending => "Payment not confirmed - not cash in a till",
            _ when row.PaymentStatus == PaymentStatus.Bounced => "Payment bounced - nothing was collected",
            _ when row.PaymentStatus == PaymentStatus.Voided => "Payment voided",
            _ when row.PaymentStatus == PaymentStatus.Refunded => "Refunded",
            _ when row.RefundCount > 0 => $"Partly refunded ({row.RefundCount.ToString(CultureInfo.InvariantCulture)} refund(s))",
            _ => "Posted",
        };

    /// <summary>True when the row's money has not actually arrived, so no refund may be offered for it.</summary>
    public static bool IsUncollected(SaleHistoryDto row)
        => row.Status == SaleState.NeedsReview
            || row.PaymentStatus is PaymentStatus.Pending or PaymentStatus.Bounced or PaymentStatus.Voided;

    /// <summary>
    /// The screen's own total for a chosen quantity, labelled a preview: the server recomputes it from the line's
    /// snapshots (prorating the posted discount and keeping the residual exact for the last part), so a figure here
    /// is never what is committed (task 21, recipe step 2).
    /// </summary>
    public static decimal PreviewLineTotal(SaleRefundLinePositionDto line, decimal quantity)
    {
        if (quantity <= 0m || line.SoldQuantity <= 0m)
        {
            return 0m;
        }

        // The whole remaining quantity is the server's own residual, so the preview repeats that figure instead of
        // recomputing it and losing the rounding difference.
        if (quantity == line.RemainingQuantity)
        {
            return line.RemainingAmount;
        }

        var discount = Math.Round(line.DiscountAmount * quantity / line.SoldQuantity, 4, MidpointRounding.AwayFromZero);

        return CommercialAmountCalculator.ComputeLine(
            quantity,
            line.UnitPrice,
            ParseTaxMode(line.TaxMode),
            line.TaxRate,
            discount,
            field: "refundPreview").PayableAmount;
    }

    public static decimal PreviewTotal(SaleRefundPositionDto position, IReadOnlyDictionary<Guid, decimal> quantities)
        => Math.Round(
            position.Lines.Sum(line => PreviewLineTotal(line, quantities.GetValueOrDefault(line.SaleLineId))),
            4,
            MidpointRounding.AwayFromZero);

    /// <summary>"Unspecified" is what a legacy snapshot may read as; the server treats it as Exclusive (D-08).</summary>
    public static CommercialTaxMode ParseTaxMode(string storedTaxMode)
        => string.Equals(storedTaxMode, "Unspecified", StringComparison.OrdinalIgnoreCase)
            ? CommercialTaxMode.Exclusive
            : CommercialAmountCalculator.ParseTaxMode(storedTaxMode, "taxMode");

    /// <summary>What a disposition means in the operator's words, including the line that cannot take stock back.</summary>
    public static string DispositionSentence(SaleRefundLinePositionDto line, RefundDisposition disposition)
    {
        if (!line.MovedStock)
        {
            return "No stock moves for this line - it never took any, so the disposition changes nothing about the shelf.";
        }

        return disposition == RefundDisposition.Restock
            ? $"Back on the shelf at the cost it left at ({Money(line.UnitCost)} each), and the value returns from cost of goods sold."
            : "Written off as damaged: the cost goes to the shrinkage account and no stock returns to the shelf.";
    }

    /// <summary>The sentence beside a line's chosen quantity, naming the server's limit.</summary>
    public static string QuantitySentence(SaleRefundLinePositionDto line, string currency)
        => line.RemainingQuantity <= 0m
            ? $"Nothing left on this line - {Money(line.RefundedQuantity)} of {Money(line.SoldQuantity)} is already back."
            : $"Refundable up to {Money(line.RemainingQuantity)} of {Money(line.SoldQuantity)}, worth {MoneyText(line.RemainingAmount, currency)}.";

    /// <summary>
    /// The refund refusals that prove nothing was committed, so the screen may forget its key and let the operator
    /// start again. Anything else - a lost answer above all - keeps the exact key and body (specification D06,
    /// task 20's rule applied to the refund command).
    /// </summary>
    public static bool CommittedNothing(string? code)
        => code is ApiErrorCodes.ValidationFailed
            or ApiErrorCodes.InsufficientDrawerCash
            or ApiErrorCodes.CashRegisterNotOpen
            or ApiErrorCodes.AlreadyRefunded
            or ApiErrorCodes.RefundNotSupported
            or ApiErrorCodes.PaymentUnconfirmed
            or ApiErrorCodes.AccountingPeriodClosed
            or ApiErrorCodes.Forbidden
            or ApiErrorCodes.PermissionDenied
            or ApiErrorCodes.Unauthenticated
            or ApiErrorCodes.NotFound
            or ApiErrorCodes.SameKeyDifferentPayload;

    /// <summary>The sentence shown while an unresolved refund stands, so no second one can be started.</summary>
    public const string UnresolvedRefundNote =
        "A refund whose answer never arrived keeps its own key: retrying asks the server about that same refund "
        + "instead of paying out again. No new refund can be sent until this one is resolved.";

    /// <summary>What a reprint is, stated the same way wherever the screen offers one (task 21, recipe step 4).</summary>
    public const string ReprintNote =
        "This is a reprint of the snapshot the sale was committed with - prices, tax and product names are the ones "
        + "recorded then, never today's. It is marked COPY: the original document and its numbers are unchanged, and "
        + "a reprint never allocates a second number or posts anything.";

    /// <summary>The drawer rule, stated before an operator chooses one (task 21, recipe step 2).</summary>
    public const string DrawerRuleNote =
        "Only a register of this branch with an open drawer can pay cash back, and the server refuses a payout "
        + "bigger than the cash that drawer holds.";

    /// <summary>Why the screen's total is a preview and not the figure that will be committed.</summary>
    public const string PreviewNote =
        "The screen adds the preview up, but the server decides the figure that is committed, from the sale's own "
        + "snapshots and its residual for the last part.";

    /// <summary>What the screen says when the account may read the sale but not pay money back.</summary>
    public static string RefundAuthorityNote(bool holdsRefundAuthority)
        => holdsRefundAuthority
            ? string.Empty
            : $"Refunding needs the '{Permissions.SalesRefund}' authority, which this account does not hold - the "
                + "history, the reprint and the remaining figures above stay readable with the sale authority. An "
                + "account holding that authority has to pay the cash back.";

    /// <summary>The key for one refund intention, held while the intention stands and re-minted when it is done.</summary>
    public static string NewRefundKey(string saleNumber)
    {
        var slug = new string(saleNumber.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());

        return $"refund-{slug}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Whether the command may be sent at all: a reason a person wrote, a chosen quantity on at least one line, a
    /// drawer with an open session, and the cash payout confirmed on purpose (task 21, recipe step 3).
    /// </summary>
    public static bool CanSubmit(
        string? reason,
        Guid? drawerRegisterId,
        bool payoutConfirmed,
        IReadOnlyDictionary<Guid, decimal> quantities,
        SaleRefundPositionDto position)
        => !string.IsNullOrWhiteSpace(reason)
            && drawerRegisterId is not null
            && payoutConfirmed
            && position.RefundableAtRegister
            && quantities.Any(entry => entry.Value > 0m
                && position.Lines.Any(line => line.SaleLineId == entry.Key && entry.Value <= line.RemainingQuantity));

    /// <summary>What the confirmation has to say before a person can tick it.</summary>
    public static string PayoutSentence(SaleRefundPositionDto position, decimal previewTotal, string? registerName)
        => registerName is null
            ? "Choose the drawer the cash is paid out of; the server refuses a refund that names no open drawer."
            : $"Hand {MoneyText(previewTotal, position.Currency)} of real cash from {registerName} to the customer. "
                + "The server judges the figure, and it refuses a payout the drawer cannot cover.";

    /// <summary>The bound the search box tells the operator about, so a term is never guessed at.</summary>
    public static string SearchHelp(int pageSize)
        => $"Searches the sale number on the server, {pageSize.ToString(CultureInfo.InvariantCulture)} rows at a time - not only what the first page happened to load.";
}
