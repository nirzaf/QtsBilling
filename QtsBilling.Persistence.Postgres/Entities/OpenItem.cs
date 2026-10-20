namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// An opening receivable or payable established by the approved native import (specification task 19,
/// decision D17). The original amount is immutable; later receipts/payments allocate against it and the
/// open balance is derived from those allocations, never patched here.
/// </summary>
public sealed class OpenItem : TenantEntity
{
    public OpenItemKind Kind { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? SupplierId { get; set; }

    /// <summary>The native opening aggregate, for example <c>native_opening</c>.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Deterministic identity of the source line; unique per business.</summary>
    public Guid SourceId { get; set; }

    public Guid JournalEntryId { get; set; }

    public DateOnly DocumentDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string Currency { get; set; } = "QAR";

    public decimal OriginalAmount { get; set; }

    public string? Reference { get; set; }
}
