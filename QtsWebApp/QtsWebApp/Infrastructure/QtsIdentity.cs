using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Infrastructure;

/// <summary>Claim types issued by the QtsBilling identity stack (never trusted from the client).</summary>
public static class QtsClaims
{
    public const string BusinessId = "qts:business_id";
    public const string RequestBusinessId = "qts:request_business_id";
    public const string BusinessHeader = "X-Business-Id";
    public const string AuthorizationRevision = "qts:auth_revision";
    public const string MembershipRole = "qts:membership_role";
    public const string Permission = "qts:permission";
    public const string PasswordResetRequired = "qts:password_reset_required";
}

/// <summary>
/// Role-to-permission mapping. Permissions are resolved server-side at sign-in and re-checked on every
/// request through policies (specification §3.4/QB-04).
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<MembershipRole, IReadOnlyList<string>> Map =
        new Dictionary<MembershipRole, IReadOnlyList<string>>
        {
            [MembershipRole.Owner] = [.. Permissions.All],
            [MembershipRole.Manager] =
            [
                Permissions.CustomersRead, Permissions.CustomersWrite,
                Permissions.ProductsRead, Permissions.ProductsWrite,
                Permissions.InventoryAdjust,
                Permissions.SalesPost, Permissions.SalesVoid, Permissions.SalesRefund,
                Permissions.RegistersManage,
                Permissions.AccountingPost,
                Permissions.InvoicesIssue, Permissions.InvoicesManage, Permissions.PurchasingManage,
                Permissions.ExpensesManage, Permissions.ReportsView,
                Permissions.TaxesManage, Permissions.DiscountsManage,
                Permissions.DevicesManage,
            ],
            [MembershipRole.Cashier] =
            [
                Permissions.CustomersRead, Permissions.CustomersWrite,
                Permissions.ProductsRead,
                Permissions.SalesPost,
                Permissions.RegistersManage,
                Permissions.ReportsView,
            ],
            [MembershipRole.Accountant] =
            [
                Permissions.CustomersRead,
                Permissions.ProductsRead,
                Permissions.AccountingPost, Permissions.AccountingClose,
                Permissions.InvoicesIssue, Permissions.InvoicesManage, Permissions.ExpensesManage,
                Permissions.ReportsView,
            ],
            [MembershipRole.Viewer] =
            [
                Permissions.CustomersRead,
                Permissions.ProductsRead,
                Permissions.ReportsView,
            ],
        };

    public static IReadOnlyList<string> For(MembershipRole role)
        => Map.TryGetValue(role, out var permissions) ? permissions : [];
}

/// <summary>Authorization policies: one per permission, all requiring an authenticated session.</summary>
public static class QtsPolicies
{
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var permission in Permissions.All)
        {
            options.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
    }
}

/// <summary>Requires a single permission claim.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Authorizes a permission against the **current** authority of the actor, not against the claims that
/// were issued at sign-in (specification D02, finding F07).
/// </summary>
/// <remarks>
/// A revoked membership, a deactivated user or a locked-out account loses its authority immediately;
/// the permission is recomputed from the role the membership holds right now. The claims are only used
/// to identify the actor and the business the request claims to act for — they never grant anything.
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly BillingDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PermissionAuthorizationHandler(BillingDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (actorId is null || !Guid.TryParse(actorId, out var userId))
        {
            return;
        }

        var requestedBusiness = _httpContextAccessor.HttpContext?.Request.Headers[QtsClaims.BusinessHeader].ToString();
        var businessId = Guid.TryParse(requestedBusiness, out var headerBusinessId)
            ? headerBusinessId.ToString()
            : context.User.FindFirstValue(QtsClaims.BusinessId);

        if (businessId is null || !Guid.TryParse(businessId, out var scopedBusinessId))
        {
            // No active membership means no authority at all.
            return;
        }

        var actor = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IsActive, user.LockoutEnd })
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (actor is null || !actor.IsActive)
        {
            return;
        }

        if (actor.LockoutEnd is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
        {
            return;
        }

        var membership = await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == userId
                && membership.BusinessId == scopedBusinessId
                && membership.IsActive)
            .Select(membership => new { membership.Role, membership.AuthorizationRevision })
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (membership is null)
        {
            return;
        }

        var tokenBusiness = context.User.FindFirstValue(QtsClaims.BusinessId);
        var revisionClaim = context.User.FindFirstValue(QtsClaims.AuthorizationRevision);
        if (string.Equals(tokenBusiness, scopedBusinessId.ToString(), StringComparison.OrdinalIgnoreCase)
            && long.TryParse(revisionClaim, out var issuedRevision)
            && issuedRevision < membership.AuthorizationRevision)
        {
            return;
        }

        if (RolePermissions.For(membership.Role).Contains(requirement.Permission, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Adds the tenant and permission claims to the sign-in principal. A user with no active membership gets
/// no business claim, so every tenant-scoped endpoint rejects the request (explicit rejection required
/// by QB-04).
/// </summary>
public sealed class QtsClaimsPrincipalFactory : UserClaimsPrincipalFactory<User, IdentityRole<Guid>>
{
    private readonly BillingDbContext _db;

    public QtsClaimsPrincipalFactory(
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        IOptions<IdentityOptions> optionsAccessor,
        BillingDbContext db)
        : base(userManager, roleManager, optionsAccessor)
    {
        _db = db;
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        var membership = await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == user.Id && membership.IsActive)
            .OrderByDescending(membership => membership.BusinessId == user.PreferredBusinessId)
            .ThenBy(membership => membership.CreatedAtUtc)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (membership is not null)
        {
            identity.AddClaim(new Claim(QtsClaims.BusinessId, membership.BusinessId.ToString()));
            identity.AddClaim(new Claim(QtsClaims.AuthorizationRevision, membership.AuthorizationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            identity.AddClaim(new Claim(QtsClaims.MembershipRole, membership.Role.ToString()));

            foreach (var permission in RolePermissions.For(membership.Role))
            {
                identity.AddClaim(new Claim(QtsClaims.Permission, permission));
            }
        }

        if (user.PasswordResetRequired)
        {
            identity.AddClaim(new Claim(QtsClaims.PasswordResetRequired, bool.TrueString));
        }

        return identity;
    }
}
