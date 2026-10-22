namespace QtsBilling.Contracts.Invoices;

/// <summary>Invoice lifecycle as seen over the wire (stored as readable text - D-31).</summary>
public enum InvoiceState
{
    Draft,
    Issued,
    PartiallyPaid,
    Paid,
    Void,
}

/// <summary>The sole economic recognition source for an invoice.</summary>
public enum InvoiceRecognitionMode
{
    Service,
    DirectGoods,
    SaleRepresentation,
    Delivery,
    HistoricalUnknown,
}

/// <summary>
/// Draft line. When <see cref="VariantId"/> is set the server snapshots the catalog name/SKU; ad-hoc lines
/// (services) require an explicit <see cref="ProductName"/> (spec §3.2/§3.5).
/// </summary>
public sealed record InvoiceLineRequest(
    Guid? VariantId,
    string? ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate);

/// <summary>Create/update payload for a draft invoice; totals are computed server-side, never trusted.</summary>
public sealed record SaveInvoiceRequest(
    Guid? LocationId,
    Guid? CustomerId,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string? Note,
    IReadOnlyList<InvoiceLineRequest> Lines,
    InvoiceRecognitionMode? RecognitionMode = null,
    Guid? SourceSaleId = null,
    long? ExpectedVersion = null);

public sealed record CreateSaleInvoiceRequest(Guid SaleId);

public sealed record InvoiceSummaryDto(
    Guid Id,
    string? InvoiceNumber,
    InvoiceState Status,
    Guid? CustomerId,
    string? CustomerName,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal CreditedAmount,
    decimal BalanceDue,
    string? Note,
    DateTimeOffset? IssuedAtUtc,
    DateTimeOffset? VoidedAtUtc,
    long Version = 0,
    InvoiceRecognitionMode RecognitionMode = InvoiceRecognitionMode.Service,
    Guid? SourceSaleId = null,
    Guid? SourceOrderId = null);

public sealed record InvoiceLineDto(
    Guid Id,
    int LineNo,
    Guid? VariantId,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal,
    /// <summary>Immutable tax composition of the line (Exclusive, Inclusive or Exempt); later credits follow it.</summary>
    string TaxMode = "Exclusive",
    /// <summary>Immutable revenue base of the line (payable amount minus the tax it carries).</summary>
    decimal RevenueBaseAmount = 0m);

public sealed record InvoicePaymentDto(
    Guid Id,
    string Method,
    string Status,
    decimal Amount,
    string Currency,
    string? Reference,
    DateTimeOffset ReceivedAtUtc,
    string? PspReference = null,
    string? CardBrand = null,
    string? CardLast4 = null,
    /// <summary>When a cheque or transfer was confirmed as collected money; null while it still awaits clearance.</summary>
    DateTimeOffset? ClearedAtUtc = null,
    /// <summary>When a tender was returned unpaid, and why - an event in the document's own history.</summary>
    DateTimeOffset? BouncedAtUtc = null,
    string? BounceReason = null);

/// <summary>Full credit note referencing the original invoice (QB-11); one per invoice.</summary>
public sealed record InvoiceCreditNoteDto(
    Guid Id,
    string CreditNumber,
    decimal Amount,
    string Reason,
    string? Note,
    DateTimeOffset IssuedAtUtc,
    string? JournalEntryNumber,
    IReadOnlyList<InvoiceCreditNoteLineDto>? Lines = null);

public sealed record InvoiceCreditNoteLineDto(
    Guid InvoiceLineId,
    int LineNo,
    Guid? VariantId,
    decimal QuantityCredited,
    decimal UnitPrice,
    decimal TaxAmount,
    decimal LineTotal,
    bool RestockInventory,
    decimal UnitCostRestocked);

public sealed record InvoiceDetailDto(
    InvoiceSummaryDto Invoice,
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<InvoicePaymentDto> Payments,
    InvoiceCreditNoteDto? CreditNote,
    IReadOnlyList<InvoiceCreditNoteDto>? CreditNotes = null,
    Guid? LocationId = null,
    InvoicePartySnapshotDto? PartySnapshot = null,
    InvoicePresentationDto? Presentation = null,
    InvoiceSourceDto? Source = null,
    /// <summary>Tenders recorded but not yet confirmed as collected money, in the document's own currency.</summary>
    decimal AwaitingClearanceAmount = 0m);

