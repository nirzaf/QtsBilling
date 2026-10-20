namespace QtsBilling.Contracts.Banking;

public sealed record BankAccountDto(
    Guid Id,
    string Name,
    string AccountNumberMasked,
    string? IbanMasked,
    string Currency,
    Guid LedgerAccountId,
    string LedgerAccountCode,
    string LedgerAccountName,
    bool IsActive);

public sealed record CreateBankAccountRequest(
    string Name,
    string AccountNumberMasked,
    string? Iban,
    string Currency,
    Guid LedgerAccountId);

public sealed record BankStatementImportDto(
    Guid Id,
    Guid BankAccountId,
    string FileName,
    DateOnly StatementDate,
    decimal OpeningBalance,
    decimal ClosingBalance,
    int LineCount,
    DateTimeOffset ImportedAtUtc);

public sealed record BankStatementLineDto(
    Guid Id,
    int LineNo,
    DateOnly ValueDate,
    string Description,
    string? Reference,
    decimal Amount,
    Guid? MatchedJournalLineId,
    DateTimeOffset? ReconciledAtUtc);

public sealed record BankStatementDetailDto(
    BankStatementImportDto Import,
    IReadOnlyList<BankStatementLineDto> Lines);

public sealed record MatchStatementLineRequest(Guid JournalLineId);
