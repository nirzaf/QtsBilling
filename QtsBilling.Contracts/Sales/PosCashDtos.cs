namespace QtsBilling.Contracts.Sales;

/// <summary>Cash register configured for a location (QB-10).</summary>
public sealed record CashRegisterDto(
    Guid Id,
    Guid LocationId,
    string Name,
    bool IsActive,
    CashRegisterSessionDto? CurrentSession);

public sealed record CreateCashRegisterRequest(Guid LocationId, string Name);

/// <summary>Drawer session lifecycle as seen over the wire.</summary>
public enum CashRegisterSessionState
{
    Open,
    Closed,
}

/// <summary>Cash movement kinds; the sign of the drawer effect is implied by the kind (QB-10).</summary>
public enum CashMovementKind
{
    Opening,
    SalePayment,
    CashIn,
    CashOut,
    Refund,
    Closing,
}

/// <summary>Cash drawer session; expected cash is always derived from append-only movements.</summary>
public sealed record CashRegisterSessionDto(
    Guid Id,
    Guid CashRegisterId,
    string Currency,
    CashRegisterSessionState Status,
    decimal OpeningFloat,
    DateTimeOffset OpenedAtUtc,
    Guid OpenedByUserId,
    DateTimeOffset? ClosedAtUtc,
    Guid? ClosedByUserId,
    decimal? CountedCash,
    decimal? ExpectedCash,
    decimal? Variance,
    string? ClosingNote);

public sealed record OpenCashRegisterSessionRequest(decimal OpeningFloat, string? Note);

/// <summary>Manual drawer movement: <c>cashIn</c> (paid in) or <c>cashOut</c> (paid out); both require a reason.</summary>
public sealed record CashMovementRequest(string MovementType, decimal Amount, string? Reason);

public sealed record CashMovementDto(
    Guid Id,
    CashMovementKind MovementType,
    decimal Amount,
    string Currency,
    string? Reason,
    Guid? SaleId,
    Guid? CreatedByUserId,
    DateTimeOffset OccurredAtUtc);

/// <summary>Derived drawer arithmetic for a session: every component comes from movements, never from mutable state.</summary>
public sealed record CashSessionBreakdownDto(
    decimal OpeningFloat,
    decimal SaleCash,
    decimal CashIn,
    decimal CashOut,
    decimal Refunds,
    decimal ClosingCount);

public sealed record CashSessionDetailDto(
    CashRegisterSessionDto Session,
    string RegisterName,
    Guid LocationId,
    decimal ExpectedCash,
    decimal ProvisionalCash,
    CashSessionBreakdownDto Breakdown,
    IReadOnlyList<CashMovementDto> Movements);

public sealed record CloseCashRegisterSessionRequest(decimal CountedCash, string? Note);

/// <summary>Printed/emailed receipt content; values are the immutable sale snapshots (QB-10, spec §3.6).</summary>
public sealed record SaleReceiptLineDto(
    int LineNo,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal);

public sealed record SaleReceiptPaymentDto(
    string Method,
    string Status,
    decimal Amount,
    decimal? CashReceived,
    decimal? Change,
    string? Reference);

public sealed record SaleReceiptDto(
    Guid SaleId,
    string SaleNumber,
    string ReceiptState,
    string? ProvisionalReference,
    string BusinessName,
    string? BusinessLegalName,
    string? LocationName,
    string? LocationAddress,
    string Currency,
    DateTimeOffset PostedAtUtc,
    string PostedAtLocal,
    string? CashierName,
    string? RegisterName,
    Guid? CashRegisterSessionId,
    IReadOnlyList<SaleReceiptLineDto> Lines,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    IReadOnlyList<SaleReceiptPaymentDto> Payments);

/// <summary>
/// Full refund of a posted cash sale (QB-10). Refunds are tied to the original confirmed payment, return
/// the stock and post the reversal entry; partial refunds and credit notes belong to QB-11.
/// </summary>
public sealed record RefundSaleRequest(string Reason, string? Note, Guid RegisterId);

public sealed record RefundLineDto(
    Guid SaleLineId,
    int LineNo,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxAmount,
    decimal LineTotal,
    decimal UnitCost);

public sealed record RefundDto(
    Guid RefundId,
    string RefundNumber,
    Guid SaleId,
    string SaleNumber,
    decimal Amount,
    string Currency,
    string Method,
    string Reason,
    string? Note,
    DateTimeOffset PostedAtUtc,
    Guid PostedByUserId,
    Guid? CashRegisterSessionId,
    string? JournalEntryNumber,
    IReadOnlyList<RefundLineDto> Lines);
