// QB-16 platform-injection smoke.
//
// Renders the shared customer component outside the web host with stubbed feature interfaces: no HTTP,
// no database, no Blazor Server circuit. This is the same contract a MAUI BlazorWebView host satisfies
// by registering its own ICustomerApiClient/IAccountApiClient implementations, so the component is proven
// host-agnostic before the MAUI workload is installed.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Pages;

var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | {name}{(detail is null ? string.Empty : $" | {detail}")}");

    if (!ok)
    {
        failures++;
    }
}

Console.WriteLine("== QB-16 platform-injection smoke: shared MudBlazor component outside the web host ==");

// 1. The RCL must not reference any data-access assembly.
var sharedAssembly = typeof(Customers).Assembly;
var referenced = sharedAssembly.GetReferencedAssemblies()
    .Select(reference => reference.Name ?? string.Empty)
    .ToArray();
string[] forbidden =
[
    "QtsBilling.Persistence",
    "Microsoft.EntityFrameworkCore",
    "Npgsql",
    "MongoDB",
    "QtsServices",
    "QtsWebApp",
];

var leaks = referenced
    .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
    .ToArray();

Check("shared assembly has no data-access/host references", leaks.Length == 0, leaks.Length == 0 ? sharedAssembly.GetName().Name : string.Join(", ", leaks));
Console.WriteLine($"     references: {string.Join(", ", referenced.Where(name => !name.StartsWith("System") && !name.StartsWith("Microsoft") && !name.StartsWith("netstandard")).OrderBy(name => name))}");

// 2. The routable page lives in the shared assembly (hosts only add it as an additional assembly).
var route = typeof(Customers).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
    .Cast<RouteAttribute>()
    .Select(attribute => attribute.Template)
    .FirstOrDefault();

Check("customer page carries its own route", route == "/customers", route ?? "missing");

// 3. Render the component with stubbed feature interfaces (the platform-injection point).
var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IJSRuntime>(new StubJsRuntime());
services.AddMudServices();
services.AddSingleton<ICustomerApiClient, StubCustomerApiClient>();
services.AddSingleton<IAccountApiClient, StubAccountApiClient>();
services.AddSingleton<NavigationManager, StubNavigationManager>();

await using var provider = services.BuildServiceProvider();
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

await using var renderer = new HtmlRenderer(provider, loggerFactory);
var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<Customers>(ParameterView.Empty);
    return output.ToHtmlString();
});

Check("component renders a Customers heading", html.Contains("Customers", StringComparison.Ordinal));
Check("component renders the search field", html.Contains("Search customers", StringComparison.Ordinal));
Check("component renders the add action", html.Contains("Add Customer", StringComparison.Ordinal));
Check("component renders the refresh action", html.Contains("Refresh", StringComparison.Ordinal));
File.WriteAllText(Path.Combine(Path.GetTempPath(), "qb16-render.html"), html);
Check("component renders an empty state", html.Contains("No customers yet", StringComparison.Ordinal));
Check("component renders table headers", html.Contains(">Name<", StringComparison.Ordinal) && html.Contains(">Email<", StringComparison.Ordinal));
Check("no database or connection string leaks into the markup", !html.Contains("Password" + "=", StringComparison.OrdinalIgnoreCase) && !html.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));

// 4. The browser uses the same shared POS page through the online checkout contract.
services.AddSingleton<IPosCheckoutOrchestrator, StubPosCheckoutOrchestrator>();
await using var posProvider = services.BuildServiceProvider();
await using var posRenderer = new HtmlRenderer(posProvider, posProvider.GetRequiredService<ILoggerFactory>());
var posHtml = await posRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await posRenderer.RenderComponentAsync<Pos>(ParameterView.Empty);
    return output.ToHtmlString();
});
Check("browser POS renders online checkout", posHtml.Contains("ONLINE CASH SALE", StringComparison.Ordinal));
Check("browser POS asks for location and register", posHtml.Contains("Sale context", StringComparison.Ordinal) && posHtml.Contains("Cash register", StringComparison.Ordinal));
Check("browser POS exposes a direct server post action", posHtml.Contains("Post cash sale", StringComparison.Ordinal));

Console.WriteLine($"     rendered markup: {html.Length} chars");

Console.WriteLine(failures == 0
    ? "== smoke PASSED: the same customer component builds and renders with injected feature interfaces =="
    : $"== smoke FAILED: {failures} check(s) failed ==");

return failures == 0 ? 0 : 1;

internal sealed class StubCustomerApiClient : ICustomerApiClient
{
    public Task<PagedResult<CustomerDto>> GetCustomersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<CustomerDto>.Empty(pageSize));

    public Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Sample(request.Name));

    public Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        SaveCustomerRequest request,
        long version,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Sample(request.Name, id));

    public Task DeactivateCustomerAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static CustomerDto Sample(string name, Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), name, null, null, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

internal sealed class StubPosCheckoutOrchestrator : IPosCheckoutOrchestrator
{
    public Task<PosCheckoutContext> GetContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PosCheckoutContext([], []));

    public Task<CashSessionDetailDto> OpenCashDrawerAsync(Guid registerId, decimal openingFloat, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PosCatalogPage> GetCatalogAsync(Guid locationId, string? search = null, string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new PosCatalogPage([], null));

    public Task<SaleCaptureResult> CaptureCashSaleAsync(Guid locationId, Guid registerId, LocalCashSaleDraft draft, CancellationToken cancellationToken = default)
        => Task.FromResult(new SaleCaptureResult(Guid.NewGuid(), "S-SMOKE", "USD", 0m, 0m, DateTimeOffset.UtcNow, false));
}

internal sealed class StubAccountApiClient : IAccountApiClient
{
    public Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke does not sign in.");

    public Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SessionDto?>(new SessionDto(
            Guid.CreateVersion7(),
            "owner@example.test",
            "Smoke Owner",
            Guid.CreateVersion7(),
            "Smoke Business",
            ["Owner"],
            [.. Permissions.All],
            PasswordResetRequired: false));

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PasswordResetTicketDto?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
        => Task.FromResult<PasswordResetTicketDto?>(null);

    public Task CompletePasswordResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class StubNavigationManager : NavigationManager
{
    public StubNavigationManager() => Initialize("http://localhost/", "http://localhost/");

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        // Static render: navigation is a no-op.
    }
}

internal sealed class StubJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => ValueTask.FromResult(default(TValue)!);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => ValueTask.FromResult(default(TValue)!);
}
