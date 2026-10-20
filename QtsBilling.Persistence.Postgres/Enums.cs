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
}

public enum StockTransferStatus
{
    Draft,
    Dispatched,
    Received,
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
