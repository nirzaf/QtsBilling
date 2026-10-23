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

/// <summary>
/// Closing a period is an explained act, not a click. The reviewer names the version they read, so a close cannot
/// land on a period somebody else already moved, and the reason they would show an auditor. The body is optional on
/// the route so a caller that only wants the bare gate still gets it; a screen always sends both.
/// </summary>
public sealed record CloseAccountingPeriodRequest(long ExpectedVersion, string? Reason = null);

/// <summary>
/// A reversal may be dated on the day it is recorded rather than the day of the entry it corrects, which is the only
/// way a correction can be made after the original period has been closed. The requested date is validated against
/// the business calendar and the period gates like any other posting.
/// </summary>
public sealed record ReverseJournalRequest(DateOnly? CorrectionDate = null, string? Reason = null);

/// <summary>
/// One outstanding item the reviewer must see before a period closes: what it is, how many, and the money involved.
/// A balance legitimately carried across a period boundary (an open receivable) is told apart from an unfinished act
/// inside the window (a draft journal), because only the second one is a reason not to close.
/// </summary>
public sealed record PeriodOutstandingDto(string Kind, string Title, int Count, decimal Amount, bool BlocksClose, string WhatItMeans);

/// <summary>What closing a period would leave behind, read from posted facts, alongside the readiness of the year.</summary>
public sealed record PeriodCloseEvidenceDto(
    AccountingPeriodDto Period,
    DateOnly BusinessToday,
    string AccountingZone,
    int SettlementScale,
    bool PeriodOpen,
    decimal PostedDebit,
    decimal PostedCredit,
    int PostedEntryCount,
    decimal NetIncome,
    decimal ReceivableBalance,
    decimal PayableBalance,
    decimal StockValue,
    decimal TransitValue,
    decimal UnappliedDeposits,
    decimal DrawerCash,
    int OpenCustodyCases,
    int DraftJournalCount,
    int UnconfirmedPaymentCount,
    IReadOnlyList<PeriodOutstandingDto> Outstanding,
    bool YearComplete,
    bool YearAlreadyClosed,
    string? YearGapFrom,
    IReadOnlyList<TrialBalanceRowDto> TrialBalance);

/// <summary>The year-end position: whether the close can run, what it would move, and what it produces.</summary>
public sealed record FiscalYearEvidenceDto(
    int FiscalYear,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly BusinessToday,
    string AccountingZone,
    int SettlementScale,
    int PeriodCount,
    int OpenPeriodCount,
    int LockedPeriodCount,
    bool CoversWholeYear,
    bool AlreadyClosed,
    bool RetainedEarningsReady,
    string? RetainedEarningsCode,
    string? GapFrom,
    decimal Revenue,
    decimal Expenses,
    decimal NetIncome,
    IReadOnlyList<PeriodOutstandingDto> Outstanding,
    IReadOnlyList<JournalEntrySummaryDto> ClosingEntries);

public sealed record FiscalYearCloseDto(int FiscalYear, DateOnly ClosingDate, string JournalEntryNumber, decimal NetIncome, int LockedPeriodCount);

public sealed record BusinessAccountingSettingsDto(
    int SettlementScale,
    Guid CashAccountId,
    Guid BankClearingAccountId,
    Guid AccountsReceivableAccountId,
    Guid InventoryAssetAccountId,
    Guid DeliveredNotInvoicedAccountId,
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
    Guid DeliveredNotInvoicedAccountId,
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
