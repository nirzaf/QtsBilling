using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Contracts.Sync;
using QtsWebApp.Features.Devices;
using QtsWebApp.Infrastructure;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The gate G2 exit dry run (specification requirements R01 and R03, journeys J01 and J10): a fresh empty
/// database, the operator bootstrap, the first owner sign-in, branch and user setup with a one-time handover
/// ticket, and a revoked terminal - all of it against the <b>real Kestrel listener over TLS</b> with the
/// production security policy active, and none of it against a hosted identity service, a CDN or a public URL.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory harness cannot answer two questions this gate turns on: does the deployed host start at all
/// when it is told it is a production instance, and does a browser-shaped client reach it through HTTPS with the
/// session, anti-forgery and management boundaries intact? So this suite launches the built application as its
/// own process with the reviewed configuration - owned origin, persistent key ring, signing certificate,
/// offline-grant key - and talks to it over a real socket.
/// </para>
/// <para>
/// The listener's certificate is generated for this run only. Accepting exactly that certificate is the
/// caller's local trust decision, which is what "locally trusted" means before an installer puts a real
/// certificate into the operating-system store (task 41). The database is a disposable <c>qts_test_</c>
/// database, migrated to the head, and is dropped afterwards.
/// </para>
/// </remarks>
public sealed class LocalAutonomyDryRunTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    /// <summary>
    /// The dry run's operator and cashier credentials are the documented disposable fixture password: it is only
    /// ever valid against the throwaway <c>qts_test_</c> database this run creates and drops, so no new credential
    /// shape enters the repository (requirement R15).
    /// </summary>
    private const string FixturePassword = FixtureUsers.Password;

    private const string OriginHost = "qts.autonomy.test";

    private static readonly string[] OrderedMigrations = [.. typeof(BillingDbContext).Assembly
        .GetTypes()
        .Where(type => type.IsSubclassOf(typeof(Migration)))
        .Select(type => type.GetCustomAttribute<MigrationAttribute>()!.Id)
        .OrderBy(id => id, StringComparer.Ordinal)];

    private readonly PostgresApiFixture _fixture = fixture;
    private UpgradeDatabase _database = null!;
    private LocalListener _listener = null!;
    private string _ownerEmail = string.Empty;
    private Guid _businessId;

    public async Task InitializeAsync()
    {
        _database = await _fixture.CreateUpgradeDatabaseAsync(OrderedMigrations[^1]).ConfigureAwait(false);

        // The instance is empty exactly as on a new installation, and the operator's command initialises it.
        using (var probe = new InstanceProbe(_database.ConnectionString))
        {
            Assert.False(await probe.AnyBusinessAsync().ConfigureAwait(false),
                "the dry run must start from an empty instance");
        }

        var instanceId = Guid.NewGuid();
        _ownerEmail = $"dryrun.owner-{instanceId:N}@autonomy.test";

        using (var host = new BootstrapHost(_database.ConnectionString))
        {
            var result = await LocalBootstrap.InitialiseAsync(host.Services, new BootstrapRequest(
                instanceId,
                _ownerEmail,
                FixturePassword,
                "Dry Run Trading",
                "Dry Run Trading FZ-LLC",
                "Main counter",
                "DR-MAIN",
                "QAR",
                "Asia/Qatar",
                SettlementScale: 2,
                FiscalYearStartMonth: 1,
                OpeningMode: BootstrapOpeningMode.NewBusiness,
                AcknowledgeZeroOpenings: true)).ConfigureAwait(false);

            Assert.True(result.Status == BootstrapStatus.Initialised,
                $"the operator bootstrap failed: {result.Status} {result.Message}");

            _businessId = result.BusinessId ?? Guid.Empty;
            Assert.NotEqual(Guid.Empty, _businessId);
        }

        _listener = await LocalListener.StartAsync(_database.ConnectionString).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task G2_01_the_first_owner_signs_in_over_locally_trusted_tls_and_the_boundaries_hold()
    {
        // 1. Signing in is a cookie mutation: without the session proof nothing happens at all.
        using (var anonymous = _listener.CreateClient())
        {
            var unproved = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
                new Dictionary<string, string> { ["email"] = _ownerEmail, ["password"] = FixturePassword });

            Assert.Equal(HttpStatusCode.BadRequest, unproved.StatusCode);
            Assert.Equal(ApiErrorCodes.ValidationFailed, CodeOf(await unproved.Content.ReadAsStringAsync()));
        }

        // 2. With the proof, the operator signs in over HTTPS and the business context becomes visible.
        using var owner = await SignedInClientAsync(_ownerEmail, FixturePassword);

        var session = await owner.GetAsync("/api/v1/auth/session");
        session.EnsureSuccessStatusCode();

        using (var document = JsonDocument.Parse(await session.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Dry Run Trading", document.RootElement.GetProperty("businessName").GetString());
            Assert.Contains("Owner", document.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        }

        // 3. There is no plaintext listener to talk to: the bound endpoint is HTTPS, so a cleartext request
        //    cannot be served at all rather than being downgraded. That is stronger than a redirect, and it is
        //    what the owned non-loopback https origin in the configuration is there to make unambiguous.
        using (var cleartext = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_listener.Port}/"),
            Timeout = TimeSpan.FromSeconds(20),
        })
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => cleartext.GetAsync("/"));
        }

        // 4. The host answers only the names the operator declared, not one a client invents.
        var forged = await _listener.SendRawHttpsAsync("/", hostHeader: "someone-elses-server.example");
        Assert.Contains("400", forged.Split("\r\n")[0], StringComparison.Ordinal);

        // 5. The production policy really was in force for this process: it would have refused to start without
        //    the owned origin, the persistent ring and the reviewed keys, and it named them in its refusal.
        var refused = LocalSecurityPolicy.Evaluate(
            Configuration(
                (LocalSecurityPolicy.InstanceOriginVariable, $"https://{OriginHost}:{_listener.Port}"),
                (LocalSecurityPolicy.DataProtectionPathVariable, string.Empty),
                ("QTS_OPENIDDICT_CERTIFICATE_PATH", string.Empty),
                (OfflineGrantKeyRing.PrivateKeyVariable, string.Empty)),
            new HostEnvironment("Production", Path.GetTempPath()));

        Assert.Contains(refused, failure => failure.Contains(LocalSecurityPolicy.DataProtectionPathVariable, StringComparison.Ordinal));
    }

    [Fact]
    public async Task G2_02_branch_setup_and_a_one_time_cashier_handover_work_with_no_hosted_service()
    {
        using var owner = await SignedInClientAsync(_ownerEmail, FixturePassword);

        // 1. Business identity and numbering are versioned operator setup, reachable on the running host.
        var settings = await owner.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/business-settings", ContractJson.Options);
        Assert.NotNull(settings);
        Assert.Equal(_businessId, settings!.BusinessId);
        Assert.True(settings.Version >= 1, "operator setup must be versioned before it can be saved");

        var saved = await owner.PutAsJsonAsync("/api/v1/business-settings", new SaveBusinessSettingsRequest(
            settings.Version,
            "Dry Run Trading",
            "Dry Run Trading FZ-LLC",
            "Asia/Qatar",
            FiscalYearStartMonth: 4,
            BaseCurrency: null,
            SettlementScale: null,
            Numbering: new NumberingSettingsDto("INV-", 5)));

        var savedBody = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.IsSuccessStatusCode, $"business setup was refused: {(int)saved.StatusCode} {savedBody}");

        // 2. A second branch is created and listed; no branch is ever deleted.
        var branch = await owner.PostAsJsonAsync("/api/v1/locations", new CreateLocationRequest("Second counter", "DR-TWO", null, null));
        var branchBody = await branch.Content.ReadAsStringAsync();
        Assert.True(branch.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"a branch could not be created: {(int)branch.StatusCode} {branchBody}");

        var afterBranch = await owner.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/business-settings", ContractJson.Options);
        Assert.Equal(2, afterBranch!.Locations.Count);

        // 3. A cashier is hired with a one-time ticket and works inside their own limits.
        var email = $"dryrun.cashier-{Guid.NewGuid():N}@autonomy.test";
        var hired = await owner.PostAsJsonAsync("/api/v1/administration/users", new CreateAdministrationUserRequest(
            email,
            "Dry Run Cashier",
            "Cashier",
            AllLocations: false,
            LocationIds: [afterBranch.Locations[1].Id],
            MaxDiscountRate: 0m,
            Reason: "gate G2 dry run"));

        Assert.Equal(HttpStatusCode.Created, hired.StatusCode);
        var created = await hired.Content.ReadFromJsonAsync<CreateAdministrationUserResultDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the hire returned no payload");
        Assert.True(created.Ticket.Secret.Length >= 22, "the handover secret must carry at least 128 bits");

        // The handover is completed by the new person, from a client with no session of its own.
        using (var anonymous = _listener.CreateClient())
        {
            var redeemed = await anonymous.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
                new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, FixturePassword));
            Assert.True(redeemed.IsSuccessStatusCode,
                $"the ticket could not be redeemed: {(int)redeemed.StatusCode} {await redeemed.Content.ReadAsStringAsync()}");
        }

        using var cashier = await SignedInClientAsync(email, FixturePassword);
        var posted = await cashier.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest("Walk-in counter customer", null, null, null));
        Assert.True(posted.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"a cashier with sales authority was refused: {(int)posted.StatusCode} {await posted.Content.ReadAsStringAsync()}");

        var refusedAdministration = await cashier.GetAsync("/api/v1/administration/users");
        Assert.Equal(HttpStatusCode.Forbidden, refusedAdministration.StatusCode);

        // 4. Operational metrics are not part of the profile unless the operator switches them on.
        using var scrape = _listener.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await scrape.GetAsync("/metrics")).StatusCode);

        // 5. Nothing on the served page tells a browser to fetch a public host.
        using var shell = _listener.CreateClient();
        var page = await shell.GetStringAsync("/");
        var external = ExternalHosts(page);
        Assert.True(external.Count == 0, $"the served document references public hosts: {string.Join(", ", external)}");
    }

    [Fact]
    public async Task G2_03_a_revoked_terminal_loses_offline_authority_on_the_running_host()
    {
        using var owner = await SignedInClientAsync(_ownerEmail, FixturePassword);
        var settings = await owner.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/business-settings", ContractJson.Options);
        var locationId = settings!.Locations[0].Id;

        var challenge = await owner.PostAsJsonAsync("/api/v1/devices/enrolment-challenges", new { locationId });
        challenge.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await challenge.Content.ReadAsStringAsync()).RootElement
            .GetProperty("enrolmentCode").GetString()!;

        var enrolled = await owner.PostAsJsonAsync("/api/v1/devices", new
        {
            name = "Dry run till",
            platform = "windows",
            locationId,
            enrolmentCode = code,
        });

        var enrolledBody = await enrolled.Content.ReadAsStringAsync();
        Assert.True(enrolled.IsSuccessStatusCode, $"the terminal could not be enrolled: {(int)enrolled.StatusCode} {enrolledBody}");

        var deviceId = Guid.Parse(enrolled.Headers.Location!.OriginalString.TrimEnd('/').Split('/')[^1]);

        // The grant verification key is published by this instance to its own authenticated terminals, so a
        // device can check offline grants without any hosted trust service or public JWKS endpoint.
        using (var anonymous = _listener.CreateClient())
        {
            var unauthenticated = await anonymous.GetAsync("/api/v1/devices/offline-grant-key");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }

        var published = await owner.GetAsync("/api/v1/devices/offline-grant-key");
        var publishedBody = await published.Content.ReadAsStringAsync();
        Assert.True(published.IsSuccessStatusCode, $"the grant key was not published: {(int)published.StatusCode} {publishedBody}");

        using (var keyDocument = JsonDocument.Parse(publishedBody))
        {
            Assert.False(string.IsNullOrWhiteSpace(keyDocument.RootElement.GetProperty("publicKey").GetString()),
                "the published grant verification key was empty");
            Assert.False(string.IsNullOrWhiteSpace(keyDocument.RootElement.GetProperty("keyId").GetString()),
                "the published grant key had no identifier to pin");
        }

        // A revoked terminal is refused, and the refusal comes from this host's own policy.
        var version = await DeviceVersionAsync(deviceId);
        var revoked = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/revoke",
            new RevokeDeviceRequest(version, "dry run: terminal withdrawn"));
        Assert.True(revoked.IsSuccessStatusCode, $"revocation failed: {(int)revoked.StatusCode} {await revoked.Content.ReadAsStringAsync()}");

        var detail = await owner.GetFromJsonAsync<DeviceInfoDto>($"/api/v1/devices/{deviceId}", ContractJson.Options);
        Assert.NotNull(detail);
        Assert.Equal("Revoked", detail!.Status);
        Assert.NotNull(detail.RevokedAtUtc);

        var grantAfterRevocation = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/offline-grant",
            new { locationId, registerSessionId = (Guid?)null });
        Assert.NotEqual(HttpStatusCode.OK, grantAfterRevocation.StatusCode);
        Assert.True((int)grantAfterRevocation.StatusCode >= 400,
            $"a revoked terminal was handed a grant: {(int)grantAfterRevocation.StatusCode}");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<HttpClient> SignedInClientAsync(string email, string password)
    {
        var client = _listener.CreateClient();
        await PostgresApiFixture.AttachAntiforgeryAsync(client).ConfigureAwait(false);

        var signIn = await client.PostAsJsonAsync("/api/v1/auth/login",
            new Dictionary<string, string> { ["email"] = email, ["password"] = password }).ConfigureAwait(false);

        if (!signIn.IsSuccessStatusCode)
        {
            var body = await signIn.Content.ReadAsStringAsync().ConfigureAwait(false);
            client.Dispose();
            throw new InvalidOperationException($"the dry run could not sign in as {email}: {(int)signIn.StatusCode} {body}");
        }

        // The proof the login needed belongs to the anonymous session; the signed-in session gets its own.
        await PostgresApiFixture.AttachAntiforgeryAsync(client).ConfigureAwait(false);
        return client;
    }

    private async Task<long> DeviceVersionAsync(Guid deviceId)
    {
        await using var scope = ProgramScope(_database.ConnectionString);
        var db = scope.GetRequiredService<BillingDbContext>();

        return await db.DeviceRegistrations.AsNoTracking()
            .Where(device => device.Id == deviceId)
            .Select(device => device.Version)
            .SingleAsync();
    }

    private static List<string> ExternalHosts(string document)
    {
        var hosts = new List<string>();

        foreach (Match match in Regex.Matches(document, "https?://([^/\"'\\s<>]+)", RegexOptions.IgnoreCase))
        {
            var host = match.Groups[1].Value.Split(':')[0];

            if (host is "127.0.0.1" or "localhost" or "::1" || host.Equals(OriginHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Namespace and schema identifiers inside markup are never fetched by a browser.
            if (host.EndsWith(".w3.org", StringComparison.OrdinalIgnoreCase)
                || host.Equals("schemas.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || host.Contains("schema.org", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hosts.Add(match.Value);
        }

        return hosts;
    }

    private static string CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration(params (string Key, string Value)[] values)
        => new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    private static ServiceProvider ProgramScope(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BillingDbContext>(options => options.UseNpgsql(connectionString));
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the same service entry point the operator command calls, against the disposable database.</summary>
    private sealed class BootstrapHost(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(PostgresServiceCollectionExtensions.ConnectionStringVariable, connectionString);
        }
    }

    /// <summary>Reads the instance so the dry run can prove it started from an empty database.</summary>
    private sealed class InstanceProbe(string connectionString) : IDisposable
    {
        private readonly ServiceProvider _services = ProgramScope(connectionString);

        public async Task<bool> AnyBusinessAsync()
        {
            var db = _services.GetRequiredService<BillingDbContext>();
            return await db.Businesses.AsNoTracking().AnyAsync();
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class HostEnvironment(string environmentName, string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "QtsWebApp";

        public string ContentRootPath { get; set; } = contentRootPath;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// The built application, started as its own process against a real Kestrel HTTPS endpoint with the
    /// production security policy satisfied by generated local material.
    /// </summary>
    private sealed class LocalListener
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new();
        private readonly string _material;
        private readonly string _thumbprint;

        private LocalListener(Process process, int port, string material, string thumbprint)
        {
            _process = process;
            Port = port;
            _material = material;
            _thumbprint = thumbprint;
        }

        public int Port { get; }

        public static async Task<LocalListener> StartAsync(string connectionString)
        {
            var dll = LocateApplicationDll();
            var material = Path.Combine(Path.GetTempPath(), $"qts-g2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(material);
            Directory.CreateDirectory(Path.Combine(material, "keys"));

            const string passphrase = "local-dry-run-passphrase";
            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={OriginHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
                critical: false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(4));
            var package = generated.Export(X509ContentType.Pfx, passphrase);
            var certificatePath = Path.Combine(material, "signing.pfx");
            File.WriteAllBytes(certificatePath, package);

            string thumbprint;
            using (var loaded = X509CertificateLoader.LoadPkcs12(package, passphrase))
            {
                thumbprint = loaded.Thumbprint;
            }

            var grantKey = Path.Combine(material, "grant.pem");
            File.WriteAllText(grantKey, key.ExportPkcs8PrivateKeyPem());

            var port = FreePort();

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(dll)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add(dll);

            // The reviewed production profile: an owned non-loopback origin, a persistent ring outside the
            // application directory, a signing certificate and an offline-grant key. Nothing here is a
            // development convenience switched on - this is the shape an operator's configuration takes, and
            // LocalSecurityPolicy.Enforce is what lets the process reach a listener at all.
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            startInfo.Environment["ASPNETCORE_URLS"] = $"https://127.0.0.1:{port}";
            startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = certificatePath;
            startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = passphrase;

            // The token signing certificate is the reviewed one for this instance. The dry run generates it, so
            // it is a run-local stand-in for the certificate an operator provisions and stores (task 41).
            startInfo.Environment["QTS_OPENIDDICT_CERTIFICATE_PATH"] = certificatePath;
            startInfo.Environment["QTS_OPENIDDICT_CERTIFICATE_PASSWORD"] = passphrase;
            startInfo.Environment[LocalSecurityPolicy.InstanceOriginVariable] = $"https://{OriginHost}:{port}";
            startInfo.Environment[LocalSecurityPolicy.AllowedHostsVariable] = "127.0.0.1";
            startInfo.Environment[LocalSecurityPolicy.DataProtectionPathVariable] = Path.Combine(material, "keys");
            startInfo.Environment[OfflineGrantKeyRing.PrivateKeyVariable] = grantKey;
            startInfo.Environment[LocalSecurityPolicy.MetricsEnabledVariable] = "false";
            startInfo.Environment[PostgresServiceCollectionExtensions.ConnectionStringVariable] = connectionString;

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the application process did not start");

            var listener = new LocalListener(process, port, material, thumbprint);
            listener.Capture(process);

            // Readiness: the real listener reports what it bound, or the process dies with its own reason.
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"the application exited before it listened (code {process.ExitCode}):\n{listener.Dump()}");
                }

                if (listener.Dump().Contains("Now listening on", StringComparison.Ordinal))
                {
                    await listener.WaitForDiscoveryAsync().ConfigureAwait(false);
                    return listener;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            await listener.StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"the application never started listening:\n{listener.Dump()}");
        }

        public HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AllowAutoRedirect = false,

                // The listener's certificate is generated for this run: the caller accepts exactly this one,
                // which is the local trust decision an installer later makes for the operating-system store.
                ServerCertificateCustomValidationCallback = (_, certificate2, _, _) =>
                    certificate2 is not null && certificate2.Thumbprint == _thumbprint,
            };

            return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{Port}/") };
        }

        /// <summary>
        /// Sends one HTTP request with a chosen Host header over the real TLS connection. An ordinary client
        /// cannot be made to lie about its authority, so the host filter is proven with a socket instead.
        /// </summary>
        public async Task<string> SendRawHttpsAsync(string path, string hostHeader)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);

            using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false), false, (_, _, _, _) => true);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = OriginHost })
                .ConfigureAwait(false);

            var payload = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {hostHeader}\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        public string Dump()
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }

        private void Capture(Process process)
        {
            DataReceivedEventHandler handler = (_, arguments) =>
            {
                if (arguments.Data is null)
                {
                    return;
                }

                lock (_log)
                {
                    _log.AppendLine(arguments.Data);
                }
            };

            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private async Task WaitForDiscoveryAsync()
        {
            using var client = CreateClient();
            var deadline = DateTime.UtcNow.AddMinutes(1);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var discovery = await client.GetAsync("/.well-known/openid-configuration").ConfigureAwait(false);
                    if (discovery.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // The listener is up but has not finished registering its endpoints.
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"the running host never served OIDC discovery:\n{Dump()}");
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static string LocateApplicationDll()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QtsBilling.sln")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName
                ?? throw new InvalidOperationException($"the repository root was not found from {AppContext.BaseDirectory}");

            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(root, "QtsWebApp", "QtsWebApp", "bin", configuration, "net10.0", "QtsWebApp.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"QtsWebApp.dll has not been built; run 'dotnet build QtsWebApp/QtsWebApp/QtsWebApp.csproj -c Release' first (looked under {root})");
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);

            try
            {
                Directory.Delete(_material, recursive: true);
            }
            catch (IOException)
            {
                // A transient handle must not fail the dry run: the material is under %TEMP% and holds no data.
            }
        }

        private async Task StopAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            _process.Dispose();
        }
    }
}
