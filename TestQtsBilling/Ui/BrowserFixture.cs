#if QTS_BROWSER_TESTS
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using TestQtsBilling.Integration;
#endif

namespace TestQtsBilling.Ui;

/// <summary>
/// The shared support for browser journeys (specification task 02). Its only always-compiled job is to name
/// the browser the kit must have preloaded: a journey never downloads a browser during a test run, and a
/// missing prerequisite is reported as a blocked run instead of a pass.
/// </summary>
public static class BrowserTestSupport
{
    /// <summary>Environment variable naming the locally preloaded browser executable.</summary>
    public const string BrowserPathVariable = "QTS_BROWSER_EXECUTABLE";

    public static string BrowserPath => Environment.GetEnvironmentVariable(BrowserPathVariable) ?? string.Empty;

    public static bool IsBrowserPreloaded => BrowserPath.Length > 0 && File.Exists(BrowserPath);

    public static string MissingBrowserMessage =>
        $"BLOCKED: no test browser is preloaded. Set {BrowserPathVariable} to the browser that "
        + "scripts/prepare-build-kit.ps1 ships in the offline test kit (specification tasks 11 and 45). "
        + "Browser journeys never fetch a browser at run time.";
}

#if QTS_BROWSER_TESTS
/// <summary>Shares one owned host, one disposable database and one browser across the journey class.</summary>
[CollectionDefinition(nameof(BrowserHarnessCollection))]
public sealed class BrowserHarnessCollection : ICollectionFixture<BrowserFixture>;

