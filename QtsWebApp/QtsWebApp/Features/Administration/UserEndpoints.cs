using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sync;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Administration;

/// <summary>
/// Local account provisioning, authority changes and one-time setup/recovery tickets (specification task 07,
/// requirements R01/R03/R04). Nothing here needs email, SMS or a hosted identity provider: an owner issues a
/// short-lived single-use ticket, hands it over in person or by print, and the recipient chooses their own
/// password. The secret exists only inside one response body; the server keeps a verifier hash.
/// </summary>
public static class UserEndpoints
{
    /// <summary>Suggested ticket lifetime: long enough to hand over, short enough to be worth rotating.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(30);

    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;
    private const int SecretBytes = 32;

    public static IEndpointRouteBuilder MapUserAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/administration/users").WithTags("Administration");

        users.MapGet("/", async (
                ClaimsPrincipal principal,
                BillingDbContext db,
                string? search,
                string? cursor,
                int? pageSize,
                CancellationToken cancellationToken) =>
            await ListAsync(principal, db, search, cursor, pageSize, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/", async (
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                CreateAdministrationUserRequest request,
                CancellationToken cancellationToken) =>
            await CreateAsync(principal, db, http, userManager, request, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPut("/{userId:guid}/membership", async (
                Guid userId,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                UpdateMembershipRequest request,
                CancellationToken cancellationToken) =>
            await UpdateMembershipAsync(userId, principal, db, http, userManager, request, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{userId:guid}/disable", async (
                Guid userId,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                DeactivateUserRequest request,
                CancellationToken cancellationToken) =>
            await SetActiveAsync(userId, principal, db, http, userManager, request.ExpectedVersion, request.Reason, active: false, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{userId:guid}/activate", async (
                Guid userId,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                ActivateUserRequest request,
                CancellationToken cancellationToken) =>
            await SetActiveAsync(userId, principal, db, http, userManager, request.ExpectedVersion, request.Reason, active: true, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{userId:guid}/setup-ticket", async (
                Guid userId,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                IssueSetupTicketRequest request,
                CancellationToken cancellationToken) =>
            await IssueTicketAsync(userId, principal, db, http, userManager, request, cancellationToken))
            .RequireAuthorization(Permissions.UsersManage);

        // Anonymous on purpose: the holder of a handover ticket is signing in for the first time and has no
        // session yet. Failures are rate limited, and the payload chooses no target user of its own.
        app.MapPost("/api/v1/auth/setup-ticket/complete", async (
                BillingDbContext db,
                HttpContext http,
                UserManager<User> userManager,
                IOptions<IdentityOptions> identityOptions,
                CompleteSetupTicketRequest request,
                CancellationToken cancellationToken) =>
            await CompleteTicketAsync(db, http, userManager, identityOptions, request, cancellationToken))
            .WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting("auth-strict");

        return app;
    }

    // ------------------------------------------------------------------ listing

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.BusinessMemberships.AsNoTracking()
            .Where(membership => membership.BusinessId == businessId)
            .Join(db.Users.AsNoTracking(), membership => membership.UserId, user => user.Id, (membership, user) => new { membership, user });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(row => row.user.Email!.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.user.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var after = TryReadCursor(cursor);
        if (after is not null)
        {
            query = query.Where(row => string.Compare(row.user.Email, after.Value.Email, StringComparison.OrdinalIgnoreCase) > 0
                || (string.Equals(row.user.Email, after.Value.Email, StringComparison.OrdinalIgnoreCase) && row.user.Id.CompareTo(after.Value.UserId) > 0));
        }

        var rows = await query
            .OrderBy(row => row.user.Email).ThenBy(row => row.user.Id)
            .Take(size + 1)
            .Select(row => new
            {
                row.user.Id,
                row.user.Email,
                row.user.DisplayName,
                row.user.PasswordResetRequired,
                row.user.IsActive,
                row.user.UpdatedAtUtc,
                membership = row.membership,
                locations = db.BusinessMembershipLocations.AsNoTracking()
                    .Where(grant => grant.BusinessId == businessId && grant.MembershipId == row.membership.Id)
                    .Select(grant => grant.LocationId)
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = rows.Take(size).ToList();
        var nextCursor = rows.Count > size && page.Count > 0
            ? WriteCursor(page[^1].Email ?? string.Empty, page[^1].Id)
            : null;

        var items = page.Select(row => new AdministrationUserDto(
            row.Id,
            row.Email ?? string.Empty,
            row.DisplayName,
            row.membership.Role.ToString(),
            row.membership.IsActive && row.IsActive,
            row.PasswordResetRequired,
            row.membership.AllLocations,
            row.membership.MaxDiscountRate,
            row.locations,
            row.membership.Version,
            row.UpdatedAtUtc)).ToList();

        return Results.Ok(new AdministrationUserPage(items, nextCursor, size));
    }

    // ------------------------------------------------------------------ creation

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UserManager<User> userManager,
        CreateAdministrationUserRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var actorUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var actor = await CurrentMembershipAsync(db, businessId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return BusinessScope.MissingMembership();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var email = request.Email?.Trim() ?? string.Empty;
        if (email.Length is 0 or > 256 || !email.Contains('@'))
        {
            errors[nameof(request.Email)] = ["Enter a valid email address for the account."];
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > 200)
        {
            errors[nameof(request.DisplayName)] = ["Enter a display name of 1-200 characters."];
        }

        if (!TryReadRole(request.Role, out var role) || !HoldsAuthority(actor.Role, role))
        {
            errors[nameof(request.Role)] = ["Choose a role this business can delegate to you."];
        }

        if (request.MaxDiscountRate is < 0m or > 1m)
        {
            errors[nameof(request.MaxDiscountRate)] = ["A discount allowance is between 0 and 1."];
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            errors[nameof(request.Reason)] = ["Record why this account is being created (1-500 characters)."];
        }

        var locationError = await ValidateLocationsAsync(db, businessId, request.AllLocations, request.LocationIds, cancellationToken).ConfigureAwait(false);
        if (locationError is not null)
        {
            errors[nameof(request.LocationIds)] = [locationError];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The account payload is invalid.", errors);
        }

        if (await db.Users.AnyAsync(user => user.NormalizedEmail == email.ToUpperInvariant(), cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "An account already uses that email address on this instance.",
                "An identity is shared across businesses; add the existing person as a member of this business instead.");
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = displayName,
            IsActive = true,
            // The recipient sets the password themselves; nobody, including this administrator, ever learns it.
            PasswordResetRequired = true,
            PreferredBusinessId = businessId,
        };

        var created = await userManager.CreateAsync(user).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return IdentityErrors(created, "The account could not be created.");
        }

        var now = DateTimeOffset.UtcNow;
        var membership = new BusinessMembership
        {
            BusinessId = businessId,
            UserId = user.Id,
            Role = role.GetValueOrDefault(),
            IsActive = true,
            AllLocations = request.AllLocations,
            // A new cashier starts with no discount allowance at all: the field default of 100 % is not a safe default.
            MaxDiscountRate = request.MaxDiscountRate ?? (role == MembershipRole.Cashier ? 0m : 1m),
            AuthorizationRevision = 1,
            Version = 1,
        };

        db.BusinessMemberships.Add(membership);
        foreach (var locationId in request.AllLocations ? [] : request.LocationIds ?? [])
        {
            db.BusinessMembershipLocations.Add(new BusinessMembershipLocation
            {
                BusinessId = businessId,
                MembershipId = membership.Id,
                LocationId = locationId,
            });
        }

        var (ticket, secret) = NewTicket(businessId, user.Id, actorUserId, SetupTicketPurpose.Setup, reason, now);
        db.SetupTickets.Add(ticket);

        db.AuditEvents.Add(UserAudit(businessId, actorUserId, user.Id, "created", now, http.TraceIdentifier, new
        {
            email = user.Email,
            role = membership.Role.ToString(),
            allLocations = membership.AllLocations,
            maxDiscountRate = membership.MaxDiscountRate,
            reason,
            ticketId = ticket.Id,
        }));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Report what the row actually holds: an operator who edits this account immediately afterwards sends
        // the version we just gave them, so it must match the stored value exactly.
        var stored = await db.BusinessMemberships.AsNoTracking()
            .SingleAsync(row => row.BusinessId == businessId && row.UserId == user.Id, cancellationToken)
            .ConfigureAwait(false);

        // Returned exactly once, with no-store: the secret is never recoverable from the server afterwards.
        http.Response.Headers.CacheControl = "no-store";
        return Results.Created(
            $"/api/v1/administration/users/{user.Id}",
            new CreateAdministrationUserResultDto(
                ToDto(user, stored, request.AllLocations ? [] : [.. request.LocationIds ?? []]),
                new SetupTicketDto(ticket.Id, secret, ticket.Purpose.ToString(), ticket.ExpiresAtUtc)));
    }

    // ------------------------------------------------------------------ authority changes

    private static async Task<IResult> UpdateMembershipAsync(
        Guid userId,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UserManager<User> userManager,
        UpdateMembershipRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var actorUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var actor = await CurrentMembershipAsync(db, businessId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return BusinessScope.MissingMembership();
        }

        var target = await db.BusinessMemberships
            .SingleOrDefaultAsync(membership => membership.BusinessId == businessId && membership.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return NotFoundUser();
        }

        if (request.ExpectedVersion != target.Version)
        {
            return StaleVersion(target.Version);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TryReadRole(request.Role, out var role) || !HoldsAuthority(actor.Role, role))
        {
            errors[nameof(request.Role)] = ["Choose a role this business can delegate to you."];
        }

        if (request.MaxDiscountRate is < 0m or > 1m)
        {
            errors[nameof(request.MaxDiscountRate)] = ["A discount allowance is between 0 and 1."];
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            errors[nameof(request.Reason)] = ["Record why this authority is changing (1-500 characters)."];
        }

        var locationError = await ValidateLocationsAsync(db, businessId, request.AllLocations, request.LocationIds, cancellationToken).ConfigureAwait(false);
        if (locationError is not null)
        {
            errors[nameof(request.LocationIds)] = [locationError];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The membership payload is invalid.", errors);
        }

        // The last owner cannot demote themselves away from ownership: an instance with no owner has nobody
        // who can issue the recovery ticket that would restore access.
        if (target.Role == MembershipRole.Owner && role != MembershipRole.Owner
            && await ActiveOwnerCountAsync(db, businessId, cancellationToken).ConfigureAwait(false) <= 1)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This is the last active owner of the business.",
                "Appoint another owner first; ownership cannot be removed while it would leave the business unowned.");
        }

        var now = DateTimeOffset.UtcNow;
        target.Role = role.GetValueOrDefault();
        target.AllLocations = request.AllLocations;
        target.MaxDiscountRate = request.MaxDiscountRate ?? target.MaxDiscountRate;
        // Every authority change advances the revision that cached principals are checked against.
        target.AuthorizationRevision += 1;

        await ReplaceLocationGrantsAsync(db, businessId, target.Id, request.AllLocations, request.LocationIds, cancellationToken).ConfigureAwait(false);

        var user = await userManager.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null)
        {
            return NotFoundUser();
        }

