using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using QtsBilling.Contracts.Clients;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Components;
using QtsWebApp.Features.Auth;
using QtsWebApp.Features.Accounting;
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

    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

// API plumbing (QB-05) and the typed API clients used by server prerender and the browser.
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => QtsBilling.Contracts.Serialization.ContractJson.Apply(options.SerializerOptions));
builder.Services.AddOpenApi();
builder.Services.AddTransient<LocalApiHandler>();

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
app.MapHealthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(QtsWebApp.Client._Imports).Assembly);

app.Run();
