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
