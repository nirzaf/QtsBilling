using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Serialization;
using QtsWebApp.Features.Devices;
using QtsWebApp.Infrastructure;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The trust boundaries of a self-hosted instance (specification task 09, T09.01 - T09.04): startup refuses a
/// deployed host that is missing its keys or origin, every cookie mutation needs an explicit anti-forgery proof
/// while a native bearer call never carries one, operational metrics answer only an opted-in local or
/// authorised caller, and a persistent key ring survives a restart without merging the purposes that share it.
/// </summary>
/// <remarks>
/// The fail-closed cases drive the same functions <c>Program.cs</c> calls, and the wire cases drive the real
/// host against the real disposable PostgreSQL. Nothing here is a mock: a refused mutation is proven by the
/// absent row, and an accepted one by the row that appeared.
/// </remarks>
public sealed class LocalSecurityPolicyTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    // ---------------------------------------------------------------- T09.01

    [Fact]
    public void T09_01_a_deployed_instance_names_every_unmet_trust_requirement_before_it_starts()
    {
        var contentRoot = Root("app");
        var failures = LocalSecurityPolicy.Evaluate(Configure(), Deployed(contentRoot));

        Assert.Contains(failures, failure => failure.Contains(LocalSecurityPolicy.InstanceOriginVariable, StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains(LocalSecurityPolicy.DataProtectionPathVariable, StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("QTS_OPENIDDICT_CERTIFICATE_PATH", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains(OfflineGrantKeyRing.PrivateKeyVariable, StringComparison.Ordinal));

        // There is no built-in development database credential for a deployed host to fall back on.
        Assert.Contains(failures, failure => failure.Contains("PostgreSQL", StringComparison.Ordinal));

        Directory.Delete(contentRoot, recursive: true);
    }

    [Fact]
    public void T09_01b_a_loopback_http_or_wildcard_configuration_is_refused_rather_than_warned_about()
    {
        var contentRoot = Root("app");
        var keys = Root("keys");
        var certificate = Path.Combine(contentRoot, "signing.pfx");
        File.WriteAllBytes(certificate, [1, 2, 3]);

        // A loopback alias cannot be the identity of a machine other terminals must trust.
        Assert.Single(LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://localhost:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, keys),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed")),
            Deployed(contentRoot)), failure => failure.Contains("real host name", StringComparison.Ordinal));

        // Cleartext is not an owned origin.
        Assert.Contains(LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "http://pos.office.local:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, keys),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed")),
            Deployed(contentRoot)), failure => failure.Contains("absolute https", StringComparison.Ordinal));

        // A wildcard or a range would let any client advertise the address the rate limiter and the metrics
        // boundary partition on; reviewed literal proxy addresses are accepted.
        var wildcard = LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://pos.office.local:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, keys),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed"),
            (LocalSecurityPolicy.TrustedProxiesVariable, "*")),
            Deployed(contentRoot));
        Assert.Contains(wildcard, failure => failure.Contains(LocalSecurityPolicy.TrustedProxiesVariable, StringComparison.Ordinal));

        var ranged = LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://pos.office.local:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, keys),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed"),
            (LocalSecurityPolicy.TrustedProxiesVariable, "192.168.7.0/24")),
            Deployed(contentRoot));
        Assert.Contains(ranged, failure => failure.Contains(LocalSecurityPolicy.TrustedProxiesVariable, StringComparison.Ordinal));

        var reviewed = LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://pos.office.local:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, keys),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed"),
            (LocalSecurityPolicy.TrustedProxiesVariable, "10.0.0.4, 10.0.0.5")),
            Deployed(contentRoot));
        Assert.Empty(reviewed);

        Directory.Delete(contentRoot, recursive: true);
        Directory.Delete(keys, recursive: true);
    }

    [Fact]
    public void T09_01c_a_key_ring_inside_the_application_directory_is_refused()
    {
        var contentRoot = Root("app");
        var insideApp = Path.Combine(contentRoot, "keys");
        Directory.CreateDirectory(insideApp);
        var certificate = Path.Combine(contentRoot, "signing.pfx");
        File.WriteAllBytes(certificate, [1, 2, 3]);

        var failures = LocalSecurityPolicy.Evaluate(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://pos.office.local:8443"),
            (LocalSecurityPolicy.DataProtectionPathVariable, insideApp),
            ("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate),
            (OfflineGrantKeyRing.PrivateKeyVariable, Path.Combine(contentRoot, "grant.key")),
            ("ConnectionStrings:BillingDb", "Host=localhost;Database=qts;Username=app;Password=reviewed")),
            Deployed(contentRoot));

        Assert.Contains(failures, failure => failure.Contains("outside the application directory", StringComparison.Ordinal));

        Directory.Delete(contentRoot, recursive: true);
    }

    [Fact]
    public void T09_01d_the_harness_and_development_environments_keep_their_conveniences()
    {
        var contentRoot = Root("app");

        Assert.Empty(LocalSecurityPolicy.Evaluate(Configure(), Harness(contentRoot)));
        Assert.Empty(LocalSecurityPolicy.Evaluate(Configure(), Named("Development", contentRoot)));

        Directory.Delete(contentRoot, recursive: true);
    }

    [Fact]
    public void T09_01e_the_host_refuses_to_start_before_it_can_serve_any_business_data()
    {
        // A reviewed signing certificate and a persistent key directory are supplied so the only things left
        // unmet are the owned origin and the offline-grant key: this proves LocalSecurityPolicy.Enforce - not an
        // incidental earlier gate - stops the host. The same Program the integration suite drives is told it is a
        // deployed instance with no origin and no grant key.
        var certificate = WriteSigningCertificate();
        var keys = Root("keys");

        try
        {
            using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("QTS_OPENIDDICT_CERTIFICATE_PATH", certificate.Path);
                builder.UseSetting("QTS_OPENIDDICT_CERTIFICATE_PASSWORD", certificate.Password);
                builder.UseSetting(LocalSecurityPolicy.DataProtectionPathVariable, keys);
                builder.UseSetting(LocalSecurityPolicy.InstanceOriginVariable, string.Empty);
                builder.UseSetting(OfflineGrantKeyRing.PrivateKeyVariable, string.Empty);
            });

            var refused = Record.Exception(() => factory.CreateClient().GetAsync("/api/v1/customers").GetAwaiter().GetResult());

            Assert.NotNull(refused);
            Assert.Contains(
                Chain(refused!),
                message => message.Contains("Refusing to start outside a development environment", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(certificate.Path);
            Directory.Delete(keys, recursive: true);
        }

        static IEnumerable<string> Chain(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                yield return current.Message;
            }
        }
    }

    [Fact]
    public void T09_01f_a_deployed_host_with_nothing_at_all_configured_is_refused_by_the_first_gate_it_meets()
    {
        using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("QTS_OPENIDDICT_CERTIFICATE_PATH", string.Empty);
            builder.UseSetting(LocalSecurityPolicy.InstanceOriginVariable, string.Empty);
            builder.UseSetting(LocalSecurityPolicy.DataProtectionPathVariable, string.Empty);
            builder.UseSetting(OfflineGrantKeyRing.PrivateKeyVariable, string.Empty);
        });

        var refused = Record.Exception(() => factory.CreateClient().GetAsync("/api/v1/customers").GetAwaiter().GetResult());

        // Either gate answers - a token-signing key or the trust configuration - and neither one serves data.
        Assert.NotNull(refused);
        Assert.Contains(
            Chain(refused!),
            message => message.Contains("Refusing to start outside a development environment", StringComparison.Ordinal)
                || message.Contains("QTS_OPENIDDICT_CERTIFICATE_PATH", StringComparison.Ordinal));

        static IEnumerable<string> Chain(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                yield return current.Message;
            }
        }
    }

    // ---------------------------------------------------------------- T09.02

    [Fact]
    public async Task T09_02_a_cookie_mutation_without_the_proof_is_refused_and_changes_nothing()
    {
        var name = $"forged-{Guid.NewGuid():N}";
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        client.DefaultRequestHeaders.Remove(ApiHttp.AntiforgeryHeaderName);

        var refused = await client.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest(name, null, null, null));
        var body = await refused.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, CodeOf(body));
        Assert.Equal(0, await CountCustomersAsync(name));

        // The rejection was the missing proof, not a lost session: the same client with a proof still works.
        await PostgresApiFixture.AttachAntiforgeryAsync(client);
        var accepted = await client.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest(name, null, null, null));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();

        Assert.True(accepted.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"a same-origin mutation with the proof was refused: {(int)accepted.StatusCode} {acceptedBody}");
        Assert.Equal(1, await CountCustomersAsync(name));
    }

    [Fact]
    public async Task T09_02b_a_cookie_mutation_declaring_a_foreign_origin_is_refused()
    {
        var name = $"cross-origin-{Guid.NewGuid():N}";
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customers")
        {
            Content = JsonContent.Create(new SaveCustomerRequest(name, null, null, null)),
        };
        request.Headers.Add("Origin", "https://not-my-office.example");

        var refused = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, CodeOf(await refused.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountCustomersAsync(name));

        // A foreign Referer is the same forgery by another header.
        using var referred = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customers")
        {
            Content = JsonContent.Create(new SaveCustomerRequest(name, null, null, null)),
        };
        referred.Headers.Add("Referer", "https://not-my-office.example/steal.html");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(referred)).StatusCode);
        Assert.Equal(0, await CountCustomersAsync(name));
    }

    [Fact]
    public async Task T09_02c_the_proof_is_a_header_never_a_query_string_and_reads_need_none()
    {
        var anonymous = _fixture.CreateAnonymousClient();

        // A token endpoint that answered in a query string or a cacheable body would leak the proof.
        var issued = await anonymous.GetAsync(ApiHttp.AntiforgeryTokenEndpoint);
        issued.EnsureSuccessStatusCode();

        Assert.True(issued.Headers.CacheControl?.NoStore == true,
            $"the anti-forgery token response was cacheable: {issued.Headers.CacheControl}");

        using var payload = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var token = payload.RootElement.GetProperty("requestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(ApiHttp.AntiforgeryHeaderName, payload.RootElement.GetProperty("headerName").GetString());

        // The anti-forgery cookie is separate from the session cookie, HttpOnly and SameSite=Strict.
        var csrfCookie = (issued.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("qts.csrf", StringComparison.Ordinal)));
        Assert.Contains("HttpOnly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secure", csrfCookie, StringComparison.OrdinalIgnoreCase);

        // Reads are safe methods: no proof is demanded, and an anonymous read still answers with the typed 401.
        using var unproved = _fixture.CreateAnonymousClient();
        var session = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        session.DefaultRequestHeaders.Remove(ApiHttp.AntiforgeryHeaderName);

        var read = await session.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var anonymousRead = await unproved.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, CodeOf(await anonymousRead.Content.ReadAsStringAsync()));
    }

    // ---------------------------------------------------------------- T09.03

    [Fact]
    public async Task T09_03_metrics_answer_only_an_opted_in_local_or_currently_authorised_caller()
    {
        var deployed = Deployed(Root("app"));
        var harness = Harness(Root("harness"));

        // Enabled, but from a machine that is not the operator's: no management data.
        Assert.False(await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: IPAddress.Parse("10.0.0.55"), user: null),
            db: null!,
            Configure((LocalSecurityPolicy.MetricsEnabledVariable, "true")),
            deployed,
            CancellationToken.None));

        // Enabled and loopback: the operator's own machine may scrape it.
        Assert.True(await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: IPAddress.Loopback, user: null),
            db: null!,
            Configure((LocalSecurityPolicy.MetricsEnabledVariable, "true")),
            deployed,
            CancellationToken.None));

        // Not enabled at all: even the loopback caller gets nothing, so the surface is opt-in by default.
        Assert.False(await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: IPAddress.Loopback, user: null),
            db: null!,
            Configure(),
            deployed,
            CancellationToken.None));

        // The automated harness is the one place an in-memory host has no peer address at all, and only there.
        Assert.True(await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: null!, user: null),
            db: null!,
            Configure(),
            harness,
            CancellationToken.None));
        Assert.False(await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: null!, user: null),
            db: null!,
            Configure(),
            deployed,
            CancellationToken.None));

        // A remote caller holding audit.view right now may scrape; one whose role does not hold it may not.
        var owner = await _fixture.WithFreshContextAsync(async db => await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: IPAddress.Parse("10.0.0.55"), user: Principal(_fixture.Users.OwnerA)),
            db,
            Configure((LocalSecurityPolicy.MetricsEnabledVariable, "true")),
            deployed,
            CancellationToken.None));
        Assert.True(owner, "an owner holding audit.view was refused the management endpoint");

        var cashier = await _fixture.WithFreshContextAsync(async db => await LocalSecurityPolicy.MayScrapeMetricsAsync(
            Context(remote: IPAddress.Parse("10.0.0.55"), user: Principal(_fixture.Users.Cashier)),
            db,
            Configure((LocalSecurityPolicy.MetricsEnabledVariable, "true")),
            deployed,
            CancellationToken.None));
        Assert.False(cashier, "a cashier reached operational metrics");
    }

    [Fact]
    public async Task T09_03b_the_wire_answer_is_404_when_metrics_are_switched_off()
    {
        using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting(LocalSecurityPolicy.MetricsEnabledVariable, "false"));

        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);

        // And the enabled harness answer stays available to the operator's own loopback scrape.
        using var open = _fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await open.GetAsync("/metrics")).StatusCode);
    }

    // ---------------------------------------------------------------- T09.04

    [Fact]
    public void T09_04_a_persistent_key_ring_survives_a_restart_and_keeps_purposes_separate()
    {
        var contentRoot = Root("app");
        var keys = Root("keys");
        var configuration = Configure((LocalSecurityPolicy.DataProtectionPathVariable, keys));
        var environment = Deployed(contentRoot);

        // Two process lifetimes over one reviewed directory: what the first instance protected, the second reads.
        using var before = BuildRing(configuration, environment);
        var issued = before.GetDataProtector("QtsBilling.Session").Protect("fixture-session-payload");

        using var after = BuildRing(configuration, environment);
        Assert.Equal("fixture-session-payload", after.GetDataProtector("QtsBilling.Session").Unprotect(issued));

        // Separate purposes stay separate: a session payload is not a reset ticket, even inside one ring.
        Assert.ThrowsAny<CryptographicException>(() => after.GetDataProtector("QtsBilling.Ticket").Unprotect(issued));

        // The ring really is on disk, which is what the documented file backup restores.
        Assert.NotEmpty(Directory.GetFiles(keys, "*.xml"));

        Directory.Delete(contentRoot, recursive: true);
        Directory.Delete(keys, recursive: true);
    }

    [Fact]
    public void T09_04b_a_deployed_instance_without_a_persistent_ring_gets_no_ephemeral_fallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Assert.Throws<InvalidOperationException>(() => LocalSecurityPolicy.ConfigureDataProtection(
            services,
            Configure(),
            Deployed(Root("app"))));
    }

    [Fact]
    public void T09_04c_forwarded_headers_are_untrusted_unless_a_proxy_is_reviewed()
    {
        Assert.False(LocalSecurityPolicy.HasTrustedProxies(Configure()));
        Assert.True(LocalSecurityPolicy.HasTrustedProxies(Configure((LocalSecurityPolicy.TrustedProxiesVariable, "10.0.0.4"))));

        // With no reviewed proxy the forwarded headers stay off: a client cannot describe itself as the proxy.
        var ignoring = new ServiceCollection();
        ignoring.AddOptions();
        LocalSecurityPolicy.ConfigureForwardedHeaders(ignoring, Configure());
        Assert.Equal(ForwardedHeaders.None, Resolve<ForwardedHeadersOptions>(ignoring).ForwardedHeaders);

        var trusting = new ServiceCollection();
        trusting.AddOptions();
        LocalSecurityPolicy.ConfigureForwardedHeaders(trusting, Configure((LocalSecurityPolicy.TrustedProxiesVariable, "10.0.0.4")));
        var forwarded = Resolve<ForwardedHeadersOptions>(trusting);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
        Assert.Contains(IPAddress.Parse("10.0.0.4"), forwarded.KnownProxies);
        Assert.DoesNotContain(IPAddress.Parse("10.9.9.9"), forwarded.KnownProxies);

        // The host only answers the owned origin, plus any reviewed alias.
        var hosts = LocalSecurityPolicy.AllowedHosts(Configure(
            (LocalSecurityPolicy.InstanceOriginVariable, "https://pos.office.local:8443"),
            (LocalSecurityPolicy.AllowedHostsVariable, "qtsbilling.local")));
        Assert.Equal(new[] { "pos.office.local", "qtsbilling.local" }, hosts.ToArray());

        // A sandboxed frame is never same-origin; a same-address declaration always is.
        Assert.True(LocalSecurityPolicy.IsSameOriginRequest(Request("http://localhost", null, null)));
        Assert.True(LocalSecurityPolicy.IsSameOriginRequest(Request("https://pos.office.local:8443", "https://pos.office.local:8443", null)));
        Assert.False(LocalSecurityPolicy.IsSameOriginRequest(Request("https://pos.office.local:8443", "https://evil.example", null)));
        Assert.False(LocalSecurityPolicy.IsSameOriginRequest(Request("https://pos.office.local:8443", null, "https://evil.example/")));
        Assert.False(LocalSecurityPolicy.IsSameOriginRequest(Request("https://pos.office.local:8443", "null", null)));

        // A port difference is a different origin, and so is a different host.
        Assert.False(LocalSecurityPolicy.IsSameOriginRequest(Request("https://pos.office.local:8443", "https://pos.office.local:9000", null)));

        // Only the session-issuing path needs a proof before it has a principal; the OAuth protocol paths never do.
        Assert.True(LocalSecurityPolicy.IsSessionIssuingPath("/api/v1/auth/login"));
        Assert.False(LocalSecurityPolicy.IsSessionIssuingPath("/api/v1/auth/setup-ticket/complete"));
        Assert.True(LocalSecurityPolicy.IsOAuthProtocolPath("/connect/authorize"));
        Assert.True(LocalSecurityPolicy.IsOAuthProtocolPath("/connect/token"));
        Assert.True(LocalSecurityPolicy.IsOAuthProtocolPath("/.well-known/openid-configuration"));
        Assert.False(LocalSecurityPolicy.IsOAuthProtocolPath("/api/v1/customers"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A throwaway self-signed certificate for the OIDC gate only. It is never a real credential: it exists for
    /// the duration of one test and is deleted with it.
    /// </summary>
    private static (string Path, string Password) WriteSigningCertificate()
    {
        const string password = "fixture-only-not-a-secret";
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=qts-test-signing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            critical: false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var path = Path.Combine(Path.GetTempPath(), $"qts-t09-signing-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));

        return (path, password);
    }

    private static T Resolve<T>(IServiceCollection services) where T : class
        => services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<T>>().Value;

    private static ServiceProvider BuildRing(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        LocalSecurityPolicy.ConfigureDataProtection(services, configuration, environment);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(FixtureUser user) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(QtsClaims.BusinessId, user.BusinessId.ToString()),
        ],
        authenticationType: "TestAuth"));

    private static DefaultHttpContext Context(IPAddress remote, ClaimsPrincipal? user)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = remote;

        if (user is not null)
        {
            http.User = user;
        }

        return http;
    }

    private static HttpRequest Request(string url, string? origin, string? referer)
    {
        var http = new DefaultHttpContext();
        var uri = new Uri(url);
        http.Request.Scheme = uri.Scheme;
        http.Request.Host = new HostString(uri.Host, uri.Port);

        var request = http.Request;
        if (origin is not null)
        {
            request.Headers.Origin = origin;
        }

        if (referer is not null)
        {
            request.Headers.Referer = referer;
        }

        return request;
    }

    private async Task<int> CountCustomersAsync(string name)
        => await _fixture.WithFreshContextAsync(db => db.Customers
            .AsNoTracking()
            .CountAsync(customer => customer.Name == name));

    private static string CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
    }

    private static IConfiguration Configure(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    private static IWebHostEnvironment Deployed(string contentRoot) => Named("Production", contentRoot);

    private static IWebHostEnvironment Harness(string contentRoot) => Named("Testing", contentRoot);

    private static IWebHostEnvironment Named(string name, string contentRoot) => new HostEnvironment(name, contentRoot);

    private static string Root(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qts-t09-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A host environment for policy evaluation: the policy reads the name and the two root paths.</summary>
    private sealed class HostEnvironment : IWebHostEnvironment
    {
        public HostEnvironment(string environmentName, string contentRootPath)
        {
            EnvironmentName = environmentName;
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "QtsWebApp";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; }
    }
}
