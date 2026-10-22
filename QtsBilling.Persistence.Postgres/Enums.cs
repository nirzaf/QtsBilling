namespace QtsBilling.Persistence.Postgres;

/// <summary>Declared inventory costing method. FIFO/LIFO/AVCO semantics are implemented in QB-08.</summary>
public enum AccountingMethod
{
    Fifo,
    Lifo,
    Avco,
}

public enum ProductType
{
    Single,
    Variable,
    Modifier,
    Combo,
}

public enum TaxType
{
    Exclusive,
    Inclusive,
}

/// <summary>Authoritative business effect of an inventory movement.</summary>
public enum MovementType
{
    Opening,
    PurchaseReceipt,
    Sale,
    SaleReversal,
    PurchaseReturn,
    StockAdjustment,
    TransferIn,
    TransferOut,
}

/// <summary>Origin of a journal entry; legacy <c>Transactions</c> and <c>JournalEntries</c> both map here.</summary>
public enum JournalSourceType
{
    Manual,
    Sale,
    Invoice,
    Purchase,
    Expense,
    Payment,
    PaymentAllocation,
    FiscalYearClose,
    CreditNote,
    StockAdjustment,
    Refund,
    Reversal,
    MigrationOpening,
    StockTransfer,
    StockTransferReceipt,
    StockCount,
    SalesOrderDelivery,

    /// <summary>The counted discrepancy of a closed drawer, posted only when the count differs from the movements (task 19).</summary>
    CashOverShort,
}

public enum StockTransferStatus
{
    Draft,
    Dispatched,
    Received,
}

public enum StockCountStatus
{
    Open,
    Submitted,
    Approved,
    Cancelled,
}

public enum EntryStatus
{
    Draft,
    Posted,
    Reversed,
}

public enum AccountingPeriodStatus
{
    Open,
    Closed,
    Locked,
}

public enum SaleStatus
{
    Posted,
    Reversed,
    NeedsReview,
}

public enum RefundDisposition
{
    Restock,
    DamagedWriteOff,
}

/// <summary>Wire-visible payment lifecycle; provisional offline money is never <see cref="Confirmed"/>.</summary>
public enum PaymentStatus
{
    Pending,
    Confirmed,
    Bounced,
    Refunded,
    Voided,
}

public enum PaymentMethod
{
    Cash,
    Card,
    Cheque,
    BankTransfer,
    Other,
    CustomerCredit,
}

public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    PartiallyPaid,
    Void,
}

public enum PurchaseOrderStatus
{
    Draft,
    Ordered,
    PartiallyReceived,
    Received,
    Cancelled,
}

public enum DiscountType
{
    Fixed,
    Percentage,
}

/// <summary>Receipt kind: goods received (+stock) or returned to the supplier (-stock) (QB-12).</summary>
public enum PurchaseReceiptKind
{
    Receipt,
    Return,
}

public enum DiscountAppliesTo
{
    All,
    Product,
    Category,
    Customer,
}

/// <summary>Chart-of-accounts classification; balances are derived from posted journal lines (spec §3.4).</summary>
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>The party subledger a native opening item belongs to.</summary>
public enum OpenItemKind
{
    Receivable,
    Payable,
}

/// <summary>Reason for a cash-register movement (spec §3.6 cash controls).</summary>
public enum CashMovementType
{
    Opening,
    SalePayment,
    CashIn,
    CashOut,
    Refund,
    Closing,
}

/// <summary>Cash drawer session lifecycle; at most one open session per register (QB-10).</summary>
public enum CashRegisterSessionStatus
{
    Open,
    Closed,
}

/// <summary>
/// What kind of unresolved money a carry-forward row names (specification D07, task 19). The three are kept
/// separate because each is seen differently on screen and cleared by different evidence: a payment the server
/// recorded but never confirmed, an operation a terminal pushed that awaits review, or the custody a device
/// holds which the server cannot count until it is pushed and therefore has to be declared.
/// </summary>
public enum CashCustodySourceKind
{
    ProvisionalPayment,
    DeviceOperation,
    DeviceCustody,
}

public enum MembershipRole
{
    Owner,
    Manager,
    Cashier,
    Accountant,
    Viewer,
}

public enum DeviceStatus
{
    Active,
    Revoked,
}

public enum SyncChangeKind
{
    Upsert,
    Tombstone,
}

public enum SyncOutcome
{
    Posted,
    AlreadyPosted,
    NeedsReview,
    NotAuthorized,
    DeviceRevoked,
    UnsupportedPayloadVersion,
    TransientRetry,
    SameKeyDifferentPayload,
}

/// <summary>
/// Why a one-time local handoff ticket exists: first password setup for a newly created account, or
/// administrator-mediated recovery for an account that cannot sign in.
/// </summary>
public enum SetupTicketPurpose
{
    Setup,
    Recovery,
}

/// <summary>
/// Whether an accountant may still change how a statement line was reconciled. A finalised statement is a
/// reviewed position: its lines are locked until an authorised, reasoned reopen (specification task 31), and the
/// accounting period behind it is not touched either way.
/// </summary>
public enum BankReconciliationStatus
{
    Open,
    Finalised,
}

/// <summary>
/// The evidence-writing steps of bank reconciliation. Every transition of a statement line's match state, every
/// reviewed fee or interest posting, and both sides of finalise/reopen leave one append-only row of these, so a
/// corrected match never erases the match it replaced (specification task 31, case T31.03).
/// </summary>
public enum BankReconciliationAction
{
    Matched,
    Unmatched,
    FeePosted,
    InterestPosted,
    Finalised,
    Reopened,
}
