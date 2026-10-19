using System.Security.Claims;
using System.Text;
using QtsBilling.Contracts.Abstractions;

namespace QtsWebApp.Infrastructure;

/// <summary>ProblemDetails helpers that always attach the stable <see cref="ApiErrorCodes"/> value.</summary>
public static class ApiResults
{
    public static IResult Problem(int statusCode, string code, string title, string? detail = null)
        => Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult ValidationProblem(string detail, IDictionary<string, string[]> errors)
        => Results.ValidationProblem(
            errors,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The request payload is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.ValidationFailed });
}

/// <summary>Tenant resolution from the authenticated principal.</summary>
public static class BusinessScope
{
    public static bool TryGetBusinessId(this ClaimsPrincipal principal, out Guid businessId)
    {
        businessId = Guid.Empty;

        return principal.FindFirst(QtsClaims.BusinessId)?.Value is { Length: > 0 } value
            && Guid.TryParse(value, out businessId);
    }

    public static IResult MissingMembership()
        => ApiResults.Problem(
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.Forbidden,
            "The signed-in user is not an active member of a business.");

    /// <summary>
    /// True when the signed-in membership holds the permission. The permissions are issued by the identity
    /// stack at sign-in and re-checked on every request; the client never decides them.
    /// </summary>
    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
        => principal.HasClaim(QtsClaims.Permission, permission);
}

/// <summary>Digits-only phone normalization used for the per-business uniqueness rule (F-14).</summary>
public static class PhoneNormalizer
{
    private const int MaxDigits = 20;

    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        if (digits.Length == 0)
        {
            return null;
        }

        return digits.Length <= MaxDigits ? digits : digits[^MaxDigits..];
    }
}

/// <summary>Like-pattern escaping for user-supplied search text.</summary>
public static class LikePattern
{
    public static string Contains(string value)
        => $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
}

/// <summary>
/// Bridges between stored enums and wire enums that intentionally share member names. Casting inside a
/// LINQ projection would make EF translate a text column to an integer; bridging in memory avoids that.
/// </summary>
public static class EnumBridge
{
    public static TTarget To<TTarget>(Enum value)
        where TTarget : struct, Enum
        => Enum.Parse<TTarget>(value.ToString(), ignoreCase: true);
}

/// <summary>Domain rejection carrying a stable error code so every handler answers with the same contract.</summary>
public sealed class DomainRuleException(int statusCode, string code, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string Title { get; } = title;

    public IResult ToResult() => ApiResults.Problem(StatusCode, Code, Title, Message);
}

/// <summary>
/// Canonical fingerprint of a request payload (spec §4.4): identical retried payloads produce the same
/// fingerprint, so a duplicate submission can return the original result while a changed payload is rejected.
/// </summary>
public static class RequestFingerprint
{
    public static string Compute<T>(T payload)
    {
        var document = System.Text.Json.JsonSerializer.SerializeToDocument(
            payload,
            QtsBilling.Contracts.Serialization.ContractJson.FingerprintOptions);

        return QtsBilling.Contracts.Serialization.CanonicalJson.ComputeFingerprint(document.RootElement);
    }
}

/// <summary>
/// Opaque keyset cursors. Identifiers are UUIDv7 (time-ordered), so id-order is a stable, unique page key
/// and a page can never repeat or skip a row when data changes between requests (specification §3.3/§7.1).
/// </summary>
public static class CursorPaging
{
    public static string Encode(Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString("D")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static bool TryDecode(string? cursor, out Guid id)
    {
        id = Guid.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');

            return Guid.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(value)), out id) && id != Guid.Empty;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Correlates every request/response pair so support can trace a report to server logs.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied) && !string.IsNullOrWhiteSpace(supplied.ToString())
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Server-side prerender calls its own API. This handler rewrites relative paths to the current request's
/// origin and forwards the session cookie so prerendered markup matches what the browser will load.
/// </summary>
public sealed class LocalApiHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LocalApiHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext;

        if (context is not null && request.RequestUri is not null && request.RequestUri.IsAbsoluteUri)
        {
            var origin = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            request.RequestUri = new Uri($"{origin}{request.RequestUri.PathAndQuery}");

            if (context.Request.Headers.Cookie.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", context.Request.Headers.Cookie.ToArray());
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
