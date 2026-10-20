using System.Security.Claims;

namespace QtsWebApp.Infrastructure;

/// <summary>Applies a requested tenant header to the request principal; authorization validates membership before handlers run.</summary>
public sealed class BusinessScopeHeaderMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (Guid.TryParse(context.Request.Headers[QtsClaims.BusinessHeader].ToString(), out var businessId)
            && context.User.Identity is ClaimsIdentity identity
            && identity.IsAuthenticated)
        {
            foreach (var existing in identity.FindAll(QtsClaims.RequestBusinessId).ToArray())
            {
                identity.RemoveClaim(existing);
            }

            identity.AddClaim(new Claim(QtsClaims.RequestBusinessId, businessId.ToString()));
        }

        await next(context).ConfigureAwait(false);
    }
}
