using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using OpenTelemetry.Metrics;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using QtsBilling.Contracts.Abstractions;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Components;
using QtsWebApp.Features.Auth;
using QtsWebApp.Features.Administration;
using QtsWebApp.Features.Banking;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Audit;
using QtsWebApp.Features.Catalog;
using QtsWebApp.Features.Customers;
using QtsWebApp.Features.Devices;
using QtsWebApp.Features.Health;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Features.Invoices;
using QtsWebApp.Features.Expenses;
using QtsWebApp.Features.Pricing;
using QtsWebApp.Features.Purchasing;
using QtsWebApp.Features.Orders;
using QtsWebApp.Features.Reporting;
using QtsWebApp.Features.Sales;
using QtsWebApp.Features.Sync;
using QtsWebApp.Infrastructure;
using static MudBlazor.Defaults.Classes.Position;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = TopCenter;
    options.SnackbarConfiguration.PreventDuplicates = true;
    options.SnackbarConfiguration.NewestOnTop = true;
});

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(BillingMetrics.MeterName)
        .AddPrometheusExporter());

// PostgreSQL 18 is the single authoritative store (QB-03). Credentials stay server-side.
// The connection string is read from the configuration, so a test host can point the same code at its
// own disposable database; in every real deployment the environment variable keeps the precedence.
var billingConnectionString = builder.Configuration[PostgresServiceCollectionExtensions.ConnectionStringVariable] is { Length: > 0 } configured
    ? configured
    : PostgresServiceCollectionExtensions.ResolveConnectionString();

builder.Services.AddQtsPostgresBilling(billingConnectionString);

// ---------------------------------------------------------------------------------------------
// Identity (QB-04): no raw-password lookups, lockout on repeated failures, safe account enrolment.
// ---------------------------------------------------------------------------------------------
builder.Services
    .AddIdentityCore<User>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<QtsClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<BillingDbContext>();

builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = AuthEndpoints.ResetTicketLifetime);

builder.Services.AddAuthentication(options =>
    {
        // One request is authenticated by one scheme: a bearer header selects the native access token,
        // otherwise the browser session cookie. The two principals are never merged.
        options.DefaultScheme = "QtsAuth";
        options.DefaultAuthenticateScheme = "QtsAuth";
        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddIdentityCookies();

builder.Services.AddAuthentication()
    .AddPolicyScheme("QtsAuth", "browser cookie or native bearer token", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                : IdentityConstants.ApplicationScheme;
    });

// Native identity (QB-22): the Windows public client uses an authorization code with PKCE. There is
// no client secret and no password grant; the token and discovery endpoints are OpenIddict's own.
builder.Services.AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<BillingDbContext>())
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetRevocationEndpointUris("/connect/revoke");

        options.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow()
            .RequireProofKeyForCodeExchange();

        options.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.OfflineAccess,
            NativeAuthorizationEndpoints.ApiScope);

        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
        options.SetRefreshTokenLifetime(TimeSpan.FromHours(8));

        // A redeemed refresh token cannot be replayed, not even immediately: reuse is treated as theft
        // and revokes the chain.
        options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

        var signingCertificatePath = builder.Configuration["QTS_OPENIDDICT_CERTIFICATE_PATH"];

        if (!string.IsNullOrWhiteSpace(signingCertificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                signingCertificatePath,
                builder.Configuration["QTS_OPENIDDICT_CERTIFICATE_PASSWORD"]);

            options.AddSigningCertificate(certificate).AddEncryptionCertificate(certificate);
        }
        else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            options.AddDevelopmentSigningCertificate();
            options.AddDevelopmentEncryptionCertificate();
        }
        else
        {
            // Fail closed: a production host never signs tokens with an ephemeral development key.
            throw new InvalidOperationException(
                "QTS_OPENIDDICT_CERTIFICATE_PATH must point to the reviewed signing certificate outside development.");
        }

        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            options.UseAspNetCore()
                .EnableAuthorizationEndpointPassthrough()
                .DisableTransportSecurityRequirement();
        }
        else
        {
            options.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
        }
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

