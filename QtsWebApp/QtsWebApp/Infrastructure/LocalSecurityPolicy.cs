using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsWebApp.Features.Devices;

namespace QtsWebApp.Infrastructure;

/// <summary>
/// The trust boundaries a self-hosted instance must satisfy before it serves anything (specification task 09,
/// requirements R01/R03/R04/R13). Startup fails closed and names every unmet requirement: an operator is never
/// left running a financial system on an ephemeral key ring, an unencrypted listener or a demo secret, and none
/// of these checks can be answered by silently generating something.
/// </summary>
public static class LocalSecurityPolicy
{
    /// <summary>Explicitly configured HTTPS origin of this instance, e.g. https://pos.office.local:8443.</summary>
    public const string InstanceOriginVariable = "QTS_INSTANCE_ORIGIN";

    /// <summary>Directory holding the Data Protection key ring; keys must survive a restart.</summary>
    public const string DataProtectionPathVariable = "QTS_DATAPROTECTION_PATH";

    /// <summary>Comma-separated literal proxy addresses whose forwarded headers may be trusted. Empty means no proxy.</summary>
    public const string TrustedProxiesVariable = "QTS_TRUSTED_PROXY_HOSTS";

    /// <summary>Header the typed cookie clients use for the anti-forgery token; one name for host, client and harness.</summary>
    public const string AntiforgeryHeaderName = QtsBilling.Contracts.Serialization.ApiHttp.AntiforgeryHeaderName;

    /// <summary>Anti-forgery cookie name; distinct from the authentication cookie.</summary>
    public const string AntiforgeryCookieName = "qts.csrf";

    /// <summary>Extra host names this instance answers for, beside the owned origin (e.g. a mDNS name).</summary>
    public const string AllowedHostsVariable = "QTS_ALLOWED_HOSTS";

    /// <summary>Metrics are opt-in, and then only for a loopback or management-authorised caller.</summary>
    public const string MetricsEnabledVariable = "QTS_METRICS_ENABLED";

    /// <summary>
    /// Key-ring application discriminator. Two instances that ever share a directory still cannot read each
    /// other's payloads, so a copied key ring is not a shared secret.
    /// </summary>
    public const string KeyRingApplicationName = "QtsBilling";

    /// <summary>True when the environment may use ephemeral development conveniences.</summary>
    public static bool AllowsDevelopmentConveniences(IWebHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public static void Enforce(IConfiguration configuration, IWebHostEnvironment environment, ILogger logger)
    {
        var failures = Evaluate(configuration, environment);

        if (failures.Count == 0)
        {
            return;
        }

        foreach (var failure in failures)
        {
            // Nothing sensitive is written: the messages name configuration keys, never values.
            logger.LogError("Local security policy: {Requirement}", failure);
        }

        throw new InvalidOperationException(
            "Refusing to start outside a development environment: " + string.Join(" ", failures)
            + " Configure them, or run the instance in Development. Ephemeral keys and cleartext listeners are not a fallback.");
    }

    /// <summary>The complete list of unmet requirements; empty means the instance may bind a listener.</summary>
    public static IReadOnlyList<string> Evaluate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var failures = new List<string>();

        if (AllowsDevelopmentConveniences(environment))
        {
            return failures;
        }

        var origin = configuration[InstanceOriginVariable]?.Trim();
        if (string.IsNullOrWhiteSpace(origin))
        {
            failures.Add($"{InstanceOriginVariable} must name the owned HTTPS origin this instance is reached at.");
        }
        else if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{InstanceOriginVariable} must be an absolute https:// URL.");
        }
        else if (string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || originUri.Host == "127.0.0.1" || originUri.Host == "::1")
        {
            failures.Add($"{InstanceOriginVariable} must name the server's real host name, not a loopback alias, so the pinned certificate and the OIDC issuer agree.");
        }

        var dataProtectionPath = configuration[DataProtectionPathVariable]?.Trim();
        if (string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            failures.Add($"{DataProtectionPathVariable} must point at a private, backed-up directory so session and ticket keys survive restarts.");
        }
        else if (!Directory.Exists(dataProtectionPath))
        {
            failures.Add($"{DataProtectionPathVariable} points at a directory that does not exist: {dataProtectionPath}");
        }
        else if (IsUnderContentRoot(dataProtectionPath, environment))
        {
            failures.Add($"{DataProtectionPathVariable} must sit outside the application directory: key material inside the web root is both servable and deployable overwriting a backed-up ring.");
        }

