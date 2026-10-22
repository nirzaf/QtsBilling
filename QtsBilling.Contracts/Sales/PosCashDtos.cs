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

/// <summary>
/// What kind of unresolved money a drawer's custody position names (specification D07, task 19). Each kind is
/// cleared by different evidence, so they are never merged into one number: a payment the server recorded but
/// never confirmed, an operation a terminal pushed that awaits review, or the custody a terminal holds that the
/// server cannot count until it is pushed and that therefore has to be declared by a person.
/// </summary>
public enum CashCustodySourceState
{
    ProvisionalPayment,
    DeviceOperation,
    DeviceCustody,
}

/// <summary>One unresolved cash item, as the register workbench shows it before anything is closed.</summary>
/// <param name="Kind">Which kind of obligation this is.</param>
/// <param name="SourceId">The payment, operation or device this item names.</param>
/// <param name="Reference">What an operator can read - a sale number, an operation id, a terminal name.</param>
/// <param name="Amount">The money at stake. Zero is only ever stated for an obligation whose amount the server cannot see, and the explanation says so.</param>
/// <param name="Currency">The session's currency; a carry-forward may not change it.</param>
/// <param name="DeviceId">The terminal the obligation came from, when it came from one.</param>
/// <param name="DeviceName">The terminal's name, so a custodian is handed a thing a person can identify.</param>
/// <param name="DeviceLastSeenAtUtc">When the terminal last spoke to the server. Absence is not proof of zero.</param>
/// <param name="Explanation">Why this item is unresolved, in the operator's words.</param>
public sealed record UnresolvedCashItemDto(
    CashCustodySourceState Kind,
    Guid SourceId,
    string Reference,
    decimal Amount,
    string Currency,
    Guid? DeviceId,
    string? DeviceName,
    DateTimeOffset? DeviceLastSeenAtUtc,
    string Explanation);

/// <summary>What the server can see about one terminal assigned to this register's branch.</summary>
public sealed record DeviceCustodyEvidenceDto(
    Guid DeviceId,
    string Name,
    string Status,
    DateTimeOffset? LastSeenAtUtc,
    bool ContactedSinceSessionOpened,
    int OperationsAwaitingReview,
    decimal AmountAwaitingReview,
    string Explanation);

/// <summary>One recorded carry-forward row: who now owns the duty to account for one unresolved amount.</summary>
public sealed record CashCustodyTransferDto(
    string CarryForwardNumber,
    CashCustodySourceState Kind,
    Guid SourceId,
    string Reference,
    decimal Amount,
    string Currency,
    string Reason,
    Guid ReceivingCustodianUserId,
    string ReceivingCustodianName,
    Guid AuthorisedByUserId,
    string AuthorisedByName,
    DateTimeOffset CreatedAtUtc);

/// <summary>One item of a carry-forward. The amount is what the custodian accepts, and it may not understate
/// money the server can already see for that operation (specification D07).</summary>
public sealed record CustodyTransferItemRequest(CashCustodySourceState Kind, Guid SourceId, decimal Amount);

/// <summary>An authorised carry-forward document: every unresolved item it names, the reason, and who receives them.</summary>
public sealed record CreateCustodyTransferRequest(
    Guid ReceivingCustodianUserId,
    string Reason,
    IReadOnlyList<CustodyTransferItemRequest> Items);

/// <summary>An account of this business that may be named as the receiving custodian of carried cash.</summary>
/// <param name="UserId">Chosen from the list; never typed by an operator.</param>
/// <param name="Name">The address that identifies the person on screen.</param>
/// <param name="Role">The role they hold in this business, so the hand-over is made to somebody with a position.</param>
public sealed record CashCustodianDto(Guid UserId, string Name, string Role);

/// <summary>
/// The custody position of one drawer: the derived arithmetic, what is still unresolved, what has already been
/// carried forward, and whether the session may be closed at all (specification D07, task 19).
/// </summary>
public sealed record DrawerCustodyDto(
    Guid SessionId,
    Guid CashRegisterId,
    string Currency,
    decimal ExpectedCash,
    decimal ProvisionalCash,
    IReadOnlyList<UnresolvedCashItemDto> Unresolved,
    IReadOnlyList<DeviceCustodyEvidenceDto> Devices,
    IReadOnlyList<CashCustodyTransferDto> CarryForwards,
    decimal UncoveredAmount,
    bool Closable,
    string? BlockReason);

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
    decimal LineTotal,
    /// <summary>How the tax was composed into the price (Exclusive, Inclusive or Exempt).</summary>
    string TaxMode = "Exclusive");

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
/// Refund of a posted cash sale (QB-10/QB-11). Omitted lines refund all remaining quantities using Restock.
/// </summary>
public sealed record RefundSaleRequest(
    string Reason,
    string? Note,
    Guid RegisterId,
    IReadOnlyList<RefundLineRequest>? Lines = null);

public enum RefundDisposition
{
    Restock,
    DamagedWriteOff,
}

public sealed record RefundLineRequest(Guid SaleLineId, decimal Quantity, RefundDisposition Disposition = RefundDisposition.Restock);

public sealed record RefundLineDto(
    Guid SaleLineId,
    int LineNo,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxAmount,
    decimal LineTotal,
    decimal UnitCost,
    string Disposition = "Restock");

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
