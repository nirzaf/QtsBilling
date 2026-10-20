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
    IReadOnlyList<InvoiceLineRequest> Lines);

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
    DateTimeOffset? VoidedAtUtc);

public sealed record InvoiceLineDto(
    int LineNo,
    Guid? VariantId,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal);

public sealed record InvoicePaymentDto(
    Guid Id,
    string Method,
    string Status,
    decimal Amount,
    string Currency,
    string? Reference,
    DateTimeOffset ReceivedAtUtc);

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
    IReadOnlyList<InvoiceCreditNoteDto>? CreditNotes = null);

/// <summary>
/// Payment allocation against an issued invoice. Cash may name a register with an open drawer session;
/// cheque/bank transfers require a processing reference (card is disabled - D-36).
/// </summary>
public sealed record InvoicePaymentRequest(string Method, decimal Amount, string? Reference, Guid? RegisterId);

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
