namespace QtsBilling.Contracts.Customers;

/// <summary>Wire representation of a customer (business-scoped).</summary>
public sealed record CustomerDto(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    bool IsActive,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Customer create/update request. Validation is repeated server-side (spec §7.2).</summary>
public sealed record SaveCustomerRequest(
    string Name,
    string? Email,
    string? Phone,
    string? Address);

/// <summary>Optional customer search filters.</summary>
public sealed record CustomerQuery
{
    public string? Search { get; init; }
    public bool IncludeInactive { get; init; }
}
