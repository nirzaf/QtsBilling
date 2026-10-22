namespace QtsBilling.Persistence.Postgres.Entities;

public enum SupplierBillStatus
{
    Draft,
    Issued,
    PartiallyPaid,
    Paid,
    Void,
}

/// <summary>Purchase order header. Ordered quantities are separate from received quantities (QB-12).</summary>
public sealed class PurchaseOrder : TenantEntity
{
    public Guid SupplierId { get; set; }

    public Guid LocationId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public DateOnly OrderDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Note { get; set; }
}

public sealed class PurchaseOrderLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid PurchaseOrderId { get; set; }

    public int LineNo { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal QuantityOrdered { get; set; }

    public decimal QuantityReceived { get; set; }

    public decimal UnitCost { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }
}

/// <summary>Business expense; posts to the ledger through a controlled service path (QB-12).</summary>
public sealed class Expense : TenantEntity
{
    public Guid? ExpenseCategoryId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? SupplierId { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Input-tax snapshot included in the paid amount; the tax mode and rate never follow mutable settings.</summary>
    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public string TaxMode { get; set; } = "Exclusive";

    public string Currency { get; set; } = "QAR";

    public DateOnly OccurredOn { get; set; }

    public PaymentMethod PaidVia { get; set; } = PaymentMethod.Cash;

    public string? Reference { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Idempotency receipt for the expense write (QB-12); unique per business when set.</summary>
    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>
/// Goods received note or return against purchase order lines; one document per delivery event, so a
/// retried receipt can return the stored document instead of increasing stock twice (QB-12).
/// </summary>
public sealed class PurchaseReceipt : TenantEntity
{
    public Guid PurchaseOrderId { get; set; }

    public Guid LocationId { get; set; }

    public string ReceiptNumber { get; set; } = string.Empty;

    public PurchaseReceiptKind Kind { get; set; } = PurchaseReceiptKind.Receipt;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public Guid ReceivedByUserId { get; set; }

    public string? Note { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Quantity and cost snapshot of one received/returned purchase order line (append-only).</summary>
public sealed class PurchaseReceiptLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid PurchaseReceiptId { get; set; }

    public int LineNo { get; set; }

    public Guid PurchaseOrderLineId { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }
}

/// <summary>Supplier invoice matched to received goods and posted to the AP subledger.</summary>
public sealed class SupplierBill : TenantEntity
{
    public Guid SupplierId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public string BillNumber { get; set; } = string.Empty;
    public string? SupplierInvoiceReference { get; set; }
    public SupplierBillStatus Status { get; set; } = SupplierBillStatus.Draft;
    public DateOnly BillDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = "QAR";
    public decimal SubtotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal RoundingAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset? IssuedAtUtc { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PayloadHash { get; set; }
}

/// <summary>Immutable commercial and receipt-matching snapshot for one supplier bill line.</summary>
public sealed class SupplierBillLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }
    public Guid SupplierBillId { get; set; }
    public int LineNo { get; set; }
    public Guid PurchaseReceiptLineId { get; set; }
    public Guid VariantId { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public decimal QuantityBilled { get; set; }
    public decimal UnitCost { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal NetAmount { get; set; }
    public string TaxMode { get; set; } = "Exclusive";
}

/// <summary>Cash or bank settlement allocated to one or more supplier bills.</summary>
public sealed class SupplierPayment : TenantEntity
{
    public Guid SupplierId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "QAR";
    public DateOnly PaidOn { get; set; }
    public string? Reference { get; set; }
    public Guid JournalEntryId { get; set; }
}

/// <summary>
/// Append-only allocation of a supplier settlement to one AP open item: an issued bill, or an imported opening
/// payable settled without inventing a bill for it (specification task 27, recipe step 2).
/// </summary>
public sealed class SupplierPaymentAllocation : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }
    public Guid SupplierPaymentId { get; set; }

    /// <summary>Null when the target is an opening payable rather than a bill.</summary>
    public Guid? SupplierBillId { get; set; }

    /// <summary>Exactly one of the two targets is set - a database check, not a convention.</summary>
    public Guid? OpenItemId { get; set; }

    public decimal AllocatedAmount { get; set; }
}

/// <summary>
/// One-time reversal of a posted expense; append-only so a double reversal request is refused with
/// <c>ALREADY_REVERSED</c> and a retried request replays the stored outcome (QB-12).
/// </summary>
public sealed class ExpenseReversal : TenantEntity
{
    public Guid ExpenseId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    public Guid JournalEntryId { get; set; }

    public DateTimeOffset ReversedAtUtc { get; set; }

    public Guid ReversedByUserId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>
/// A supplier's own correction of an issued bill (specification task 27). The header records which bill it reverses,
/// the posted return whose goods physically went back when there is one, and the value at the <em>bill's</em> price;
/// the amounts are immutable once posted and the unused balance is derived from the applications against it. A credit
/// never removes stock by itself - that is a return document's job - so crediting a returned delivery cannot take the
/// same quantity out of inventory twice.
/// </summary>
public sealed class SupplierCreditNote : TenantEntity
{
    public Guid SupplierBillId { get; set; }

    public Guid SupplierId { get; set; }

    public Guid LocationId { get; set; }

    /// <summary>The posted return this credit follows. Null for a value-only credit, which changes price, not stock.</summary>
    public Guid? SourcePurchaseReceiptId { get; set; }

    public string CreditNumber { get; set; } = string.Empty;

    /// <summary>Mandatory and audited: whoever reconciles the account reads why the supplier owes a credit.</summary>
    public string Reason { get; set; } = string.Empty;

    public string Currency { get; set; } = "QAR";

    /// <summary>The value before tax, at the bill line's own figures.</summary>
    public decimal BaseAmount { get; set; }

    public decimal TaxAmount { get; set; }

    /// <summary>What the credit takes back from the payable: base plus the input tax that travels with it.</summary>
    public decimal ValueAmount { get; set; }

    /// <summary>Documents this credit has already paid for. The usable balance is derived, never typed.</summary>
    public decimal AppliedAmount { get; set; }

    public decimal BalanceDue { get; set; }

    public Guid JournalEntryId { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public Guid IssuedByUserId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>
/// One credited line of a supplier credit note. It names the bill line it reverses and, where the goods went back,
/// the receipt line carrying the cost the stock was capitalised at - the two prices differ by design, and the
/// difference is the variance the entry records rather than a rounding mystery.
/// </summary>
public sealed class SupplierCreditNoteLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid SupplierCreditNoteId { get; set; }

    public int LineNo { get; set; }

    public Guid SupplierBillLineId { get; set; }

    public Guid? PurchaseReceiptLineId { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal BaseAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal ValueAmount { get; set; }
}

/// <summary>
/// Use of a credit's remaining value against another open bill or an imported opening payable. The credit reduced
/// the payable when it was issued; these rows say which document the benefit settled, so the ledger is never debited
/// twice for one credit and an opening's imported amount is never rewritten (D17).
/// </summary>
public sealed class SupplierCreditApplication : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid SupplierCreditNoteId { get; set; }

    public Guid? SupplierBillId { get; set; }

    public Guid? OpenItemId { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset AppliedAtUtc { get; set; }

    public Guid AppliedByUserId { get; set; }
}