        var signingCertificatePath = configuration["QTS_OPENIDDICT_CERTIFICATE_PATH"]?.Trim();
        if (string.IsNullOrWhiteSpace(signingCertificatePath))
        {
            failures.Add("QTS_OPENIDDICT_CERTIFICATE_PATH must name the reviewed token signing certificate.");
        }
        else if (!File.Exists(signingCertificatePath))
        {
            failures.Add($"QTS_OPENIDDICT_CERTIFICATE_PATH names no readable file: {signingCertificatePath}");
        }

        if (string.IsNullOrWhiteSpace(configuration[OfflineGrantKeyRing.PrivateKeyVariable]))
        {
            failures.Add($"{OfflineGrantKeyRing.PrivateKeyVariable} must name the reviewed offline-grant signing key.");
        }

        foreach (var failure in ProxyTrustFailures(configuration))
        {
            failures.Add(failure);
        }

        if (configuration["ConnectionStrings:BillingDb"]?.Contains("Password=", StringComparison.OrdinalIgnoreCase) != true
            && string.IsNullOrWhiteSpace(configuration["QTS_POSTGRES_CONNECTION"]))
        {
            failures.Add("The PostgreSQL connection must come from configuration with its own credentials; there is no built-in development default.");
        }

        return failures;
    }

    /// <summary>
    /// A proxy list is the only way a forwarded <c>X-Forwarded-For</c>/<c>Host</c> becomes believable. Entries must
    /// be literal IP addresses, never a wildcard or a range: an attacker who can arrange to be treated as a
    /// trusted proxy can otherwise forge the client address the rate limiter and the metrics boundary both
    /// partition on (specification task 09, step 2 and 4).
    /// </summary>
    private static IEnumerable<string> ProxyTrustFailures(IConfiguration configuration)
    {
        foreach (var entry in SplitList(configuration[TrustedProxiesVariable]))
        {
            if (entry.Contains('*') || entry.Contains('/') || entry.Contains("://"))
            {
                yield return $"{TrustedProxiesVariable} entries must be a literal proxy IP address, never a wildcard, range or URL: {entry}";
                continue;
            }

            if (!IPAddress.TryParse(entry, out _))
            {
                yield return $"{TrustedProxiesVariable} contains an address that is not a literal IP: {entry}";
            }
        }
    }

    /// <summary>
    /// Configures the Data Protection ring. A deployed instance persists it to the reviewed private directory so
    /// session cookies, security stamps and reset tickets survive a restart; a development or harness instance keeps
    /// an ephemeral ring and is told so rather than silently writing keys next to the database.
    /// </summary>
    public static IDataProtectionBuilder ConfigureDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var path = configuration[DataProtectionPathVariable]?.Trim();
        var builder = services.AddDataProtection().SetApplicationName(KeyRingApplicationName);

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!AllowsDevelopmentConveniences(environment))
            {
                // Enforce() names this before startup; reaching here means the gate was bypassed.
                throw new InvalidOperationException(
                    $"{DataProtectionPathVariable} must point at a private, backed-up directory outside a development environment.");
            }

            return builder;
        }

        return builder.PersistKeysToFileSystem(new DirectoryInfo(path));
    }

    /// <summary>True when a local proxy has been explicitly trusted to forward addresses.</summary>
    public static bool HasTrustedProxies(IConfiguration configuration)
        => SplitList(configuration[TrustedProxiesVariable]).Count > 0;

    /// <summary>
    /// Applies the narrow proxy trust list. With no configured proxy the framework default is kept, which ignores
    /// every forwarded header: a client cannot advertise itself as the proxy.
    /// </summary>
    public static void ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        var proxies = SplitList(configuration[TrustedProxiesVariable]);
        if (proxies.Count == 0)
        {
            return;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();

            // Only single literal addresses: a range or a hostname would widen the trust beyond one reviewed box.
            foreach (var entry in proxies)
            {
                if (IPAddress.TryParse(entry, out var proxy))
                {
                    options.KnownProxies.Add(proxy);
                }
            }
        });
    }

    /// <summary>
    /// Restricts the <c>Host</c> header this instance answers. The owned origin is always allowed; a configured
    /// extra name (a mDNS or LAN alias) may be added. With no owned origin the check is not installed at all
    /// because the host is a development or harness listener whose name is not a stable identity.
    /// </summary>
    public static void ConfigureHostFiltering(IServiceCollection services, IConfiguration configuration)
    {
        var hosts = AllowedHosts(configuration);
        if (hosts.Count == 0)
        {
            return;
        }

        services.AddHostFiltering(options =>
        {
            options.AllowedHosts = hosts.ToArray();
        });
    }

    public static IReadOnlyList<string> AllowedHosts(IConfiguration configuration)
    {
        var hosts = new List<string>();

        var origin = configuration[InstanceOriginVariable]?.Trim();
        if (!string.IsNullOrWhiteSpace(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && !string.IsNullOrWhiteSpace(originUri.Host))
        {
            hosts.Add(originUri.Host);
        }

        foreach (var host in SplitList(configuration[AllowedHostsVariable]))
        {
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    private static bool IsUnderContentRoot(string path, IWebHostEnvironment environment)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var content = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
        var webRoot = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.WebRootPath));

        return StartsUnder(full, content) || (webRoot is not null && StartsUnder(full, webRoot));

        static bool StartsUnder(string candidate, string root)
            => candidate.Length >= root.Length
            && candidate.StartsWith(root, Path.DirectorySeparatorChar == '/' ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && (candidate.Length == root.Length || candidate[root.Length] == Path.DirectorySeparatorChar);
    }

    private static IReadOnlyList<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Cookie-authenticated state changes need an explicit anti-forgery proof. A native bearer request carries
    /// its own access token and is never asked for, and never granted, a cookie principal.
    /// </summary>
    public static bool RequiresAntiforgeryProof(HttpContext http)
    {
        if (HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
        {
            return false;
        }

        // The native PKCE flow authenticates itself; a bearer principal is not a cookie principal.
        if (http.Request.Headers.Authorization is StringValues values
            && values.Any(value => value is not null
                && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // OAuth/OIDC protocol endpoints are driven by the native client without any cookie token: their request
        // integrity comes from PKCE, the state parameter and the registered redirect-address allowlist.
        if (IsOAuthProtocolPath(http.Request.Path))
        {
            return false;
        }

        if (http.User?.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        // Signing in is the mutation that creates the session, so the caller is not authenticated yet. Login CSRF
        // is still a forgery - an attacker submits their own credentials into the operator's browser - and only the
        // session-issuing path needs a proof before it has one.
        return IsSessionIssuingPath(http.Request.Path);
    }

    /// <summary>OpenIddict's own endpoints, which the native PKCE client must be able to drive token-free.</summary>
    public static bool IsOAuthProtocolPath(PathString path)
        => path.StartsWithSegments("/.well-known")
        || (path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/connect/logout", StringComparison.OrdinalIgnoreCase));

    /// <summary>Paths that hand out a session cookie to a caller that does not have one yet.</summary>
    public static bool IsSessionIssuingPath(PathString path)
        => path.Equals("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares the origin the caller declares with the address this instance is actually serving. A hostile page
    /// cannot carry the session proof, and the check runs before token validation so a cross-origin mutation and a
    /// tokenless one get the same rejection (specification task 09, step 2).
    /// </summary>
    public static bool IsSameOriginRequest(HttpRequest request)
    {
        var declared = request.Headers.Origin.ToString();

        if (string.IsNullOrWhiteSpace(declared))
        {
            // No Origin header at all: a native client, an in-process prerender call or a plain navigation.
            // Referer is the next declaration the caller makes about itself; with neither there is nothing to
            // contradict, and the anti-forgery cookie pairing still applies.
            declared = request.Headers.Referer.ToString();
            if (string.IsNullOrWhiteSpace(declared))
            {
                return true;
            }
        }

        if (string.Equals(declared, "null", StringComparison.OrdinalIgnoreCase))
        {
            // An opaque origin (a sandboxed frame) is never same-origin with a host.
            return false;
        }

        return Uri.TryCreate(declared, UriKind.Absolute, out var uri) && HostAndPortMatch(request, uri);
    }

    private static bool HostAndPortMatch(HttpRequest request, Uri declared)
    {
        if (!string.Equals(declared.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actualPort = request.Host.Port
            ?? (string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        return declared.Port == actualPort;
    }

    public static void ConfigureAntiforgery(AntiforgeryOptions options, IWebHostEnvironment environment)
    {
        options.HeaderName = AntiforgeryHeaderName;
        options.Cookie.Name = AntiforgeryCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Loopback development and the automated harness run on plain http; everything else is Secure.
        options.Cookie.SecurePolicy = AllowsDevelopmentConveniences(environment)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    }

    /// <summary>
    /// Issues the anti-forgery cookie and returns the token the typed clients echo back in
    /// <see cref="AntiforgeryHeaderName"/>. The response is never cacheable: the value is a credential for
    /// mutating the operator's own session.
    /// </summary>
    public static Task<IResult> IssueAntiforgeryTokenAsync(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        return Task.FromResult<IResult>(Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken, AntiforgeryHeaderName)));
    }

    /// <summary>
    /// Metrics are opt-in outside development. When enabled they answer only a loopback caller or a principal
    /// whose <b>current</b> membership holds the management audit permission. Permissions are re-read from the
    /// database rather than trusted from an issued claim, so a withdrawn role cannot keep scraping operational
    /// data (specification D04, task 09).
    /// </summary>
    public static async Task<bool> MayScrapeMetricsAsync(
        HttpContext http,
        BillingDbContext db,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var configured = configuration[MetricsEnabledVariable];
        var enabled = string.IsNullOrWhiteSpace(configured)
            ? AllowsDevelopmentConveniences(environment)
            : string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            return false;
        }

        var remote = http.Connection.RemoteIpAddress;
        if (remote is null)
        {
            // An in-memory host has no peer address; that is only loopback-equivalent inside development or the
            // automated harness, never in a deployed environment.
            return AllowsDevelopmentConveniences(environment)
                && http.User?.Identity?.IsAuthenticated != true;
        }

        var loopback = IPAddress.IsLoopback(remote);
        if (loopback && http.User?.Identity?.IsAuthenticated != true)
        {
            // Loopback is the operator's own machine; a remote caller must be authorised instead.
            return true;
        }

        if (!loopback && http.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!http.User.TryGetBusinessId(out var businessId)
            || !http.User.TryGetCurrentUserId(out var userId))
        {
            return false;
        }

        return await BusinessScope.HasCurrentPermissionAsync(db, userId, businessId, Permissions.AuditView, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Anti-forgery proof for the current session; sent as a header, never as a query string. The token is absent
    /// only if the response had already started, and a client that receives no value must not mutate.
    /// </summary>
    public sealed record AntiforgeryTokenResponse(string? RequestToken, string HeaderName);
}

/// <summary>
/// Operational metrics are management data. They are opt-in, and when enabled they answer only a loopback
/// caller or a principal whose current membership holds <c>audit.view</c>; anybody else gets the same answer as
/// if the endpoint did not exist, so the surface cannot be probed (specification task 09, finding F07).
/// </summary>
public sealed class MetricsAccessMiddleware
{
    private readonly RequestDelegate _next;

    public MetricsAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext http,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // The database is reached only for a scrape that needs a permission decision, so the middleware costs an
        // ordinary request nothing beyond one path comparison.
        if (!http.Request.Path.StartsWithSegments("/metrics")
            || await LocalSecurityPolicy.MayScrapeMetricsAsync(
                http,
                http.RequestServices.GetRequiredService<BillingDbContext>(),
                configuration,
                environment,
                http.RequestAborted).ConfigureAwait(false))
        {
            await _next(http).ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}

/// <summary>
/// Rejects cookie-authenticated mutations that arrive without an anti-forgery proof. Placed after
/// authentication so the decision is made about a known principal, and typed as endpoint metadata so
/// anonymous native routes are unaffected.
/// </summary>
public sealed class CookieAntiforgeryMiddleware
{
    private readonly RequestDelegate _next;

    public CookieAntiforgeryMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext http,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
    {
        if (!LocalSecurityPolicy.RequiresAntiforgeryProof(http))
        {
            await _next(http).ConfigureAwait(false);
            return;
        }

        // A declared foreign origin is refused before any token is looked at: the proof belongs to the session
        // the caller is pretending to be, and answering "missing token" for both cases keeps the rejection
        // shape identical (specification task 09, case T09.02).
        if (!LocalSecurityPolicy.IsSameOriginRequest(http.Request))
        {
            await RejectAsync(http).ConfigureAwait(false);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            await RejectAsync(http).ConfigureAwait(false);
            return;
        }

        await _next(http).ConfigureAwait(false);
    }

    private static async Task RejectAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        http.Response.ContentType = "application/problem+json";
        await http.Response.WriteAsJsonAsync(new Dictionary<string, object?>
        {
            ["type"] = "https://httpstatuses.com/400",
            ["title"] = "The anti-forgery token is missing or invalid.",
            ["status"] = StatusCodes.Status400BadRequest,
            ["detail"] = $"Fetch /api/v1/auth/csrf-token and send its value in the {LocalSecurityPolicy.AntiforgeryHeaderName} header.",
            ["code"] = QtsBilling.Contracts.Abstractions.ApiErrorCodes.ValidationFailed,
        }).ConfigureAwait(false);
    }
}
