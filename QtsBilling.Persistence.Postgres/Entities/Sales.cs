namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Posted sale (POS). Financial values are historical snapshots; nothing is recomputed from mutable
/// product data (spec §3.1). Posting happens inside one database transaction together with stock
/// movements and journal entries (QB-09).
/// </summary>
public sealed class Sale : TenantEntity
{
    public Guid LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public string SaleNumber { get; set; } = string.Empty;

    public SaleStatus Status { get; set; } = SaleStatus.Posted;

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;

    public string? Note { get; set; }

    public DateTimeOffset PostedAtUtc { get; set; }

    public Guid? PostedByUserId { get; set; }

    public Guid? ReversalOfSaleId { get; set; }

    /// <summary>Client idempotency key of the posting request; unique per business when present.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Canonical fingerprint of the original payload: a changed payload under the same key is rejected (QB-09).</summary>
    public string? PayloadHash { get; set; }
}

/// <summary>Sale line with full product/price/tax snapshot at posting time (spec §3.1).</summary>
public sealed class SaleLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid SaleId { get; set; }

    public int LineNo { get; set; }

    public Guid VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    /// <summary>
    /// How the tax was composed into the price of this line (specification D03). It is a snapshot: a
    /// later change of the catalog tax policy never rewrites a posted line, and a document can mix
    /// exclusive and inclusive lines.
    /// </summary>
    public string TaxMode { get; set; } = "Exclusive";

    /// <summary>Cost of goods snapshot used for margin/COGS reporting.</summary>
    public decimal UnitCostSnapshot { get; set; }
}

/// <summary>
/// Payment record. Card PAN/CVV are structurally absent (F-07); card payments may only store a
/// PSP reference and non-sensitive brand/last-four metadata after a verified PSP confirmation.
/// </summary>
public sealed class Payment : TenantEntity
{
    public Guid? SaleId { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? BankAccountId { get; set; }

    public PaymentMethod Method { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public decimal Amount { get; set; }

    public decimal UnallocatedAmount { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal? CashReceived { get; set; }

    public string? Reference { get; set; }

    public string? PspReference { get; set; }

    public string? CardBrand { get; set; }

    public string? CardLast4 { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Drawer session that captured this payment; null for sales without register tracking (QB-10).</summary>
    public Guid? CashRegisterSessionId { get; set; }

    /// <summary>Idempotency receipt for client-initiated invoice payments (QB-11); null for POS sale payments.</summary>
    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }

    public DateTimeOffset? ClearedAtUtc { get; set; }

    public DateTimeOffset? BouncedAtUtc { get; set; }

    public string? BounceReason { get; set; }
}

/// <summary>
/// An append-only application of a customer receipt to one settleable document. Exactly one target is named
/// (specification task 25, recipe step 2): an issued invoice, or an opening receivable the native import brought
/// in - never both, and never neither, because an allocation that belongs to no document would silently erase
/// the deposit it came from. The row is never updated or deleted: a correction is a new linked event.
/// </summary>
public sealed class PaymentAllocation : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid PaymentId { get; set; }

    /// <summary>The invoice this money settles; null when an opening receivable was settled instead.</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>The imported opening receivable this money settles; null when an invoice was settled.</summary>
    public Guid? OpenItemId { get; set; }

    public decimal AllocatedAmount { get; set; }

    public DateTimeOffset AllocatedAtUtc { get; set; }
}

/// <summary>
/// An authorised refund of money a customer left on deposit and that was never applied to a document
/// (specification task 25, recipe step 4). The receipt it returns, the amount it returns and the reason stay on
/// the row forever, the reversal journal is linked by identity, and the ledger - never this row's parent - decides
/// what the customer still has unapplied. Refunding and allocating draw on the same remaining figure, so both
/// take the receipt row lock and the attempt that arrives second finds nothing left to take.
/// </summary>
public sealed class DepositRefund : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid PaymentId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    /// <summary>Mandatory: who authorised money going back out, and why.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>How the money left: a cash drawer movement or a confirmed bank settlement.</summary>
    public string Method { get; set; } = string.Empty;

    public Guid? CashRegisterSessionId { get; set; }

    public string? BankReference { get; set; }

    public Guid JournalEntryId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public Guid RefundedByUserId { get; set; }

    /// <summary>The command's stable key, so a replayed attempt returns this same refund instead of a second one.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;
}

/// <summary>Cash drawer session register for a location.</summary>
public sealed class CashRegister : TenantEntity
{
    public Guid LocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>Append-only cash movement; the drawer balance is always derived from these rows.</summary>
public sealed class CashRegisterMovement : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid CashRegisterId { get; set; }

