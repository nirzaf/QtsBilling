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

/// <inheritdoc />
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindAll(QtsClaims.Permission).Any(claim => string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
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
            .OrderBy(membership => membership.CreatedAtUtc)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (membership is not null)
        {
            identity.AddClaim(new Claim(QtsClaims.BusinessId, membership.BusinessId.ToString()));
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
