namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Tenant root. The single authoritative business record (specification §3.1).
/// </summary>
public sealed class Business : Entity, IVersioned
{
    public string Name { get; set; } = string.Empty;

    public string? LegalName { get; set; }

    /// <summary>ISO 4217 code; every monetary amount is denominated in the business base currency or an explicit transaction currency.</summary>
    public string BaseCurrency { get; set; } = "QAR";

    /// <summary>IANA time zone id used for business-day boundaries and reporting.</summary>
    public string TimeZone { get; set; } = "Asia/Qatar";

    public AccountingMethod AccountingMethod { get; set; } = AccountingMethod.Fifo;

    public bool IsActive { get; set; } = true;

    public long Version { get; set; }
}

/// <summary>Sell/stock location belonging to a business.</summary>
public sealed class BusinessLocation : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Address { get; set; }

    public string? TimeZone { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Application user. Legacy plaintext passwords are never migrated (decision D-20); credentials are
/// established through the QB-04 identity flow, so <see cref="PasswordResetRequired"/> defaults to true.
/// </summary>
public sealed class User : Entity, IVersioned
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Lower-cased e-mail used for uniqueness and lookups.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public bool PasswordResetRequired { get; set; } = true;

    public long Version { get; set; }
}

/// <summary>Business membership and role. A user has at most one membership per business.</summary>
public sealed class BusinessMembership : TenantEntity
{
    public Guid UserId { get; set; }

    public MembershipRole Role { get; set; } = MembershipRole.Cashier;

    public bool IsActive { get; set; } = true;
}

/// <summary>Enrolled client device (MAUI desktop). Revocation is enforced server-side for every sync operation.</summary>
public sealed class DeviceRegistration : TenantEntity
{
    public Guid? LocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Platform { get; set; } = "windows";

    public string? AppVersion { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Active;

    public DateTimeOffset EnrolledAtUtc { get; set; }

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}
