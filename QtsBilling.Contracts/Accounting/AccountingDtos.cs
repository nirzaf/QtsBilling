namespace QtsBilling.Contracts.Accounting;

/// <summary>Chart-of-accounts classification (mirrors the stored enum; serialized camelCase).</summary>
public enum AccountKind
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>Journal lifecycle. Posted entries are immutable; corrections are reversals.</summary>
public enum JournalEntryState
{
    Draft,
    Posted,
    Reversed,
}

/// <summary>Origin namespace of a journal entry; the legacy <c>Transactions</c>/<c>JournalEntries</c> split collapses here.</summary>
public enum JournalOrigin
{
    Manual,
    Sale,
    Invoice,
    Purchase,
    Expense,
    Payment,
    PaymentAllocation,
    FiscalYearClose,
    StockAdjustment,
    Reversal,
    MigrationOpening,
}

public enum AccountingPeriodState
{
    Open,
    Closed,
    Locked,
}

public sealed record AccountDto(
    Guid Id,
    string Code,
    string Name,
    AccountKind AccountType,
    Guid? ParentId,
    bool IsActive,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveAccountRequest(string Code, string Name, AccountKind AccountType, Guid? ParentId);

public sealed record UpdateAccountRequest(string Name, Guid? ParentId, bool IsActive);

public sealed record AccountingPeriodDto(
    Guid Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    AccountingPeriodState Status,
    long Version);

public sealed record SaveAccountingPeriodRequest(string Name, DateOnly StartDate, DateOnly EndDate);

public sealed record FiscalYearCloseDto(int FiscalYear, DateOnly ClosingDate, string JournalEntryNumber, decimal NetIncome, int LockedPeriodCount);

public sealed record BusinessAccountingSettingsDto(
    int SettlementScale,
    Guid CashAccountId,
    Guid BankClearingAccountId,
    Guid AccountsReceivableAccountId,
    Guid InventoryAssetAccountId,
    Guid InputTaxReceivableAccountId,
    Guid AccountsPayableAccountId,
    Guid OutputTaxPayableAccountId,
    Guid GrniClearingAccountId,
    Guid CustomerDepositsAccountId,
    Guid RetainedEarningsAccountId,
    Guid SalesRevenueAccountId,
    Guid SalesReturnsAccountId,
    Guid CogsAccountId,
    Guid PurchasePriceVarianceAccountId,
    Guid InventoryShrinkageAccountId,
    Guid CashOverShortAccountId,
    Guid? RoundingAccountId = null);

public sealed record SaveBusinessAccountingSettingsRequest(
    int SettlementScale,
    Guid CashAccountId,
    Guid BankClearingAccountId,
    Guid AccountsReceivableAccountId,
    Guid InventoryAssetAccountId,
    Guid InputTaxReceivableAccountId,
    Guid AccountsPayableAccountId,
    Guid OutputTaxPayableAccountId,
    Guid GrniClearingAccountId,
    Guid CustomerDepositsAccountId,
    Guid RetainedEarningsAccountId,
    Guid SalesRevenueAccountId,
    Guid SalesReturnsAccountId,
    Guid CogsAccountId,
    Guid PurchasePriceVarianceAccountId,
    Guid InventoryShrinkageAccountId,
    Guid CashOverShortAccountId,
    Guid? RoundingAccountId = null);

/// <summary>One journal line: exactly one of debit/credit must be greater than zero.</summary>
public sealed record JournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Description, Guid? LocationId);

/// <summary>
/// Canonical posting request. A posted entry always has at least two lines, equal debit/credit totals and
/// at most one entry per (origin, source id) so a retried posting cannot double-post.
/// </summary>
public sealed record PostJournalEntryRequest(
    DateOnly EntryDate,
    string? Memo,
    string? Currency,
    JournalOrigin Origin,
    Guid? SourceId,
    IReadOnlyList<JournalLineRequest> Lines);

public sealed record JournalLineDto(
    Guid Id,
    int LineNo,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal Debit,
    decimal Credit,
    string Currency,
    string? Description,
    Guid? LocationId);

public sealed record JournalEntryDto(
    Guid Id,
    string EntryNumber,
    DateOnly EntryDate,
    JournalEntryState Status,
    JournalOrigin Origin,
    Guid? SourceId,
    string Currency,
    decimal TotalDebit,
    decimal TotalCredit,
    string? Memo,
    DateTimeOffset? PostedAtUtc,
    Guid? PostedByUserId,
    Guid? ReversalOfEntryId,
    long Version,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<JournalLineDto> Lines);

public sealed record JournalEntrySummaryDto(
    Guid Id,
    string EntryNumber,
    DateOnly EntryDate,
    JournalEntryState Status,
    JournalOrigin Origin,
    decimal TotalDebit,
    decimal TotalCredit,
    string? Memo,
    DateTimeOffset? PostedAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>Derived account activity: balances come from posted lines only, never from a stored balance column.</summary>
public sealed record TrialBalanceRowDto(
    Guid AccountId,
    string Code,
    string Name,
    AccountKind AccountType,
    decimal Debit,
    decimal Credit,
    decimal Balance);

public sealed record TrialBalanceDto(
    DateOnly From,
    DateOnly To,
    decimal TotalDebit,
    decimal TotalCredit,
    IReadOnlyList<TrialBalanceRowDto> Rows);
