using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using QtsBilling.Contracts.Abstractions;
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
builder.Services.AddQtsPostgresBilling(PostgresServiceCollectionExtensions.ResolveConnectionString());

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

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization(QtsPolicies.Configure);
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

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
app.MapHealthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(QtsWebApp.Client._Imports).Assembly, typeof(QtsBilling.Ui.Pages.Customers).Assembly);

app.Run();
