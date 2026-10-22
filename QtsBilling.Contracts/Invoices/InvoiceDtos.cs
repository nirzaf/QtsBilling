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

public sealed record InvoiceAllocationRequest(Guid InvoiceId, decimal Amount);

public sealed record CreateCustomerPaymentRequest(
    string Method,
    decimal Amount,
    DateOnly ReceivedDate,
    string? Reference,
    IReadOnlyList<InvoiceAllocationRequest>? Allocations);

public sealed record AllocateCustomerPaymentRequest(IReadOnlyList<InvoiceAllocationRequest> Allocations);

public sealed record CustomerPaymentAllocationDto(Guid InvoiceId, decimal Amount);

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
