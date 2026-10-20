using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace QtsWebApp.Features.Auth;

/// <summary>
/// The native identity endpoint (specification task 22): the Windows public client obtains an
/// authorization code through the system browser and exchanges it with a PKCE verifier.
/// </summary>
/// <remarks>
/// The token, revocation and discovery endpoints are served by OpenIddict itself; only the
/// authorization endpoint is ours, because it is the point where the signed-in membership becomes the
/// subject. The client is public: there is no client secret to embed in a desktop binary, so possession
/// of the code is proven by the PKCE verifier instead. The issued principal carries the identity and the
/// business scope, never a cached permission list: every request re-reads the current membership.
/// </remarks>
public static class NativeAuthorizationEndpoints
{
    /// <summary>The single, approved native client. Nothing else may request a code.</summary>
    public const string ClientId = "qts-windows";

    /// <summary>The API scope every native access token carries.</summary>
    public const string ApiScope = "qts.api";

    private static readonly Uri[] RegisteredRedirectUris =
    [
        new("http://127.0.0.1:5199/callback"),
        new("qtsbilling://callback"),
    ];

    public static IEndpointRouteBuilder MapNativeAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync)
            .AllowAnonymous()
            .WithName("NativeAuthorize");

        return app;
    }

    /// <summary>
    /// Registers the approved native public client once. The client has no secret; PKCE is required.
    /// </summary>
    public static async Task EnsureNativeClientAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync(ClientId) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            DisplayName = "QtsBilling for Windows",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + ApiScope,
            },
        };

        foreach (var redirectUri in RegisteredRedirectUris)
        {
            descriptor.RedirectUris.Add(redirectUri);
        }

        // The client cannot opt out of PKCE, and this requirement is stored with the application, so a
        // later request without a challenge is refused even if the server-wide switch changes.
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        await manager.CreateAsync(descriptor);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        IOpenIddictApplicationManager applications,
        BillingDbContext db)
    {
        var request = http.GetOpenIddictServerRequest();

        if (request is null || string.IsNullOrWhiteSpace(request.ClientId))
        {
            return InvalidRequest("The authorization request is malformed.");
        }

        // The human is authenticated by the web session of the system browser; the native app never
        // sees the password and never receives the session cookie.
        var session = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        if (!session.Succeeded || session.Principal is null)
        {
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/login" },
                [IdentityConstants.ApplicationScheme]);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId);

        if (application is null)
        {
            return InvalidRequest("The client is not registered.");
        }

        if (string.IsNullOrWhiteSpace(request.RedirectUri)
            || !await applications.ValidateRedirectUriAsync(application, request.RedirectUri))
        {
            return InvalidRequest("The redirect address is not registered for this client.");
        }

        if (!string.Equals(request.ResponseType, ResponseTypes.Code, StringComparison.Ordinal))
        {
            return InvalidRequest("Only the authorization code response is available.");
        }

        if (!string.Equals(request.CodeChallengeMethod, CodeChallengeMethods.Sha256, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.CodeChallenge))
        {
            return InvalidRequest("PKCE with S256 is required.");
        }

        if (string.IsNullOrWhiteSpace(request.State))
        {
            return InvalidRequest("The state parameter is required.");
        }

        var scopes = request.GetScopes();

        if (!scopes.Contains(ApiScope) || !scopes.Contains(Scopes.OpenId))
        {
            return InvalidRequest("The openid and qts.api scopes are required.");
        }

        var userId = session.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? session.Principal.FindFirstValue(Claims.Subject);

        var businessId = session.Principal.FindFirstValue(QtsClaims.BusinessId);

        if (userId is null || businessId is null)
        {
            return InvalidRequest("The session has no active business membership.");
        }

        if (!Guid.TryParse(userId, out var parsedUserId) || !Guid.TryParse(businessId, out var parsedBusinessId))
        {
            return InvalidRequest("The session identity is malformed.");
        }

        var membership = await db.BusinessMemberships.AsNoTracking()
            .Where(row => row.UserId == parsedUserId && row.BusinessId == parsedBusinessId && row.IsActive)
            .Select(row => new { row.AuthorizationRevision })
            .SingleOrDefaultAsync(http.RequestAborted)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return InvalidRequest("The session has no active business membership.");
        }

        // The principal is stored with the authorization and the code; only identity and scope travel
        // to the API. Permissions are deliberately absent: the authorization handler re-reads them.
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);

        var subject = new Claim(Claims.Subject, userId);
        identity.AddClaim(subject);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
        identity.AddClaim(new Claim(ClaimTypes.Name, session.Principal.FindFirstValue(ClaimTypes.Name) ?? userId));
        identity.AddClaim(new Claim(QtsClaims.BusinessId, businessId));
        identity.AddClaim(new Claim(QtsClaims.AuthorizationRevision, membership.AuthorizationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        foreach (var claim in identity.Claims)
        {
            claim.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
        }

        var principal = new ClaimsPrincipal(identity);

        // The granted scopes travel with the principal, so offline_access is what makes the token
        // endpoint issue a refresh token (and nothing else does).
        principal.SetScopes(scopes);

        await http.SignInAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties());

        return Results.Empty;
    }

    private static IResult InvalidRequest(string detail)
        => Results.Json(
            new Dictionary<string, string>
            {
                ["error"] = Errors.InvalidRequest,
                ["error_description"] = detail,
            },
            statusCode: StatusCodes.Status400BadRequest);
}