/// <summary>
/// How this document must be read, projected by the server that owns the values (specification task 24, recipe
/// step 4 - finding F21). A screen may not assume two decimal places or its own machine's clock: the settlement
/// scale belongs to the business, its time zone decides what "today" is, and the amounts themselves keep the
/// precision they were stored with. These are presentation facts only - the money never changes by reading them.
/// </summary>
/// <param name="SettlementScale">Decimal places the business settles in (0, 2, 3 or 4), or null when the business
/// has never been given accounting settings; a screen then shows the stored precision instead of inventing one.</param>
/// <param name="TimeZoneId">The business time zone identifier, or null when the business has none.</param>
/// <param name="BusinessToday">The business date the document is being read on, in that zone.</param>
public sealed record InvoicePresentationDto(
    int? SettlementScale,
    string? TimeZoneId,
    DateOnly BusinessToday);

/// <summary>
/// The document this invoice was made from, named the way an operator knows it. A GUID alone is not a link a
/// human can follow, and R07 forbids asking them to work with one, so the number travels with the identifier
/// the route needs.
/// </summary>
/// <param name="Kind">Which workflow owns the source: <c>sale</c> or <c>order</c>.</param>
/// <param name="Number">The source document's own number, or null when it predates numbering.</param>
public sealed record InvoiceSourceDto(string Kind, Guid Id, string? Number);

public sealed record InvoicePartySnapshotDto(
    string BusinessName,
    string? BusinessLegalName,
    string? LocationAddress,
    string? CustomerName,
    string? CustomerAddress,
    string? CustomerPhone,
    string? CustomerEmail,
    int? PaymentTermsDays);

/// <summary>
/// Payment allocation against an issued invoice. Cash may name a register with an open drawer session;
/// cheque/bank transfers require a processing reference (card is disabled - D-36).
/// </summary>
public sealed record InvoicePaymentRequest(
    string Method,
    decimal Amount,
    string? Reference,
    Guid? RegisterId,
    QtsBilling.Contracts.Sales.TerminalCaptureEnvelope? TerminalCapture = null);

public sealed record InvoicePaymentResult(InvoicePaymentDto Payment, InvoiceSummaryDto Invoice);

public sealed record PaymentBounceRequest(string Reason);

public sealed record PaymentTransitionDto(
    Guid PaymentId,
    string Status,
    DateTimeOffset? ClearedAtUtc,
    DateTimeOffset? BouncedAtUtc,
    string? BounceReason);

/// <summary>
/// One application of a customer's money to exactly one settleable document (specification task 25, recipe step 2):
/// either an issued invoice or an opening receivable the native import brought in. Both identifiers are nullable so
/// the choice is visible on the wire, and the server accepts a target that names exactly one of them - never both
/// (one payment could then be counted against two documents) and never neither (the money would leave the deposit
/// and settle nothing). The shape replaces an invoice-only allocation because an imported opening is real
/// receivable money: refusing to settle it would push operators to invent a fake invoice for it, which is how a
/// customer ends up charged twice.
/// </summary>
public sealed record AllocationTarget(Guid? InvoiceId, Guid? OpenItemId, decimal Amount)
{
    /// <summary>What this target settles, as an operator would name it.</summary>
    public string TargetKind => InvoiceId is not null ? "invoice" : OpenItemId is not null ? "opening" : "nothing";
}

/// <summary>
/// Money a customer left on deposit and had handed back unapplied (task 25, recipe step 4). Each row is an
/// immutable event with its own reversal journal, reason, settlement route and stable command key.
/// </summary>
public sealed record DepositRefundDto(
    Guid Id,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string Reason,
    string Method,
    string? BankReference,
    Guid? CashRegisterSessionId,
    string? JournalEntryNumber,
    DateTimeOffset OccurredAtUtc,
    decimal RemainingUnapplied);

/// <summary>Command: give unapplied deposit money back to the customer.</summary>
/// <param name="Amount">At most the receipt's remaining unapplied figure; the server refuses beyond it.</param>
/// <param name="Reason">Mandatory, audited with the operator who authorised it.</param>
/// <param name="Method"><c>cash</c> names a drawer with an open session; <c>bankTransfer</c> names the settlement reference the bank used.</param>
public sealed record RefundUnallocatedRequest(
    decimal Amount,
    string Reason,
    string Method,
    Guid? RegisterId,
    string? BankReference);