        // Rotating the stamp ends cookie sessions and the refresh-token chain bound to them.
        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);

        db.AuditEvents.Add(UserAudit(businessId, actorUserId, userId, "membership_updated", now, http.TraceIdentifier, new
        {
            role = target.Role.ToString(),
            allLocations = target.AllLocations,
            maxDiscountRate = target.MaxDiscountRate,
            authorizationRevision = target.AuthorizationRevision,
            reason,
        }));

        await SyncTransaction.RunAsync(db, () => Task.FromResult(true), cancellationToken).ConfigureAwait(false);

        var storedMembership = await db.BusinessMemberships.AsNoTracking()
            .SingleAsync(row => row.BusinessId == businessId && row.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToDto(user, storedMembership, await LocationIdsAsync(db, businessId, storedMembership.Id, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<IResult> SetActiveAsync(
        Guid userId,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UserManager<User> userManager,
        long expectedVersion,
        string? reason,
        bool active,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var actorUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 500)
        {
            return ApiResults.ValidationProblem("A reason is required.", new Dictionary<string, string[]>
            {
                [nameof(reason)] = ["Record why this account changes state (1-500 characters)."],
            });
        }

        var target = await db.BusinessMemberships
            .SingleOrDefaultAsync(membership => membership.BusinessId == businessId && membership.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return NotFoundUser();
        }

        if (expectedVersion != target.Version)
        {
            return StaleVersion(target.Version);
        }

        if (!active && target.Role == MembershipRole.Owner
            && await ActiveOwnerCountAsync(db, businessId, cancellationToken).ConfigureAwait(false) <= 1)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This is the last active owner of the business.",
                "Appoint another owner before disabling this one; recovery needs a live owner.");
        }

        var user = await userManager.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null)
        {
            return NotFoundUser();
        }

        var now = DateTimeOffset.UtcNow;
        target.IsActive = active;
        target.AuthorizationRevision += 1;
        user.IsActive = active;

        if (!active)
        {
            // Outstanding handovers must not survive the decision to withdraw access.
            await db.SetupTickets
                .Where(ticket => ticket.BusinessId == businessId && ticket.UserId == userId
                    && ticket.ConsumedAtUtc == null && ticket.RevokedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ticket => ticket.RevokedAtUtc, now)
                    .SetProperty(ticket => ticket.Version, ticket => ticket.Version + 1)
                    .SetProperty(ticket => ticket.UpdatedAtUtc, now)
                    .SetProperty(ticket => ticket.Reason, "revoked: account disabled"))
                .ConfigureAwait(false);
        }

        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await userManager.UpdateAsync(user).ConfigureAwait(false);

        db.AuditEvents.Add(UserAudit(businessId, actorUserId, userId, active ? "activated" : "disabled", now, http.TraceIdentifier, new
        {
            reason = trimmed,
            authorizationRevision = target.AuthorizationRevision,
            ticketsRevoked = !active,
        }));

        await SyncTransaction.RunAsync(db, () => Task.FromResult(true), cancellationToken).ConfigureAwait(false);

        var storedState = await db.BusinessMemberships.AsNoTracking()
            .SingleAsync(row => row.BusinessId == businessId && row.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToDto(user, storedState, await LocationIdsAsync(db, businessId, storedState.Id, cancellationToken).ConfigureAwait(false)));
    }

    // ------------------------------------------------------------------ tickets

    private static async Task<IResult> IssueTicketAsync(
        Guid userId,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UserManager<User> userManager,
        IssueSetupTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var actorUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var actor = await CurrentMembershipAsync(db, businessId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return BusinessScope.MissingMembership();
        }

        // Reauthentication: the caller proves their own current password before issuing authority to somebody
        // else. It is verified and then forgotten; nothing writes it anywhere.
        var actorUser = await userManager.FindByIdAsync(actorUserId.ToString("D")).ConfigureAwait(false);
        if (actorUser is null || !await userManager.CheckPasswordAsync(actorUser, request.ReauthenticationPassword).ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "Reauthentication failed.",
                "Confirm your own current password, then request the ticket again.");
        }

        var purpose = Enum.TryParse<SetupTicketPurpose>(request.Purpose, ignoreCase: true, out var parsed)
            ? parsed
            : SetupTicketPurpose.Setup;

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return ApiResults.ValidationProblem("A ticket needs a reason.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = ["Record how the ticket will be handed over (1-500 characters)."],
            });
        }

        var target = await db.BusinessMemberships
            .SingleOrDefaultAsync(membership => membership.BusinessId == businessId && membership.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return NotFoundUser();
        }

        var user = await userManager.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null)
        {
            return NotFoundUser();
        }

        var now = DateTimeOffset.UtcNow;

        // A fresh ticket supersedes the outstanding ones, so only one handover can ever succeed.
        await db.SetupTickets
            .Where(ticket => ticket.BusinessId == businessId && ticket.UserId == userId
                && ticket.ConsumedAtUtc == null && ticket.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.RevokedAtUtc, now)
                .SetProperty(ticket => ticket.Version, ticket => ticket.Version + 1)
                .SetProperty(ticket => ticket.UpdatedAtUtc, now))
            .ConfigureAwait(false);

        var (ticket, secret) = NewTicket(businessId, userId, actorUserId, purpose, reason, now);
        db.SetupTickets.Add(ticket);

        db.AuditEvents.Add(UserAudit(businessId, actorUserId, userId, "ticket_issued", now, http.TraceIdentifier, new
        {
            ticketId = ticket.Id,
            purpose = ticket.Purpose.ToString(),
            expiresAtUtc = ticket.ExpiresAtUtc,
            reason,
        }));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SetupTicketDto(ticket.Id, secret, ticket.Purpose.ToString(), ticket.ExpiresAtUtc));
    }

    private static async Task<IResult> CompleteTicketAsync(
        BillingDbContext db,
        HttpContext http,
        UserManager<User> userManager,
        IOptions<IdentityOptions> identityOptions,
        CompleteSetupTicketRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var candidate = await db.SetupTickets.AsNoTracking()
            .SingleOrDefaultAsync(ticket => ticket.Id == request.TicketId, cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null || candidate.RevokedAtUtc is not null || candidate.ConsumedAtUtc is not null || candidate.ExpiresAtUtc <= now
            || !string.Equals(candidate.VerifierHash, HashSecret(request.Secret), StringComparison.Ordinal))
        {
            return TicketInvalid();
        }

        // Reject a policy-invalid password before the ticket is spent, so a typo cannot burn the handover.
        var user = await userManager.FindByIdAsync(candidate.UserId.ToString("D")).ConfigureAwait(false);
        if (user is null)
        {
            return TicketInvalid();
        }

        var passwordVerification = await new PasswordValidator<User>()
            .ValidateAsync(userManager, user, request.NewPassword)
            .ConfigureAwait(false);

        if (!passwordVerification.Succeeded)
        {
            return IdentityErrors(passwordVerification, "The new password does not meet this instance's policy.");
        }

        // Exactly one concurrent redemption can flip the terminal state; the loser reads zero affected rows.
        var consumed = await db.SetupTickets
            .Where(ticket => ticket.Id == request.TicketId
                && ticket.ConsumedAtUtc == null
                && ticket.RevokedAtUtc == null
                && ticket.ExpiresAtUtc > now
                && ticket.VerifierHash == candidate.VerifierHash)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.ConsumedAtUtc, now)
                .SetProperty(ticket => ticket.Version, ticket => ticket.Version + 1)
                .SetProperty(ticket => ticket.UpdatedAtUtc, now))
            .ConfigureAwait(false);

        if (consumed != 1)
        {
            return TicketInvalid();
        }

        if (await userManager.HasPasswordAsync(user).ConfigureAwait(false))
        {
            var removed = await userManager.RemovePasswordAsync(user).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                return IdentityErrors(removed, "The existing password could not be replaced.");
            }
        }

        var added = await userManager.AddPasswordAsync(user, request.NewPassword).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            return IdentityErrors(added, "The new password could not be set.");
        }

        user.PasswordResetRequired = false;
        var stampUpdated = await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        if (!stampUpdated.Succeeded)
        {
            return IdentityErrors(stampUpdated, "The session chain could not be reset.");
        }

        await db.SetupTickets
            .Where(ticket => ticket.BusinessId == candidate.BusinessId
                && ticket.UserId == candidate.UserId
                && ticket.Id != candidate.Id
                && ticket.ConsumedAtUtc == null
                && ticket.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.RevokedAtUtc, now)
                .SetProperty(ticket => ticket.Version, ticket => ticket.Version + 1)
                .SetProperty(ticket => ticket.UpdatedAtUtc, now))
            .ConfigureAwait(false);

        db.AuditEvents.Add(new AuditEvent
        {
            BusinessId = candidate.BusinessId,
            UserId = candidate.UserId,
            EntityType = "setup_ticket",
            EntityId = candidate.Id,
            Action = "consumed",
            OccurredAtUtc = now,
            CorrelationId = http.TraceIdentifier,
            // The summary names the actors only: a secret, a hash and a password never enter the trail.
            Summary = JsonSerializer.Serialize(new { purpose = candidate.Purpose.ToString(), issuerUserId = candidate.IssuerUserId }),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SetupHandoffAcknowledgementDto("PasswordSet", "Sign in with the password you chose."));
    }

    // ------------------------------------------------------------------ helpers

    private static (SetupTicket Ticket, string Secret) NewTicket(
        Guid businessId,
        Guid userId,
        Guid issuerUserId,
        SetupTicketPurpose purpose,
        string reason,
        DateTimeOffset now)
    {
        // 256 bits of entropy: the handover is the only secret, and it must not be guessable or reusable.
        var secret = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(SecretBytes));
        return (new SetupTicket
        {
            BusinessId = businessId,
            UserId = userId,
            Purpose = purpose,
            VerifierHash = HashSecret(secret),
            IssuerUserId = issuerUserId,
            ExpiresAtUtc = now.Add(TicketLifetime),
            Reason = reason,
            Version = 1,
        }, secret);
    }

    private static string HashSecret(string secret)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An actor may only grant a role whose permissions they themselves already hold.</summary>
    private static bool HoldsAuthority(MembershipRole actorRole, MembershipRole? grantedRole)
    {
        if (grantedRole is null)
        {
            return false;
        }

        var actorPermissions = RolePermissions.For(actorRole);
        return RolePermissions.For(grantedRole.Value).All(permission => actorPermissions.Contains(permission, StringComparer.Ordinal));
    }

    private static bool TryReadRole(string? value, out MembershipRole? role)
    {
        if (Enum.TryParse<MembershipRole>(value, ignoreCase: false, out var parsed)
            && parsed is MembershipRole.Manager or MembershipRole.Cashier or MembershipRole.Accountant or MembershipRole.Viewer or MembershipRole.Owner)
        {
            role = parsed;
            return true;
        }

        role = null;
        return false;
    }

    private static async Task<int> ActiveOwnerCountAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
        => await db.BusinessMemberships.AsNoTracking().CountAsync(membership => membership.BusinessId == businessId
            && membership.Role == MembershipRole.Owner
            && membership.IsActive, cancellationToken).ConfigureAwait(false);

    private static async Task<BusinessMembership?> CurrentMembershipAsync(BillingDbContext db, Guid businessId, Guid userId, CancellationToken cancellationToken)
        => await db.BusinessMemberships.AsNoTracking()
            .SingleOrDefaultAsync(membership => membership.BusinessId == businessId && membership.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<string?> ValidateLocationsAsync(
        BillingDbContext db,
        Guid businessId,
        bool allLocations,
        IReadOnlyList<Guid>? locationIds,
        CancellationToken cancellationToken)
    {
        if (allLocations)
        {
            return null;
        }

        var wanted = locationIds?.Distinct().ToArray() ?? [];
        if (wanted.Length == 0)
        {
            return "Choose at least one location, or allow all locations.";
        }

        var valid = await db.BusinessLocations.CountAsync(location => location.BusinessId == businessId
            && location.IsActive
            && wanted.Contains(location.Id), cancellationToken).ConfigureAwait(false);

        return valid == wanted.Length ? null : "Every chosen location must be an active branch of this business.";
    }

    private static async Task ReplaceLocationGrantsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid membershipId,
        bool allLocations,
        IReadOnlyList<Guid>? locationIds,
        CancellationToken cancellationToken)
    {
        var existing = await db.BusinessMembershipLocations
            .Where(grant => grant.BusinessId == businessId && grant.MembershipId == membershipId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count > 0)
        {
            db.BusinessMembershipLocations.RemoveRange(existing);
        }

        if (allLocations)
        {
            return;
        }

        foreach (var locationId in locationIds?.Distinct() ?? [])
        {
            db.BusinessMembershipLocations.Add(new BusinessMembershipLocation
            {
                BusinessId = businessId,
                MembershipId = membershipId,
                LocationId = locationId,
            });
        }
    }

    private static async Task<IReadOnlyList<Guid>> LocationIdsAsync(BillingDbContext db, Guid businessId, Guid membershipId, CancellationToken cancellationToken)
        => await db.BusinessMembershipLocations.AsNoTracking()
            .Where(grant => grant.BusinessId == businessId && grant.MembershipId == membershipId)
            .Select(grant => grant.LocationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private static AuditEvent UserAudit(
        Guid businessId,
        Guid actorUserId,
        Guid targetUserId,
        string action,
        DateTimeOffset now,
        string correlationId,
        object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = actorUserId,
            EntityType = "business_membership",
            EntityId = targetUserId,
            Action = action,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
            Summary = JsonSerializer.Serialize(summary),
        };

    private static AdministrationUserDto ToDto(User user, BusinessMembership membership, IReadOnlyList<Guid> locationIds)
        => new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            membership.Role.ToString(),
            membership.IsActive && user.IsActive,
            user.PasswordResetRequired,
            membership.AllLocations,
            membership.MaxDiscountRate,
            locationIds,
            membership.Version,
            user.UpdatedAtUtc);

    private static (string Email, Guid UserId)? TryReadCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')));
            var parts = decoded.Split('\u001f');
            return parts.Length == 2 && Guid.TryParse(parts[1], out var id) ? (parts[0], id) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string WriteCursor(string email, Guid userId)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}\u001f{userId:D}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult TicketInvalid() => ApiResults.Problem(
        StatusCodes.Status403Forbidden,
        ApiErrorCodes.SetupTicketInvalid,
        "That setup ticket is not usable.",
        "A ticket works once, only before it expires, and only for the account it was issued to. Ask an owner for a new one.");

    private static IResult NotFoundUser() => ApiResults.Problem(
        StatusCodes.Status404NotFound,
        ApiErrorCodes.NotFound,
        "This account is not a member of the current business.");

    private static IResult StaleVersion(long currentVersion) => ApiResults.Problem(
        StatusCodes.Status409Conflict,
        ApiErrorCodes.OptimisticConcurrencyFailure,
        "Someone changed this membership first.",
        $"The stored version is {currentVersion}. Reload the account and repeat the change you still intend.");

    private static IResult IdentityErrors(IdentityResult result, string title) => ApiResults.ValidationProblem(
        title,
        new Dictionary<string, string[]> { ["identity"] = [.. result.Errors.Select(error => error.Description)] });
}
