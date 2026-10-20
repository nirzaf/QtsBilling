namespace QtsBilling.Contracts.Expenses;

/// <summary>Expense category; optionally maps to a ledger account (defaults to the standard expense account).</summary>
public sealed record ExpenseCategoryDto(
    Guid Id,
    string Name,
    Guid? AccountId,
    bool IsActive);

public sealed record SaveExpenseCategoryRequest(string Name, Guid? AccountId);

/// <summary>
/// Expense paid by the business. Posting is one ledger entry (expense account debit, cash/bank credit)
/// created inside the idempotent write path; the paid-via method picks the credit account (QB-12).
/// </summary>
public sealed record SaveExpenseRequest(
    Guid? ExpenseCategoryId,
    Guid? LocationId,
    Guid? SupplierId,
    string Description,
    decimal Amount,
    DateOnly OccurredOn,
    string PaidVia,
    string? Reference,
    decimal TaxRate = 0m,
    decimal TaxAmount = 0m,
    string TaxMode = "Exclusive");

public sealed record ExpenseDto(
    Guid Id,
    Guid? ExpenseCategoryId,
    string? ExpenseCategoryName,
    Guid? LocationId,
    Guid? SupplierId,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly OccurredOn,
    string PaidVia,
    string? Reference,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    DateTimeOffset? ReversedAtUtc,
    Guid? ReversalEntryId,
    string? ReversalEntryNumber,
    DateTimeOffset CreatedAtUtc,
    decimal TaxRate = 0m,
    decimal TaxAmount = 0m,
    string TaxMode = "Exclusive");

public sealed record ExpenseActionRequest(string Reason, string? Note);
