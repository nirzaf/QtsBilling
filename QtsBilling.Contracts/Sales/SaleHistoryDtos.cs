namespace QtsBilling.Contracts.Sales;

/// <summary>
/// One server-posted sale as the history workbench lists it (specification task 21, recipe step 1). The row is the
/// server's own record: a device's provisional local capture is never in here, and what the counter may or may not
/// treat as settled money is carried explicitly by <paramref name="PaymentStatus"/> rather than being implied by
/// the row's existence.
/// </summary>
/// <param name="SaleId">The sale's identity; an operator navigates by <see cref="SaleNumber"/>, never by this.</param>
/// <param name="SaleNumber">The business's own allocated number, which is what a receipt and a till roll show.</param>
/// <param name="Status">Posted, reversed or void - the server's state, not the screen's guess.</param>
/// <param name="PaymentStatus">Confirmed, refunded or still pending. A pending payment is uncollected money and the screen has to say so.</param>
/// <param name="PaymentMethod">Cash, card or transfer, as the server recorded it.</param>
/// <param name="LocationId">The branch that sold it, used to bound the drawer list.</param>
/// <param name="LocationName">The branch's name, because an operator chooses a branch by name (requirement R07).</param>
/// <param name="CustomerId">The customer the sale was written against, when there was one.</param>
/// <param name="CustomerName">The customer's name as the server holds it.</param>
/// <param name="Currency">The sale's currency; no cross-currency arithmetic happens here.</param>
/// <param name="TotalAmount">The payable total as posted, at the server's scale.</param>
/// <param name="TaxAmount">The tax inside or on top of that total, as posted.</param>
/// <param name="RefundedAmount">Cash already refunded against this sale - the cumulative figure task 03 owns.</param>
/// <param name="RefundCount">How many refund documents that figure came from.</param>
/// <param name="PostedAtUtc">The instant the server committed the sale.</param>
/// <param name="PostedAtLocal">The same instant in the business's own time zone, formatted by the server.</param>
/// <param name="CashierName">Who rang it up, from the sale's own snapshot.</param>
public sealed record SaleHistoryDto(
    Guid SaleId,
    string SaleNumber,
    SaleState Status,
    PaymentStatus PaymentStatus,
    string PaymentMethod,
    Guid LocationId,
    string LocationName,
    Guid? CustomerId,
    string? CustomerName,
    string Currency,
    decimal TotalAmount,
    decimal TaxAmount,
    decimal RefundedAmount,
    int RefundCount,
    DateTimeOffset PostedAtUtc,
    string PostedAtLocal,
    string? CashierName);

/// <summary>
/// One line of a sale as the refund workbench must see it (specification task 21, recipe step 2): what was sold,
/// what has already gone back, and what is left to refund - with the original unit cost, because the money the
/// drawer pays out and the value the stock takes back are decided by the server's snapshots and never by a figure
/// the browser computes.
/// </summary>
/// <param name="RefundedQuantity">What the refund lines already posted against this line add up to.</param>
/// <param name="RemainingQuantity">The only quantity a further refund may ask for; more is refused by the server.</param>
/// <param name="DiscountAmount">The discount as posted, which the server prorates across a partial refund.</param>
/// <param name="RemainingAmount">The money still refundable on this line, computed by the server from its own
/// snapshots - the residual of the posted total less what has already been paid back.</param>
/// <param name="UnitCost">The cost the line was valued at when it was sold, which is what a restock takes back.</param>
/// <param name="MovedStock">False for a service or a line that never took stock, which cannot be restocked.</param>
public sealed record SaleRefundLinePositionDto(
    Guid SaleLineId,
    int LineNo,
    string ProductName,
    string? Sku,
    decimal SoldQuantity,
    decimal RefundedQuantity,
    decimal RemainingQuantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal,
    decimal RemainingAmount,
    decimal UnitCost,
    bool MovedStock,
    string TaxMode);

/// <summary>
/// A drawer a refund could be paid out of. The expected cash is the server's derived figure for the open session,
/// so the operator sees whether the payout is physically payable before the money is promised (task 19's D07 rule,
/// applied to the refund side).
/// </summary>
public sealed record RefundDrawerOptionDto(
    Guid RegisterId,
    string RegisterName,
    Guid? OpenSessionId,
    decimal? ExpectedCash,
    decimal? AvailableForRefund,
    string Currency,
    string Status);

/// <summary>
/// The server's authoritative refund position for one sale: the cumulative money and quantities already refunded,
/// what remains, whether a register refund is possible at all and why not, and the drawers that could pay it
/// (specification task 21, recipe step 2). Everything a screen shows as a limit comes from here; the screen's own
/// totals are labelled previews.
/// </summary>
public sealed record SaleRefundPositionDto(
    Guid SaleId,
    string SaleNumber,
    string Status,
    string Currency,
    decimal TotalAmount,
    decimal TaxAmount,
    decimal RefundedAmount,
    decimal RemainingRefundableAmount,
    string PaymentMethod,
    string PaymentStatus,
    bool RefundableAtRegister,
    string? RefusalCode,
    string? RefusalReason,
    IReadOnlyList<SaleRefundLinePositionDto> Lines,
    IReadOnlyList<RefundDrawerOptionDto> Drawers);