    public CashMovementType MovementType { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    public string? Reason { get; set; }

    public Guid? SaleId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Drawer session the movement belongs to; null only for legacy/uncoupled movements (QB-10).</summary>
    public Guid? CashRegisterSessionId { get; set; }
}

/// <summary>
/// Cash drawer session (open/close, opening float, counted vs expected). Expected cash is always derived
/// from the append-only movements, never stored as mutable truth except at close (QB-10).
/// </summary>
public sealed class CashRegisterSession : TenantEntity
{
    public Guid CashRegisterId { get; set; }

    public string Currency { get; set; } = "QAR";

    public CashRegisterSessionStatus Status { get; set; } = CashRegisterSessionStatus.Open;

    public decimal OpeningFloat { get; set; }

    public Guid OpenedByUserId { get; set; }

    public DateTimeOffset OpenedAtUtc { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public decimal? CountedCash { get; set; }

    public decimal? ExpectedCash { get; set; }

    public decimal? Variance { get; set; }

    public string? ClosingNote { get; set; }
}

/// <summary>
/// Refund of a posted sale (QB-10/QB-11): cash returned, line quantities and inventory disposition recorded,
/// and the refund journal is posted. Refunds are immutable facts tied to the original sale.
/// </summary>
public sealed class Refund : TenantEntity
{
    public Guid SaleId { get; set; }

    public Guid LocationId { get; set; }

    public string RefundNumber { get; set; } = string.Empty;

    public string Currency { get; set; } = "QAR";

    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    public Guid? CashRegisterSessionId { get; set; }

    /// <summary>The posted reversal entry that neutralizes the original sale entry.</summary>
    public Guid? JournalEntryId { get; set; }

    public DateTimeOffset PostedAtUtc { get; set; }

    public Guid PostedByUserId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Refund line mirroring the original sale line snapshot (spec §3.6 refunds).</summary>
public sealed class RefundLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid RefundId { get; set; }

    public Guid SaleLineId { get; set; }

    public int LineNo { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    public decimal UnitCostSnapshot { get; set; }

    public RefundDisposition Disposition { get; set; } = RefundDisposition.Restock;
}

/// <summary>
/// One named unresolved cash obligation moved out of a drawer session's custody to a receiving custodian
/// (specification D07, task 19). A carry-forward never erases money and never changes the drawer's expected
/// arithmetic: it records, per operation, who now owns the duty to account for an amount that could not be
/// settled before the drawer closed. One row names one operation, so a close can only proceed when every
/// unresolved item on the session has its own row, and a manager reading the drawer later sees the operation,
/// the amount, the reason, the custodian and the authoriser.
/// </summary>
public sealed class CashCustodyResolution : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid CashRegisterId { get; set; }

    public Guid CashRegisterSessionId { get; set; }

    /// <summary>The device the obligation came from, when it came from a terminal rather than the drawer itself.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Groups the rows of one carry-forward document, so a later reader can see the whole act.</summary>
    public string CarryForwardNumber { get; set; } = string.Empty;

    public CashCustodySourceKind SourceKind { get; set; }

    /// <summary>The unresolved thing this row names: a payment, a device operation, or a device's declared custody.</summary>
    public Guid SourceId { get; set; }

    /// <summary>What the operator could read on screen when the amount was carried: a payment, sale or operation reference.</summary>
    public string SourceReference { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "QAR";

    public string Reason { get; set; } = string.Empty;

    public Guid ReceivingCustodianUserId { get; set; }

    public Guid AuthorisedByUserId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}
