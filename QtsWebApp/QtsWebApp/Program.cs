using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using QtsBilling.Contracts.Abstractions;
using System.Security.Cryptography.X509Certificates;
using QtsBilling.Contracts.Clients;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Components;
using QtsWebApp.Features.Auth;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Audit;
using QtsWebApp.Features.Catalog;
using QtsWebApp.Features.Customers;
using QtsWebApp.Features.Health;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Features.Invoices;
using QtsWebApp.Features.Expenses;
using QtsWebApp.Features.Pricing;
using QtsWebApp.Features.Purchasing;
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

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "qts.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;

    // CSRF defence: SameSite=Strict plus no cross-origin CORS means a hostile site can never ride the
    // session cookie, even for state-changing verbs (QB-04 checks).
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
builder.Services.ConfigureHttpJsonOptions(options => QtsBilling.Contracts.Serialization.ContractJson.Apply(options.SerializerOptions));
builder.Services.AddOpenApi();
builder.Services.AddTransient<LocalApiHandler>();

// The browser host never captures sales offline (QB-19): the shared point-of-sale component asks this
// service, so it shows an honest "desktop only" state instead of a fake capture.
builder.Services.AddScoped<QtsBilling.Contracts.Pos.ILocalPosService, QtsWebApp.Features.Pos.WebLocalPosService>();

// Key-ring persistence (QB-14): set QTS_DATAPROTECTION_PATH in production so session-cookie and ticket
// keys survive restarts and rotation is a deliberate, reviewed operation. Unset (development) keeps an
// ephemeral ring; no key material is ever committed or written next to the database.
var dataProtectionPath = builder.Configuration["QTS_DATAPROTECTION_PATH"];

if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("QtsBilling");
}

builder.Services.AddHttpClient<ICustomerApiClient, CustomerApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

builder.Services.AddHttpClient<IAccountApiClient, AccountApiClient>(client => client.BaseAddress = new Uri("http://qts.internal"))
    .AddHttpMessageHandler<LocalApiHandler>();

var app = builder.Build();

// The approved native public client is registered once and only when missing; the operation is
// idempotent, so a restart never rotates credentials or redirect addresses.
await NativeAuthorizationEndpoints.EnsureNativeClientAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();

    await DevDataSeeder.SeedAsync(app.Services, app.Configuration, app.Logger, isDevelopment: true);
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapAuthEndpoints();
app.MapNativeAuthorizationEndpoints();
app.MapCustomerEndpoints();
app.MapProductEndpoints();
app.MapAccountingEndpoints();
app.MapInventoryEndpoints();
app.MapSaleEndpoints();
app.MapCashRegisterEndpoints();
app.MapRefundEndpoints();
app.MapInvoiceEndpoints();
app.MapPurchasingEndpoints();
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