/// <summary>
/// An owned loopback host bound to a marked <c>qts_test_…</c> database, plus the preinstalled test browser.
/// It records the origin it actually reached, talks only to that origin, and terminates only the process it
/// started. Nothing here knows a deployment URL, and nothing here can touch a real business database: the
/// database comes from the same fixture the API integration tests use, including its refusal rules.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private readonly PostgresApiFixture _database = new();
    private readonly List<IBrowserContext> _contexts = [];
    private Process? _hostProcess;
    private IPlaywright? _playwright;
    private Guid _draftInvoiceId;
    private Guid _directGoodsInvoiceId;

    /// <summary>The loopback origin the harness published and proved ready.</summary>
    public string Origin { get; private set; } = string.Empty;

    public FixtureUser Owner => _database.Users.OwnerA;

    public FixtureUser Cashier => _database.Users.Cashier;

    /// <summary>A draft the operator may edit and issue.</summary>
    public Guid DraftInvoiceId => _draftInvoiceId;

    /// <summary>A draft whose economic mode is not the form default.</summary>
    public Guid DirectGoodsInvoiceId => _directGoodsInvoiceId;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync().ConfigureAwait(false);

        if (!BrowserTestSupport.IsBrowserPreloaded)
        {
            throw new InvalidOperationException(BrowserTestSupport.MissingBrowserMessage);
        }

        _hostProcess = await StartHostAsync().ConfigureAwait(false);
        await WaitForReadinessAsync().ConfigureAwait(false);

        var (locationId, variantId) = await ReadSeedAnchorsAsync().ConfigureAwait(false);

        _draftInvoiceId = await CreateInvoiceAsync(
            "Browser journey draft",
            QtsBilling.Contracts.Invoices.InvoiceRecognitionMode.Service,
            locationId: null,
            variantId: null,
            note: null).ConfigureAwait(false);

        _directGoodsInvoiceId = await CreateInvoiceAsync(
            "Browser journey goods",
            QtsBilling.Contracts.Invoices.InvoiceRecognitionMode.DirectGoods,
            locationId,
            variantId,
            note: "created before the journey").ConfigureAwait(false);

        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserTestSupport.BrowserPath,
            // Loopback only: the owned host presents its development certificate.
            Args = ["--ignore-certificate-errors"],
        }).ConfigureAwait(false);
    }

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Opens a page that already carries the session cookie of the given fixture user.</summary>
    public async Task<IPage> CreateSignedInPageAsync(FixtureUser user)
    {
        var cookies = await ReadSessionCookiesAsync(user).ConfigureAwait(false);

        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        }).ConfigureAwait(false);

        _contexts.Add(context);

        await context.AddCookiesAsync(cookies).ConfigureAwait(false);

        return await context.NewPageAsync().ConfigureAwait(false);
    }

    /// <summary>Removes a response delay installed by a journey so the next request is immediate.</summary>
    public Task UnstallRequestDelay(IPage page) => page.UnrouteAsync("**/api/v1/invoices**");

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.CloseAsync().ConfigureAwait(false);
        }

        _contexts.Clear();

        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();

        if (_hostProcess is { } host)
        {
            try
            {
                if (!host.HasExited)
                {
                    // Only this child (and its own children) are stopped: the harness never kills a foreign process.
                    host.Kill(entireProcessTree: true);
                    await host.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // The process already left; nothing of ours remains.
            }

            host.Dispose();
        }

        await _database.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the owned host

    private async Task<Process> StartHostAsync()
    {
        var httpPort = ReserveLoopbackPort();
        var httpsPort = ReserveLoopbackPort();
        Origin = $"https://127.0.0.1:{httpsPort}";

        var dataProtection = Path.Combine(Path.GetTempPath(), $"qts-browser-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataProtection);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            Arguments = "run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj -c Release --no-build",
            WorkingDirectory = InvoiceEditorTests.RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // "Testing" is the host's own no-dev-seeder environment: OpenIddict may still use its development
        // certificates, but the development demo data is never written into a fixture database.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort};https://127.0.0.1:{httpsPort}";
        startInfo.Environment["QTS_POSTGRES_CONNECTION"] = _database.ConnectionString;
        startInfo.Environment["ConnectionStrings__BillingDb"] = _database.ConnectionString;
        startInfo.Environment["QTS_DATAPROTECTION_PATH"] = dataProtection;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The owned loopback host did not start.");

        process.OutputDataReceived += (_, args) => StartupLog.AppendLine(args.Data);
        process.ErrorDataReceived += (_, args) => StartupLog.AppendLine(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _hostProcess = process;
        return process;
    }

    private StringBuilder StartupLog { get; } = new();

    private async Task WaitForReadinessAsync()
    {
        // The host always redirects to HTTPS, so the journey origin is the loopback listener itself. The
        // certificate is the host's development certificate: trusting it here is a loopback test concern and
        // never a substitute for the production trust checks owned by Tasks 38 and 45.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var probe = new HttpClient(handler) { BaseAddress = new Uri(Origin) };

        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await probe.GetAsync("/api/v1/health").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener is still coming up.
            }
            catch (TaskCanceledException)
            {
                // The first probe of a starting host can time out.
            }

            if (_hostProcess?.HasExited == true)
            {
                throw new InvalidOperationException($"the owned host stopped answering: {StartupLog}");
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"the owned host never reported healthy at {Origin} within 60 seconds.");
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    // ------------------------------------------------------------------ the fixture data

    private async Task<(Guid LocationId, Guid VariantId)> ReadSeedAnchorsAsync()
    {
        var anchors = await _database.WithFreshContextAsync(async db =>
        {
            var businessId = Owner.BusinessId;

            var location = await db.BusinessLocations
                .Where(row => row.BusinessId == businessId)
                .Select(row => (Guid?)row.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            var variant = await db.ProductVariants
                .Where(row => row.BusinessId == businessId)
                .Select(row => (Guid?)row.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            return (location, variant);
        }).ConfigureAwait(false);

        if (anchors.location is not { } locationId || anchors.variant is not { } variantId)
        {
            throw new InvalidOperationException(
                "the fixture database carries no location and stock variant, so a direct-goods draft cannot be seeded.");
        }

        return (locationId, variantId);
    }

    private async Task<Guid> CreateInvoiceAsync(
        string description,
        QtsBilling.Contracts.Invoices.InvoiceRecognitionMode mode,
        Guid? locationId,
        Guid? variantId,
        string? note)
    {
        using var client = await _database.CreateAuthenticatedClientAsync(Owner).ConfigureAwait(false);

        var request = new QtsBilling.Contracts.Invoices.SaveInvoiceRequest(
            locationId,
            CustomerId: null,
            IssueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            note,
            Lines: [new QtsBilling.Contracts.Invoices.InvoiceLineRequest(variantId, description, 1m, 100m, 0m, 0.15m)],
            RecognitionMode: mode);

        var response = await client.PostAsJsonAsync("/api/v1/invoices", request).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the fixture could not seed a {mode} draft: {(int)response.StatusCode} "
                + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        var created = await response.Content
            .ReadFromJsonAsync<QtsBilling.Contracts.Invoices.InvoiceDetailDto>(
                QtsBilling.Contracts.Serialization.ContractJson.Options)
            .ConfigureAwait(false);

        return created?.Invoice.Id
            ?? throw new InvalidOperationException($"the seeded {mode} draft came back empty.");
    }

    private async Task<List<Microsoft.Playwright.Cookie>> ReadSessionCookiesAsync(FixtureUser user)
    {
        using var client = _database.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        // Signing in is a cookie mutation from task 09 onwards, and no cookie jar is attached to this client, so
        // the anti-forgery pair is carried by hand: the proof is fetched, its cookie is replayed on the login, and
        // both cookies are handed to the browser along with the session cookie the login issued.
        var tokenResponse = await client.GetAsync(QtsBilling.Contracts.Serialization.ApiHttp.AntiforgeryTokenEndpoint).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();

        using var payload = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        var proof = payload.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("the csrf-token response carried no request token.");

        var antiforgeryCookies = ReadCookiePairs(tokenResponse);

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new Dictionary<string, string>
                {
                    ["email"] = user.Email,
                    ["password"] = TestQtsBilling.Integration.FixtureUsers.Password,
                },
                mediaType: null,
                options: QtsBilling.Contracts.Serialization.ContractJson.Options),
        };

        login.Headers.Add(QtsBilling.Contracts.Serialization.ApiHttp.AntiforgeryHeaderName, proof);

        foreach (var pair in antiforgeryCookies)
        {
            login.Headers.Add("Cookie", $"{pair.Key}={pair.Value}");
        }

        var response = await client.SendAsync(login).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var issued = antiforgeryCookies.Concat(ReadCookiePairs(response))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        if (issued.Count == 0)
        {
            throw new InvalidOperationException("the host answered the login without a session cookie.");
        }

        return [.. issued
            .Select(pair => new Microsoft.Playwright.Cookie
            {
                Name = pair.Key,
                Value = pair.Value,
                Url = Origin,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Strict,
            })];
    }

    /// <summary>
    /// Cookies a response issued, as name/value pairs. Attributes are dropped because the browser receives them
    /// from the Playwright cookie model instead.
    /// </summary>
    private static List<KeyValuePair<string, string>> ReadCookiePairs(HttpResponseMessage response)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return pairs;
        }

        foreach (var raw in values)
        {
            var assignment = raw.Split(';')[0].Split('=', 2);
            if (assignment.Length == 2 && !string.IsNullOrWhiteSpace(assignment[0]))
            {
                pairs.Add(new KeyValuePair<string, string>(assignment[0].Trim(), assignment[1].Trim()));
            }
        }

        return pairs;
    }
}
#endif
