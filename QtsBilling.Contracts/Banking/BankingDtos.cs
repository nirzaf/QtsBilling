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
    DateTimeOffset ImportedAtUtc,
    /// <summary>Whether the reconciliation is still open for correction or has been finalised (task 31).</summary>
    BankReconciliationState Status = BankReconciliationState.Open,
    /// <summary>The version a finalise or reopen must name, so two accountants cannot both close one statement.</summary>
    long ExpectedVersion = 0,
    DateTimeOffset? FinalisedAtUtc = null,
    string? FinalisedByUserName = null,
    string? ImportedByUserName = null,
    /// <summary>Digest of the uploaded file, so the same CSV is recognised rather than re-imported.</summary>
    string FileDigest = "");

/// <summary>Whether an accountant can still change how a statement was reconciled.</summary>
public enum BankReconciliationState
{
    Open,
    Finalised,
}

public sealed record BankStatementLineDto(
    Guid Id,
    int LineNo,
    DateOnly ValueDate,
    string Description,
    string? Reference,
    decimal Amount,
    Guid? MatchedJournalLineId,
    DateTimeOffset? ReconciledAtUtc,
    /// <summary>The posted entry the line is matched to, named rather than keyed, so an operator can check it.</summary>
    string? MatchedEntryNumber = null,
    /// <summary>The line version the reviewer looked at; a correction against a moved line is refused.</summary>
    long ExpectedVersion = 0);

public sealed record BankStatementDetailDto(
    BankStatementImportDto Import,
    IReadOnlyList<BankStatementLineDto> Lines);

public sealed record MatchStatementLineRequest(
    Guid JournalLineId,
    /// <summary>The statement version the reviewer looked at; a stale view is refused rather than silently kept.</summary>
    long? ExpectedVersion = null);

/// <summary>
/// A reasoned correction: the line is released from the journal it was matched to and the evidence of that match is
/// retained. The caller names the line version and the match it is walking away from, so a view that has moved on
/// cannot be corrected over somebody else's decision.
/// </summary>
public sealed record UnmatchStatementLineRequest(long ExpectedLineVersion, Guid ExpectedJournalLineId, string Reason);

/// <summary>
/// A fee or interest line the bank charged or paid that the business has not booked. It is posted as an ordinary
/// reviewed journal through the existing posting rules - one leg against the bank account, one against an account
/// the operator chose by name - and the statement line is then matched to the bank leg it produced.
/// </summary>
public sealed record RecordStatementMovementRequest(Guid AccountId, long ExpectedLineVersion, string Reason, DateOnly? PostingDate = null);

/// <summary>Statement-level acts are against the version the review answered with.</summary>
public sealed record FinaliseStatementRequest(long ExpectedVersion);

public sealed record ReopenStatementRequest(long ExpectedVersion, string Reason);

/// <summary>A posted bank-account line the reviewer may choose for a statement line, named by its entry.</summary>
public sealed record BankStatementCandidateDto(
    Guid JournalLineId,
    Guid JournalEntryId,
    string EntryNumber,
    DateOnly EntryDate,
    string? Description,
    decimal Amount,
    string AccountCode,
    string AccountName);

/// <summary>One reconciliation decision as it was recorded, with the entries on both sides of it.</summary>
public sealed record BankReconciliationEventDto(
    Guid Id,
    int LineNo,
    string Kind,
    decimal Amount,
    string? Reason,
    string? PriorEntryNumber,
    string? NewEntryNumber,
    DateTimeOffset OccurredAtUtc,
    string ActorName);

/// <summary>
/// The two sides of a reconciliation read together. The statement's own figures must add up; the lines it is matched
/// to must carry the same total in the ledger; and what is not matched stays visible. The account's own movement and
/// balance in the period are reported as figures the reviewer compares with the bank's closing, because one
/// statement is not obliged to explain every entry the account has ever held (specification task 31, T31.01).
/// </summary>
public sealed record BankStatementArithmeticDto(
    decimal OpeningBalance,
    decimal MovementTotal,
    decimal CalculatedClosing,
    decimal StatedClosing,
    decimal MatchedTotal,
    decimal UnmatchedTotal,
    int UnmatchedCount,
    /// <summary>The nets of the posted journal lines this statement is actually matched to.</summary>
    decimal LedgerSideTotal,
    /// <summary>What the booked bank account moved in the period this statement closes.</summary>
    decimal BookedBankMovement,
    /// <summary>What the booked bank account holds at the statement date.</summary>
    decimal BookedBankBalance,
    /// <summary>Ledger side minus matched side: any non-zero figure is a statement that cannot be trusted.</summary>
    decimal Difference,
    bool StatementAddsUp,
    bool TiesToLedger,
    int SettlementScale,
    string TimeZoneId);

/// <summary>The candidates for one statement line, named by the line number the reviewer reads.</summary>
public sealed record BankStatementLineReviewDto(Guid LineId, int LineNo, IReadOnlyList<BankStatementCandidateDto> Candidates);

/// <summary>The reviewer's whole position on one statement: figures, lines with their candidates, and the history.</summary>
public sealed record BankStatementReviewDto(
    BankAccountDto Account,
    BankStatementDetailDto Statement,
    BankStatementArithmeticDto Arithmetic,
    IReadOnlyList<BankStatementLineReviewDto> Lines,
    IReadOnlyList<BankReconciliationEventDto> Events,
    string? AccountingPeriodName,
    bool AccountingPeriodClosed);

