using Microsoft.AspNetCore.Identity;

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
/// Application user (ASP.NET Core Identity, <see cref="Guid"/> key). Legacy plaintext passwords are
/// never migrated (decision D-20); credentials are established through the reset/invitation flow, so
/// <see cref="PasswordResetRequired"/> defaults to true.
/// </summary>
public sealed class User : IdentityUser<Guid>, IVersioned, ITimestamped
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public bool PasswordResetRequired { get; set; } = true;

    public Guid? PreferredBusinessId { get; set; }

    public long Version { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Business membership and role. A user has at most one membership per business.</summary>
public sealed class BusinessMembership : TenantEntity
{
    public Guid UserId { get; set; }

    public MembershipRole Role { get; set; } = MembershipRole.Cashier;

    public bool IsActive { get; set; } = true;

    public long AuthorizationRevision { get; set; } = 1;

    public decimal MaxDiscountRate { get; set; } = 1.000000m;

    public bool AllLocations { get; set; } = true;
}

/// <summary>Location grants for memberships restricted to selected locations.</summary>
public sealed class BusinessMembershipLocation : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid MembershipId { get; set; }

    public Guid LocationId { get; set; }
}

/// <summary>Short-lived, single-use code created by an owner or manager to enroll a physical device.</summary>
public sealed class DeviceEnrolmentChallenge : TenantEntity
{
    public Guid LocationId { get; set; }

    public string ChallengeHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    public Guid? ConsumedByDeviceId { get; set; }

    public Guid CreatedByUserId { get; set; }
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
