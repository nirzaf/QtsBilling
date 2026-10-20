namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Chart-of-accounts entry. Balances are always derived from posted journal lines — the legacy
/// mutable <c>Balance</c> field is deliberately not migrated (F-03/F-11, spec §3.4).
/// </summary>
public sealed class Account : TenantEntity
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AccountType AccountType { get; set; }

    public Guid? ParentId { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Period gates posting; closing a period prevents further entries inside its date range.</summary>
public sealed class AccountingPeriod : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public AccountingPeriodStatus Status { get; set; } = AccountingPeriodStatus.Open;
}

/// <summary>Per-business settlement precision and control-account mapping.</summary>
public sealed class BusinessAccountingSettings : TenantEntity
{
    public int SettlementScale { get; set; } = 2;

    public Guid CashAccountId { get; set; }
    public Guid BankClearingAccountId { get; set; }
    public Guid AccountsReceivableAccountId { get; set; }
    public Guid InventoryAssetAccountId { get; set; }
    public Guid DeliveredNotInvoicedAccountId { get; set; }
    public Guid InputTaxReceivableAccountId { get; set; }
    public Guid AccountsPayableAccountId { get; set; }
    public Guid OutputTaxPayableAccountId { get; set; }
    public Guid GrniClearingAccountId { get; set; }
    public Guid CustomerDepositsAccountId { get; set; }
    public Guid RetainedEarningsAccountId { get; set; }
    public Guid SalesRevenueAccountId { get; set; }
    public Guid SalesReturnsAccountId { get; set; }
    public Guid CogsAccountId { get; set; }
    public Guid PurchasePriceVarianceAccountId { get; set; }
    public Guid InventoryShrinkageAccountId { get; set; }
    public Guid CashOverShortAccountId { get; set; }
    public Guid? RoundingAccountId { get; set; }
}

/// <summary>
/// Canonical journal header. Both legacy stores (<c>Transactions</c> and <c>JournalEntries</c>) map
/// here under an explicit <see cref="SourceType"/> namespace (F-04). Posted entries are immutable;
/// corrections are reversal entries.
/// </summary>
public sealed class JournalEntry : TenantEntity
{
    public string EntryNumber { get; set; } = string.Empty;

    public DateOnly EntryDate { get; set; }

    public Guid? AccountingPeriodId { get; set; }

    public string? Memo { get; set; }

    public JournalSourceType SourceType { get; set; } = JournalSourceType.Manual;

    public Guid? SourceId { get; set; }

    public EntryStatus Status { get; set; } = EntryStatus.Draft;

    public string Currency { get; set; } = "QAR";

    public decimal TotalDebit { get; set; }

    public decimal TotalCredit { get; set; }

    public DateTimeOffset? PostedAtUtc { get; set; }

    public Guid? PostedByUserId { get; set; }

    public Guid? ReversalOfEntryId { get; set; }
}

/// <summary>Journal line. Exactly one of debit/credit is non-zero (database check constraint).</summary>
public sealed class JournalLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid JournalEntryId { get; set; }

    public int LineNo { get; set; }

    public Guid AccountId { get; set; }

    public decimal DebitAmount { get; set; }

    public decimal CreditAmount { get; set; }

    public string Currency { get; set; } = "QAR";

    public string? Description { get; set; }

    public Guid? LocationId { get; set; }
}

/// <summary>
/// Per-business/per-series invoice counter. Numbers are allocated transactionally by the issuing
/// transaction only; no gaps are created by failed requests (QB-11).
/// </summary>
public sealed class InvoiceSequence : TenantEntity
{
    public string Name { get; set; } = "default";

    public string Prefix { get; set; } = string.Empty;

    public int FiscalYear { get; set; }

    public long NextNumber { get; set; } = 1;

    public int Padding { get; set; } = 5;
}

/// <summary>Controlled invoice issuance; lines always carry historical snapshots.</summary>
public sealed class Invoice : TenantEntity
{
    public Guid? LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? SourceSaleId { get; set; }

    public Guid? InvoiceSequenceId { get; set; }

    public string InvoiceNumber { get; set; } = string.Empty;

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public DateOnly IssueDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal SubtotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public decimal CreditedAmount { get; set; }

    public decimal BalanceDue { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset? IssuedAtUtc { get; set; }

    public DateTimeOffset? VoidedAtUtc { get; set; }
}

public sealed class InvoiceLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid InvoiceId { get; set; }

    public int LineNo { get; set; }

    public Guid? VariantId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    /// <summary>Original stock cost and tracking policy, captured when the invoice is saved.</summary>
    public decimal UnitCostSnapshot { get; set; }

    public bool TrackStockSnapshot { get; set; }
}

/// <summary>Partial or full credit note referencing the original invoice (QB-11).</summary>
public sealed class CreditNote : TenantEntity
{
    public Guid InvoiceId { get; set; }

    public Guid? LocationId { get; set; }

    public string CreditNumber { get; set; } = string.Empty;

    public DateOnly IssueDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>The posted journal entry for this credit note.</summary>
    public Guid? JournalEntryId { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public Guid IssuedByUserId { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? PayloadHash { get; set; }
}

/// <summary>Immutable line-level quantity and value snapshot for a partial invoice credit.</summary>
public sealed class CreditNoteLine : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid CreditNoteId { get; set; }

    public Guid InvoiceLineId { get; set; }

    public Guid? VariantId { get; set; }

    public int LineNo { get; set; }

    public decimal QuantityCredited { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal LineTotal { get; set; }

    public bool RestockInventory { get; set; }

    public decimal UnitCostRestocked { get; set; }
}
