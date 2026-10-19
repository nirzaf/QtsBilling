namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>Business-scoped customer. Phone uniqueness is normalized per business (F-14).</summary>
public sealed class Customer : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>Digits-only (E.164-style) phone used for the unique partial index.</summary>
    public string? PhoneNormalized { get; set; }

    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Business-scoped supplier.</summary>
public sealed class Supplier : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Unit of measure.</summary>
public sealed class Unit : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    public bool AllowDecimals { get; set; } = true;
}

/// <summary>Hierarchical product category (legacy <c>ProductCategories</c> / <c>Category</c>).</summary>
public sealed class Category : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public Guid? ParentId { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class Brand : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

public sealed class ProductGroup : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Tax rate stored as an exact fraction (<c>0.14</c> = 14%) with <c>numeric(12,6)</c> precision — a
/// correction of the legacy <c>double</c> rate (F-18).
/// </summary>
public sealed class TaxRate : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public decimal Rate { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class Discount : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public DiscountType Type { get; set; } = DiscountType.Percentage;

    public decimal Value { get; set; }

    public DiscountAppliesTo AppliesTo { get; set; } = DiscountAppliesTo.All;

    public DateTimeOffset? StartsAtUtc { get; set; }

    public DateTimeOffset? EndsAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ExpenseCategory : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional ledger account this category posts to; otherwise the standard expense account is used.</summary>
    public Guid? AccountId { get; set; }

    public bool IsActive { get; set; } = true;
}
