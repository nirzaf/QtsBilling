namespace QtsBilling.Contracts.Pricing;

/// <summary>Tax rate stored as an exact decimal fraction (0.14 = 14%), bounded to [0, 1] (F-18, QB-12).</summary>
public sealed record TaxRateDto(
    Guid Id,
    string Name,
    decimal Rate,
    bool IsDefault,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveTaxRateRequest(string Name, decimal Rate, bool IsDefault, bool IsActive);

/// <summary>Discount definition kind; wire names match the stored enum (D-31).</summary>
public enum DiscountKind
{
    Fixed,
    Percentage,
}

/// <summary>Scope a discount definition may be applied to.</summary>
public enum DiscountScope
{
    All,
    Product,
    Category,
    Customer,
}

/// <summary>
/// Discount definition with bounded value: percentage 0-100, fixed amount non-negative, window ordered.
/// Disallowed combinations are rejected before they can be stored (QB-12).
/// </summary>
public sealed record DiscountDto(
    Guid Id,
    string Name,
    DiscountKind Kind,
    decimal Value,
    DiscountScope AppliesTo,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveDiscountRequest(
    string Name,
    string Kind,
    decimal Value,
    string AppliesTo,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    bool IsActive);