/// <summary>
/// One line of a customer's statement: a document that raised money owed, or an event that moved it back. The
/// running balance is the server's own arithmetic over the immutable events, never a figure a screen recomputes.
/// </summary>
/// <param name="Kind">Which fact this row is: <c>opening</c>, <c>invoice</c>, <c>receipt</c>, <c>application</c>, <c>credit_note</c> or <c>refund</c>.</param>
/// <param name="Reference">The number the customer knows the document by, never a key they cannot read.</param>
/// <param name="DebitAmount">Money this event added to what the customer owes.</param>
/// <param name="CreditAmount">Money this event took back from what the customer owes.</param>
/// <param name="BalanceAfter">The customer's balance immediately after this event, as of the requested date.</param>
public sealed record CustomerStatementLineDto(
    Guid Id,
    string Kind,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    string Reference,
    string? Note,
    decimal DebitAmount,
    decimal CreditAmount,
    decimal BalanceAfter,
    string Currency);

/// <summary>
/// A customer's account as at a chosen business date (task 25, recipe step 5): the movements in order, the
/// balance the customer carried at the start and at the end, the money still sitting unapplied on deposit, and
/// the documents that are open. Statement totals tie to the posted events and the configured receivable control,
/// so a figure on paper can be traced to the ledger row that produced it.
/// </summary>
public sealed record CustomerStatementDto(
    Guid CustomerId,
    string CustomerName,
    string Currency,
    DateOnly AsOfDate,
    DateTimeOffset GeneratedAtUtc,
    decimal OpeningBalance,
    decimal MovementDebit,
    decimal MovementCredit,
    decimal ClosingBalance,
    decimal UnappliedDeposits,
    int OpenDocumentCount,
    bool Truncated,
    IReadOnlyList<CustomerStatementLineDto> Lines,
    IReadOnlyList<CustomerDepositDto>? Deposits = null,
    int? SettlementScale = null,
    string? TimeZoneId = null);

/// <summary>
/// One document the customer still has not fully settled - an issued invoice or an imported opening receivable -
/// with what it was raised at, what has been applied to it and what is left. The remaining figure is derived from
/// the applications, never patched onto the original, so a document's history stays exactly as it was written.
/// </summary>
public sealed record CustomerOpenItemDto(
    string TargetKind,
    Guid Id,
    string Reference,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    decimal OriginalAmount,
    decimal AppliedAmount,
    decimal RemainingAmount,
    string Currency,
    bool PastDue,
    string? Note);

public sealed record CreateCustomerPaymentRequest(
    string Method,
    decimal Amount,
    DateOnly ReceivedDate,
    string? Reference,
    IReadOnlyList<AllocationTarget>? Allocations);

public sealed record AllocateCustomerPaymentRequest(IReadOnlyList<AllocationTarget> Allocations);

public sealed record CustomerPaymentAllocationDto(Guid? InvoiceId, Guid? OpenItemId, decimal Amount);

/// <summary>
/// Money a customer has on deposit with this business: a receipt that was taken in their name and the part of it
/// that has not been applied to any document yet. The unapplied remainder is a liability the business holds for
/// them - it is not revenue, and it is not a negative receivable dressed up as a paid invoice (task 25, recipe
/// step 3). The refunds listed against it are the immutable record of what has already been handed back.
/// </summary>
public sealed record CustomerDepositDto(
    Guid PaymentId,
    string Method,
    string Status,
    decimal Amount,
    decimal UnallocatedAmount,
    string Currency,
    string? Reference,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<DepositRefundDto> Refunds);

public sealed record CustomerPaymentDto(
    Guid Id,
    Guid CustomerId,
    string Method,
    string Status,
    decimal Amount,
    decimal UnallocatedAmount,
    string Currency,
    string? Reference,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<CustomerPaymentAllocationDto> Allocations);

public sealed record InvoiceVoidRequest(string Reason, string? Note);

public sealed record InvoiceCreditLineRequest(Guid InvoiceLineId, decimal Quantity, bool RestockInventory = false);

public sealed record InvoiceCreditNoteRequest(
    string Reason,
    string? Note,
    IReadOnlyList<InvoiceCreditLineRequest>? Lines = null);
