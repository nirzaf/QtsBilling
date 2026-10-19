using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Auth;

/// <summary>
/// Session endpoints (QB-04). The web app signs in with secure cookies; the browser never sees a
/// password hash, a database credential or a long-lived token. Raw-password lookups are gone: Identity
/// verifies salted PBKDF2 hashes and locks accounts after repeated failures.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Reset/invitation tickets expire quickly; ops reissues if needed.</summary>
    public static readonly TimeSpan ResetTicketLifetime = TimeSpan.FromHours(2);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapGet("/session", GetSessionAsync).RequireAuthorization();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous();
        group.MapPost("/password-reset/complete", CompletePasswordResetAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        BillingDbContext db,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;

        if (email.Length == 0 || string.IsNullOrEmpty(request.Password))
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "E-mail and password are required.");
        }

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        // Unknown and inactive accounts are indistinguishable to the caller (no account enumeration).
        if (user is null || !user.IsActive)
        {
            await RecordAuthEventAsync(db, null, "login.failed", email, "unknown-or-inactive", http.TraceIdentifier, cancellationToken).ConfigureAwait(false);

            return InvalidCredentials();
        }

        if (user.PasswordResetRequired)
        {
            await RecordAuthEventAsync(db, user.Id, "login.failed", email, "setup-required", http.TraceIdentifier, cancellationToken).ConfigureAwait(false);

            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.PasswordResetRequired,
                "This account must complete account setup before signing in.",
                "Use the invitation/reset flow to establish a password, then sign in again.");
        }

        var result = await signInManager
            .PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        if (result.IsLockedOut)
        {
            await RecordAuthEventAsync(db, user.Id, "login.failed", email, "locked-out", http.TraceIdentifier, cancellationToken).ConfigureAwait(false);

            return ApiResults.Problem(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidCredentials,
                "Invalid e-mail or password.",
                "The account is temporarily locked after repeated failed attempts.");
        }

        if (!result.Succeeded)
        {
            await RecordAuthEventAsync(db, user.Id, "login.failed", email, "invalid-credentials", http.TraceIdentifier, cancellationToken).ConfigureAwait(false);

            return InvalidCredentials();
        }

        await RecordAuthEventAsync(db, user.Id, "login.succeeded", email, null, http.TraceIdentifier, cancellationToken).ConfigureAwait(false);

        return Results.Ok(await BuildSessionAsync(user, db, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetSessionAsync(
        ClaimsPrincipal principal,
        UserManager<User> userManager,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(principal).ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            return ApiResults.Problem(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthenticated,
                "The session is no longer valid.");
        }

        return Results.Ok(await BuildSessionAsync(user, db, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> LogoutAsync(SignInManager<User> signInManager)
    {
        await signInManager.SignOutAsync().ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        RequestPasswordResetRequest request,
        UserManager<User> userManager,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("QtsBilling.Auth");
        var email = request.Email?.Trim() ?? string.Empty;
        var user = email.Length == 0 ? null : await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            logger.LogInformation("Password reset requested for unknown or inactive account.");

            return Results.Accepted();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);

        if (environment.IsDevelopment())
        {
            // The development ticket travels through the response only; the log records that it was
            // generated, never the token itself (QB-14: logs carry no credentials or tokens).
            logger.LogWarning("Development password-reset ticket generated for {Email}; the token is in the response body.", user.Email);

            return Results.Ok(new PasswordResetTicketDto(user.Email ?? email, token, DateTimeOffset.UtcNow.Add(ResetTicketLifetime)));
        }

        // Production: the ticket leaves the process through the ops invitation channel, never the API response.
        logger.LogWarning("Password reset ticket generated for {Email}; deliver it out of band.", user.Email);

        return Results.Accepted();
    }

    private static async Task<IResult> CompletePasswordResetAsync(
        ResetPasswordRequest request,
        UserManager<User> userManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("QtsBilling.Auth");
        var email = request.Email?.Trim() ?? string.Empty;
        var user = email.Length == 0 ? null : await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            return InvalidTicket();
        }

        var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var errors = result.Errors
                .GroupBy(error => error.Code)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());

            logger.LogInformation("Rejected password reset for {Email}: {Codes}", user.Email, string.Join(",", errors.Keys));

            return ApiResults.ValidationProblem("The reset ticket is invalid, expired or the new password is rejected.", errors);
        }

        if (user.PasswordResetRequired)
        {
            user.PasswordResetRequired = false;
            await userManager.UpdateAsync(user).ConfigureAwait(false);
        }

        logger.LogInformation("Password established for {Email}; account setup completed.", user.Email);

        return Results.NoContent();
    }

    private static async Task<SessionDto> BuildSessionAsync(User user, BillingDbContext db, CancellationToken cancellationToken)
    {
        var membership = await db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == user.Id && membership.IsActive)
            .OrderBy(membership => membership.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string? businessName = null;

        if (membership is not null)
        {
            businessName = await db.Businesses
                .AsNoTracking()
                .Where(business => business.Id == membership.BusinessId)
                .Select(business => business.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var displayName = user.DisplayName.Length > 0 ? user.DisplayName : user.UserName ?? string.Empty;

        return new SessionDto(
            user.Id,
            user.Email ?? string.Empty,
            displayName,
            membership?.BusinessId,
            businessName,
            membership is null ? [] : [membership.Role.ToString()],
            membership is null ? [] : RolePermissions.For(membership.Role),
            user.PasswordResetRequired);
    }

    /// <summary>
    /// Structured, non-sensitive authentication audit (QB-14). The e-mail is masked before it is stored
    /// and the password or reset ticket never appears; the entry carries the request correlation id so a
    /// security review can tie it to the request that produced it.
    /// </summary>
    private static async Task RecordAuthEventAsync(
        BillingDbContext db,
        Guid? userId,
        string action,
        string email,
        string? reason,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Guid? businessId = null;

        if (userId is { } accountId)
        {
            businessId = await db.BusinessMemberships.AsNoTracking()
                .Where(membership => membership.UserId == accountId && membership.IsActive)
                .OrderBy(membership => membership.CreatedAtUtc)
                .Select(membership => (Guid?)membership.BusinessId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        db.AuditEvents.Add(new AuditEvent
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = "auth",
            Action = action,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            Summary = JsonSerializer.Serialize(new { email = Redaction.Email(email), reason }),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IResult InvalidCredentials()
        => ApiResults.Problem(
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.InvalidCredentials,
            "Invalid e-mail or password.");

    private static IResult InvalidTicket()
        => ApiResults.Problem(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationFailed,
            "The reset ticket is invalid or has expired.");
}
