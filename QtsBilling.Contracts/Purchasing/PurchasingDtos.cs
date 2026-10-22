namespace QtsBilling.Contracts.Purchasing;

/// <summary>Supplier master data (purchasing counterparty).</summary>
public sealed record SupplierDto(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    bool IsActive,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveSupplierRequest(string Name, string? Email, string? Phone, string? Address);

/// <summary>Purchase order lifecycle as seen over the wire (stored as readable text - D-31).</summary>
public enum PurchaseOrderState
{
    Draft,
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled,
}

/// <summary>Ordered line. Stock changes only through receipts, never through the order itself (QB-12).</summary>
public sealed record PurchaseOrderLineRequest(
    Guid VariantId,
    decimal Quantity,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal TaxRate);

public sealed record SavePurchaseOrderRequest(
    Guid SupplierId,
    Guid LocationId,
    DateOnly? OrderDate,
    DateOnly? ExpectedDate,
    string? Note,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string OrderNumber,
    PurchaseOrderState Status,
    Guid SupplierId,
    string? SupplierName,
    Guid LocationId,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Note,
    DateTimeOffset CreatedAtUtc);

public sealed record PurchaseOrderLineDto(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string ProductName,
    string? Sku,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal);

public sealed record PurchaseOrderDetailDto(
    PurchaseOrderSummaryDto Order,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

/// <summary>Receipt (goods received note) or return against purchase order lines.</summary>
public sealed record PurchaseReceiptLineRequest(Guid PurchaseOrderLineId, decimal Quantity);

public sealed record PurchaseReceiptRequest(string? Note, IReadOnlyList<PurchaseReceiptLineRequest> Lines);

public sealed record PurchaseReceiptLineDto(
    Guid Id,
    int LineNo,
    Guid PurchaseOrderLineId,
    Guid VariantId,
    string ProductName,
    string? Sku,
    decimal Quantity,
    decimal UnitCost,
    /// <summary>How much of this delivered line the supplier's bills have already been matched to (task 27), so a
    /// bill can be entered against what is still unbilled rather than against a quantity the ledger has used.</summary>
    decimal QuantityBilled = 0m);

public sealed record PurchaseReceiptDto(
    Guid Id,
    string ReceiptNumber,
    string Kind,
    Guid PurchaseOrderId,
    string OrderNumber,
    Guid LocationId,
    DateTimeOffset ReceivedAtUtc,
    string? Note,
    IReadOnlyList<PurchaseReceiptLineDto> Lines,
    /// <summary>The ledger entry this document produced, numbered as the accounts name it (task 26, recipe
    /// step 3). Null only when the document moved no value, so nothing could post.</summary>
    string? JournalEntryNumber = null,
    /// <summary>The control accounts the entry touched, so the delivery can be followed into the accounts it
    /// moved instead of guessed at.</summary>
    IReadOnlyList<string>? ControlAccountCodes = null);

public sealed record PurchaseOrderActionRequest(string Reason);

/// <summary>One supplier invoice line matched to a previously received goods line.</summary>
public sealed record SupplierBillLineRequest(
    Guid PurchaseReceiptLineId,
    decimal QuantityBilled,
    decimal UnitCost,
    decimal DiscountAmount,
    decimal TaxRate,
    string TaxMode);

public sealed record CreateSupplierBillRequest(
    Guid SupplierId,
    Guid LocationId,
    Guid? PurchaseOrderId,
    string? SupplierInvoiceReference,
    DateOnly BillDate,
    DateOnly? DueDate,
    IReadOnlyList<SupplierBillLineRequest> Lines);

public enum SupplierBillState
{
    Draft,
    Issued,
    PartiallyPaid,
    Paid,
    Void,
}

public sealed record SupplierBillLineDto(
    Guid Id,
    int LineNo,
    Guid PurchaseReceiptLineId,
    Guid VariantId,
    string ProductName,
    decimal QuantityBilled,
    decimal UnitCost,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxRate,
    string TaxMode,
    decimal NetAmount,
    decimal TaxAmount,
    decimal LineTotal,
    /// <summary>What earlier credits have already taken back from this line, so the next one cannot over-credit.</summary>
    decimal QuantityCredited = 0m);

public sealed record SupplierBillDto(
    Guid Id,
    string BillNumber,
    SupplierBillState Status,
    Guid SupplierId,
    Guid LocationId,
    Guid? PurchaseOrderId,
    string? SupplierInvoiceReference,
    DateOnly BillDate,
    DateOnly? DueDate,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal RoundingAmount,
    decimal TotalAmount,
    decimal AmountPaid,
    decimal BalanceDue,
    Guid? JournalEntryId,
    IReadOnlyList<SupplierBillLineDto> Lines,
    /// <summary>Names rather than keys, because R07 forbids asking an operator to read a GUID (task 27).</summary>
    string? SupplierName = null,
    string? LocationName = null,
    string? OrderNumber = null,
    string? JournalEntryNumber = null,
    decimal CreditedAmount = 0m,
    IReadOnlyList<SupplierCreditDto>? Credits = null);

public sealed record SupplierBillActionRequest(string? Note);

/// <summary>
/// One settlement target on the payable side. Exactly one of a bill or an imported opening payable may be named
/// (specification task 27, recipe step 2): paying an opening is a real event for a business that started with debts,
/// and it must not require a fabricated supplier bill.
/// </summary>
/// <param name="SupplierBillId">The bill being settled, when the target is a bill.</param>
/// <param name="OpenItemId">The imported opening payable, when the target is an opening.</param>
/// <param name="Amount">How much of the payment settles this target.</param>
public sealed record SupplierAllocationTarget(
    Guid? SupplierBillId,
    Guid? OpenItemId,
    decimal Amount);

public sealed record CreateSupplierPaymentRequest(
    Guid SupplierId,
    string Method,
    decimal Amount,
    DateOnly PaidOn,
    string Currency,
    string? Reference,
    IReadOnlyList<SupplierAllocationTarget> Allocations);

public sealed record SupplierPaymentDto(
    Guid Id,
    Guid SupplierId,
    string Method,
    decimal Amount,
    string Currency,
    DateOnly PaidOn,
    string? Reference,
    Guid JournalEntryId,
    IReadOnlyList<SupplierAllocationTarget> Allocations,
    string? JournalEntryNumber = null);

/// <summary>A credit line as an operator offers it: which bill line, how much of it, and the return that proves the goods went back.</summary>
public sealed record SupplierCreditLineRequest(
    Guid SupplierBillLineId,
    decimal Quantity,
    Guid? PurchaseReceiptLineId);

/// <summary>
/// A supplier credit against an issued bill (specification task 27, recipe step 3). The reason is mandatory because
/// it is audited with who issued the credit, and a credit that follows a posted return names it, so the same
/// quantity is never taken out of stock twice.
/// </summary>
public sealed record CreateSupplierCreditRequest(
    string Reason,
    Guid? SourcePurchaseReceiptId,
    IReadOnlyList<SupplierCreditLineRequest> Lines);

public sealed record SupplierCreditLineDto(
    Guid Id,
    int LineNo,
    Guid SupplierBillLineId,
    string ProductName,
    decimal Quantity,
    decimal UnitCost,
    decimal BaseAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal ValueAmount,
    /// <summary>The goods receipt line whose capitalised cost the entry clears, when the credit follows a return.</summary>
    Guid? PurchaseReceiptLineId,
    /// <summary>What is left of the bill line after this credit, so the next one cannot exceed it.</summary>
    decimal RemainingQuantity);

public sealed record SupplierCreditDto(
    Guid Id,
    string CreditNumber,
    Guid SupplierBillId,
    string BillNumber,
    Guid SupplierId,
    Guid LocationId,
    Guid? SourcePurchaseReceiptId,
    string? SourceReceiptNumber,
    string Reason,
    string Currency,
    decimal BaseAmount,
    decimal TaxAmount,
    decimal ValueAmount,
    decimal AppliedAmount,
    /// <summary>The credit's own unused balance, stated apart from the payable it came from (T27.04).</summary>
    decimal RemainingAmount,
    Guid JournalEntryId,
    string? JournalEntryNumber,
    IReadOnlyList<string>? ControlAccountCodes,
    DateTimeOffset IssuedAtUtc,
    IReadOnlyList<SupplierCreditLineDto> Lines);

/// <summary>Use of a credit's remaining value against another bill or an opening payable.</summary>
public sealed record ApplySupplierCreditRequest(Guid? SupplierBillId, Guid? OpenItemId, decimal Amount);

/// <summary>One event on a supplier's account: a bill, a credit, a settlement or an opening.</summary>
public sealed record SupplierStatementLineDto(
    string Kind,
    Guid Id,
    string Reference,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    decimal DebitAmount,
    decimal CreditAmount,
    decimal RunningBalance,
    string? Note);

/// <summary>An imported opening payable with its derived, never patched, remaining amount.</summary>
public sealed record SupplierOpenItemDto(
    string TargetKind,
    Guid Id,
    string Reference,
    DateOnly DocumentDate,
    DateOnly? DueDate,
    decimal OriginalAmount,
    decimal SettledAmount,
    decimal RemainingAmount,
    string Currency,
    bool IsPastDue,
    string? Note);

/// <summary>
/// One supplier's account read as at a business date (specification task 27, recipe step 5): documents still owed,
/// credits held and their unused balance stated apart from the payable, settlements, and the control-account and
/// reconciliation figures the payables screen quotes back to the operator.
/// </summary>
public sealed record SupplierStatementDto(
    Guid SupplierId,
    string SupplierName,
    string Currency,
    DateOnly AsOf,
    DateTimeOffset GeneratedAtUtc,
    decimal OpeningBalance,
    decimal MovementDebit,
    decimal MovementCredit,
    decimal ClosingBalance,
    decimal UnappliedCredits,
    decimal PayableControlBalance,
    decimal GrniBalance,
    int OpenDocumentCount,
    bool Truncated,
    IReadOnlyList<SupplierStatementLineDto> Lines,
    IReadOnlyList<SupplierOpenItemDto> OpenItems,
    IReadOnlyList<SupplierCreditDto> Credits,
    int? SettlementScale = null,
    string? TimeZoneId = null);