builder.Services.AddAuthorization(QtsPolicies.Configure);
// The handler reads the current membership of the actor, so it needs a scoped context (never a
// singleton DbContext).
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth-strict", context =>
    {
        var partition = context.Items.TryGetValue(AuthRateLimitKeyMiddleware.PartitionKeyItem, out var value)
            ? value?.ToString()
            : null;
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        return AuthRateLimitKeyMiddleware.CreatePartition(partition ?? ip);
    });

    // OpenIddict owns /connect/token's endpoint, so the global limiter applies only to that path.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!string.Equals(context.Request.Path.Value, "/connect/token", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("not-auth-token");
        }

        var partition = context.Items.TryGetValue(AuthRateLimitKeyMiddleware.PartitionKeyItem, out var value)
            ? value?.ToString()
            : null;
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        return AuthRateLimitKeyMiddleware.CreatePartition(partition ?? ip);
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "https://httpstatuses.com/429",
            ["title"] = "Too many authentication attempts.",
            ["status"] = StatusCodes.Status429TooManyRequests,
            ["detail"] = "Wait before retrying this authentication request.",
            ["code"] = "rate_limited",
        }), cancellationToken).ConfigureAwait(false);
    };
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "qts.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;

    // CSRF defence: SameSite=Strict plus no cross-origin CORS means a hostile site can never ride the
    // session cookie, even for state-changing verbs (QB-04 checks). Explicit anti-forgery proof is demanded
    // on top of that for every cookie-authenticated mutation (task 09), so the defence does not rest on one flag.
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = LocalSecurityPolicy.AllowsDevelopmentConveniences(builder.Environment)
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    options.Events.OnRedirectToLogin = async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Typed 401 for API callers instead of an empty status or an HTML login redirect (QB-14).
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["type"] = "https://httpstatuses.com/401",
                ["title"] = "Authentication is required.",
                ["status"] = StatusCodes.Status401Unauthorized,
                ["detail"] = "Sign in through the session endpoint and retry with the session cookie.",
                ["code"] = ApiErrorCodes.Unauthenticated,
            });

            return;
        }

        context.Response.Redirect(context.RedirectUri);
    };

    options.Events.OnRedirectToAccessDenied = async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Typed 403: a signed-in principal without the permission gets a machine-readable code.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["type"] = "https://httpstatuses.com/403",
                ["title"] = "The signed-in user may not perform this action.",
                ["status"] = StatusCodes.Status403Forbidden,
                ["detail"] = "The required permission is missing from this membership; ask an owner to review the role.",
                ["code"] = ApiErrorCodes.Forbidden,
            });

            return;
        }

        context.Response.Redirect(context.RedirectUri);
    };
});

// API plumbing (QB-05) and the typed API clients used by server prerender and the browser.
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddAntiforgery(options => LocalSecurityPolicy.ConfigureAntiforgery(options, builder.Environment));
builder.Services.ConfigureHttpJsonOptions(options => QtsBilling.Contracts.Serialization.ContractJson.Apply(options.SerializerOptions));
builder.Services.AddOpenApi();
builder.Services.AddTransient<LocalApiHandler>();

// The browser host never captures sales offline (QB-19): the shared point-of-sale component asks this
// service, so it shows an honest "desktop only" state instead of a fake capture.
builder.Services.AddScoped<QtsBilling.Contracts.Pos.ILocalPosService, QtsWebApp.Features.Pos.WebLocalPosService>();
builder.Services.TryAddScoped<IPaymentTerminalProvider, DisabledPaymentTerminalProvider>();

// Key-ring persistence (QB-14, task 09): QTS_DATAPROTECTION_PATH points the ring at a private, backed-up
// directory so session-cookie and ticket keys survive restarts and rotation is a deliberate, reviewed
// operation. A development or harness ring stays ephemeral, and a deployed instance without one is refused
// by LocalSecurityPolicy.Enforce below rather than silently regenerating keys. No key material is ever
// written inside the application directory, where it would be servable and deployable over.
LocalSecurityPolicy.ConfigureDataProtection(builder.Services, builder.Configuration, builder.Environment);

// Forwarded headers are trusted only for an explicitly configured local proxy, and the Host header only for
// the owned origin (plus any reviewed alias). With neither configured, a client cannot describe itself as the
// office proxy or borrow another name (task 09, step 2).
LocalSecurityPolicy.ConfigureForwardedHeaders(builder.Services, builder.Configuration);
LocalSecurityPolicy.ConfigureHostFiltering(builder.Services, builder.Configuration);

builder.Services.AddHttpClient<ICustomerApiClient, CustomerApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IAccountApiClient, AccountApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The owner-facing setup surface, used by the shared landing page for the same readiness answer the posting
// path gives (specification task 13).
builder.Services.AddHttpClient<QtsBilling.Contracts.Administration.IBusinessSetupApiClient, QtsBilling.Contracts.Administration.BusinessSetupApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The administration screens (specification task 14): accounts and handover codes, terminals and their custody,
// and the control-account mapping the posting rules read. Each client reaches the route that is already the
// authority for that fact, so the screen cannot show a different answer from the ledger.
builder.Services.AddHttpClient<QtsBilling.Contracts.Administration.IAdministrationUserApiClient, QtsBilling.Contracts.Administration.AdministrationUserApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<QtsBilling.Contracts.Administration.IDeviceAdministrationApiClient, QtsBilling.Contracts.Administration.DeviceAdministrationApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<QtsBilling.Contracts.Accounting.IAccountingApiClient, QtsBilling.Contracts.Accounting.AccountingApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The supplier half of the party workbench (specification task 15) over the routes purchasing already publishes.
builder.Services.AddHttpClient<QtsBilling.Contracts.Purchasing.ISupplierApiClient, QtsBilling.Contracts.Purchasing.SupplierApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The catalog reference lists and the pricing definitions (specification task 16): the screens maintain records the
// server already validates, so the same routes are reached rather than a second copy of the rules.
builder.Services.AddHttpClient<QtsBilling.Contracts.Catalog.IMasterDataApiClient, QtsBilling.Contracts.Catalog.MasterDataApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<QtsBilling.Contracts.Pricing.IPricingApiClient, QtsBilling.Contracts.Pricing.PricingApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The product and variant workbench (specification task 18) reads and writes through the catalog contract the
// browser and the terminal already use; the server host needs the same registration so the shared page resolves.
builder.Services.AddHttpClient<QtsBilling.Contracts.Catalog.IProductApiClient, QtsBilling.Contracts.Catalog.ProductApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

