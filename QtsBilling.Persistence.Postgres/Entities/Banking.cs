namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>Bank statement source mapped to one asset account in the business ledger.</summary>
public sealed class BankAccount : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string AccountNumberMasked { get; set; } = string.Empty;

    /// <summary>IBAN retained as supplied; API responses expose only its final four characters.</summary>
    public string? Iban { get; set; }

    public string Currency { get; set; } = "QAR";

    public Guid LedgerAccountId { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Immutable statement import metadata; the original CSV is not persisted.</summary>
public sealed class BankStatementImport : TenantEntity
{
    public Guid BankAccountId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public DateOnly StatementDate { get; set; }

    public decimal OpeningBalance { get; set; }

    public decimal ClosingBalance { get; set; }

    public string FileHash { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public int LineCount { get; set; }

    /// <summary>Whether the reconciliation is still open for correction or has been finalised by a reviewer.</summary>
    public BankReconciliationStatus Status { get; set; } = BankReconciliationStatus.Open;

    public DateTimeOffset? FinalisedAtUtc { get; set; }

    public Guid? FinalisedByUserId { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }

    public Guid ImportedByUserId { get; set; }
}

/// <summary>Parsed statement movement with a one-time link to its posted journal line.</summary>
public sealed class BankStatementLine : TenantEntity
{
    public Guid StatementImportId { get; set; }

    public int LineNo { get; set; }

    public DateOnly ValueDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Reference { get; set; }

    public decimal Amount { get; set; }

    public Guid? MatchedJournalLineId { get; set; }

    public DateTimeOffset? ReconciledAtUtc { get; set; }

    public Guid? ReconciledByUserId { get; set; }
}

/// <summary>
/// Append-only evidence of one reconciliation decision: what a statement line was matched to before and after,
/// the journal a reviewed fee or interest line produced, the reason an accountant gave for correcting or
/// reopening, and the command key that wrote it. The line row keeps only the current state; this table keeps the
/// history, so correcting a mistaken match cannot erase it (specification task 31, case T31.03, D10).
/// </summary>
public sealed class BankStatementReconciliationEvent : TenantEntity
{
    public Guid StatementImportId { get; set; }

    /// <summary>Null for a statement-level act (finalise or reopen), set for a line-level act.</summary>
    public Guid? StatementLineId { get; set; }

    public Guid BankAccountId { get; set; }

    public BankReconciliationAction Action { get; set; }

    public Guid? PriorJournalLineId { get; set; }

    public Guid? NewJournalLineId { get; set; }

    /// <summary>The entry a reviewed fee or interest posting produced, so the movement is traceable, not implied.</summary>
    public Guid? JournalEntryId { get; set; }

    /// <summary>The statement line's signed movement at the time of the act, or the posted movement amount.</summary>
    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public Guid ActorUserId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;
}