// The register and cash-custody workbench (specification task 19): the drawer commands are the same versioned,
// idempotency-keyed contract the terminal uses, so the shared page resolves on this host too.
builder.Services.AddHttpClient<QtsBilling.Contracts.Sales.ICashRegisterApiClient, QtsBilling.Contracts.Sales.CashRegisterApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IQuoteApiClient, QuoteApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<ISalesOrderApiClient, SalesOrderApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IFulfillmentApiClient, FulfillmentApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IInvoiceApiClient, InvoiceApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IInventoryApiClient, InventoryApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IPaymentApiClient, PaymentApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddSingleton(serviceProvider =>
{
    var environment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();

    return OfflineGrantKeyRing.Shared(configuration, environment);
});

var app = builder.Build();

// The operator bootstrap runs as a command: it initialises an empty migrated instance and exits before any listener
// starts, so no public route can ever create the first owner.
if (LocalBootstrap.IsBootstrapCommand(args))
{
    Environment.ExitCode = await LocalBootstrap.RunCommandAsync(app.Services, args);
    return;
}

// Fail closed before anything is served: a production instance with missing keys, an unowned origin or an
// ephemeral key ring must refuse to start rather than run a financial system on a convenience (task 09).
LocalSecurityPolicy.Enforce(app.Configuration, app.Environment, app.Logger);

// Forwarded values are believed only after the peer address has matched the reviewed proxy list, so this runs
// before anything reads the client address. With no configured proxy the headers stay untrusted and the host
// filter (if an owned origin is configured) answers only the names the operator declared (task 09, step 2).
if (LocalSecurityPolicy.HasTrustedProxies(app.Configuration))
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();

    // The dev seeder applies the schema first. Ensure the native client only after OpenIddict's
    // tables exist on a brand-new local database.
    await DevDataSeeder.SeedAsync(app.Services, app.Configuration, app.Logger, isDevelopment: true);
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// The approved native public client is registered once and only when missing; the operation is
// idempotent, so a restart never rotates credentials or redirect addresses. Production schema
// application remains a deployment responsibility.
await NativeAuthorizationEndpoints.EnsureNativeClientAsync(app.Services);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseMiddleware<AuthRateLimitKeyMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<BusinessScopeHeaderMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

// A cookie session may only be mutated with an explicit proof, and only for cookie principals: a native
// bearer request never inherits one (specification D04, task 09).
app.UseMiddleware<CookieAntiforgeryMiddleware>();

// Metrics sit behind the management boundary and after authentication, so an unauthenticated scrape of
// operational data is not a property of the self-hosted profile.
app.UseMiddleware<MetricsAccessMiddleware>();
app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapStaticAssets();

app.MapAuthEndpoints();
app.MapNativeAuthorizationEndpoints();
app.MapBusinessAdministrationEndpoints();
app.MapUserAdministrationEndpoints();
app.MapDeviceEndpoints();
app.MapCustomerEndpoints();
app.MapProductEndpoints();
app.MapMasterDataEndpoints();
app.MapAccountingEndpoints();
app.MapInventoryEndpoints();
app.MapBankingEndpoints();
app.MapSaleEndpoints();
app.MapPaymentTerminalEndpoints();
app.MapCashRegisterEndpoints();
app.MapRefundEndpoints();
app.MapOrdersEndpoints();
app.MapInvoiceEndpoints();
app.MapPaymentEndpoints();
app.MapPurchasingEndpoints();
app.MapSupplierBillEndpoints();
app.MapExpenseEndpoints();
app.MapPricingEndpoints();
app.MapReportEndpoints();
app.MapAuditEndpoints();
app.MapSyncOperationEndpoints();
app.MapSyncChangeEndpoints();
app.MapHealthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(QtsWebApp.Client._Imports).Assembly, typeof(QtsBilling.Ui.Pages.Customers).Assembly);

app.Run();

/// <summary>
/// The entry point is visible to the integration test host, which points the same code at its own
/// disposable database (specification task 01). It adds no production behaviour and no bypass.
/// </summary>
public partial class Program
{
}
