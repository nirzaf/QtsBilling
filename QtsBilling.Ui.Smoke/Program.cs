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
using MudBlazor;
using MudBlazor.Services;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages;
using QtsBilling.Ui.Pages.Administration;
using QtsBilling.Ui.Pages.Catalog;
using QtsBilling.Ui.Pages.Invoices;
using QtsBilling.Ui.Smoke;

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

// 1. The RCL must reference only the approved positive dependency graph.
var sharedAssembly = typeof(Customers).Assembly;
var referenced = sharedAssembly.GetReferencedAssemblies()
    .Select(reference => reference.Name ?? string.Empty)
    .ToArray();

// A controlled fault injection proves the allow-list rejects persistence references. It can only add a
// disallowed name, never suppress the real assembly references.
var injectedReference = Environment.GetEnvironmentVariable("QTS_SMOKE_INJECT_REFERENCE");
if (!string.IsNullOrWhiteSpace(injectedReference))
{
    referenced = [.. referenced, injectedReference];
}

var leaks = SharedUiDependencyPolicy.Disallowed(referenced);

Check(
    "shared assembly references only the approved Contracts/UI graph",
    leaks.Length == 0,
    leaks.Length == 0 ? sharedAssembly.GetName().Name : string.Join(", ", leaks));
Console.WriteLine($"     references: {string.Join(", ", referenced.Where(name => !name.StartsWith("System", StringComparison.Ordinal) && !name.StartsWith("Microsoft", StringComparison.Ordinal) && !name.StartsWith("netstandard", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))}");

// 2. The routable page lives in the shared assembly (hosts only add it as an additional assembly).
var route = typeof(Customers).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
    .Cast<RouteAttribute>()
    .Select(attribute => attribute.Template)
    .FirstOrDefault();

Check("customer page carries its own route", route == "/customers", route ?? "missing");

// 3. Render the component with stubbed feature interfaces (the platform-injection point).
var stubCustomers = new StubCustomerApiClient();
var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IJSRuntime>(new StubJsRuntime());
services.AddMudServices();
services.AddSingleton<ICustomerApiClient>(stubCustomers);
services.AddSingleton<IAccountApiClient>(new StubAccountApiClient());
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

// The task 15 workbench rules, proven where they are visible: a prerender asks the server for nothing, states the
// loading answer instead of inventing a table, keeps the out-of-service view reachable, and says deactivation is
// not deletion.
Check("the customer workbench reads nothing during the prerender", stubCustomers.ListCalls == 0, $"reads: {stubCustomers.ListCalls}");
Check("the customer workbench states its loading answer rather than an empty file", html.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the customer workbench offers the out-of-service view for history", html.Contains("Include out of service", StringComparison.Ordinal));
Check("the customer workbench says a deactivated party keeps its history",
    html.Contains("keeps every invoice and sale that names them", StringComparison.Ordinal));
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

// 5. The invoice editor must render every permission and lifecycle branch through injected clients alone
//    (spec T02.01/T02.04): issue reachable on a draft, readers not handed an editable form, issued drafts
//    frozen, and a pre-recognition draft honestly refused.
var draftId = Guid.CreateVersion7();
var issuedId = Guid.CreateVersion7();
var unknownId = Guid.CreateVersion7();
var smokeLocationId = Guid.CreateVersion7();
var smokeCustomerId = Guid.CreateVersion7();

async Task<string> RenderInvoiceEditor(Guid? invoiceId, string[] permissions)
{
    var editorServices = new ServiceCollection();
    editorServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    editorServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
    editorServices.AddMudServices();
    editorServices.AddSingleton<ICustomerApiClient, StubCustomerApiClient>();
    editorServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient(permissions));
    editorServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(smokeLocationId));
    editorServices.AddSingleton<IInvoiceApiClient>(new StubInvoiceApiClient(
        draft: InvoiceSample(draftId, InvoiceState.Draft, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId),
        issued: InvoiceSample(issuedId, InvoiceState.Issued, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId),
        unknown: InvoiceSample(unknownId, InvoiceState.Draft, InvoiceRecognitionMode.HistoricalUnknown, smokeLocationId, smokeCustomerId)));
    editorServices.AddSingleton<NavigationManager, StubNavigationManager>();

    await using var editorProvider = editorServices.BuildServiceProvider();
    await using var editorRenderer = new HtmlRenderer(editorProvider, editorProvider.GetRequiredService<ILoggerFactory>());

    return await editorRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var parameters = invoiceId is null ? ParameterView.Empty : ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(InvoiceEditor.InvoiceId)] = invoiceId,
        });
        var output = await editorRenderer.RenderComponentAsync<InvoiceEditor>(parameters);
        return output.ToHtmlString();
    });
}

var newDraftHtml = await RenderInvoiceEditor(null, [Permissions.InvoicesManage, Permissions.InvoicesIssue]);
Check("editor renders the create form for a manager", newDraftHtml.Contains("New invoice", StringComparison.Ordinal) && newDraftHtml.Contains("Save draft", StringComparison.Ordinal));
Check("editor labels the estimate as local, not server-final", newDraftHtml.Contains("Estimated total", StringComparison.Ordinal));

var draftHtml = await RenderInvoiceEditor(draftId, [Permissions.InvoicesManage, Permissions.InvoicesIssue]);
Check("editor offers issue on a draft the operator may issue", draftHtml.Contains("Issue invoice", StringComparison.Ordinal));
Check(
    "editor converts the stored tax fraction once into percent units",
    draftHtml.Contains("Tax %", StringComparison.Ordinal)
        && draftHtml.Contains("aria-valuenow=\"15\"", StringComparison.Ordinal)
        && !draftHtml.Contains("aria-valuenow=\"0.15\"", StringComparison.Ordinal));
File.WriteAllText(Path.Combine(Path.GetTempPath(), "qb-invoice-editor-draft.html"), draftHtml);

var readerHtml = await RenderInvoiceEditor(draftId, [Permissions.InvoicesIssue]);
Check("reader sees the draft without an editable form", readerHtml.Contains("your role cannot edit it", StringComparison.Ordinal) && !readerHtml.Contains("Save draft", StringComparison.Ordinal));

var issuedHtml = await RenderInvoiceEditor(issuedId, [Permissions.InvoicesManage, Permissions.InvoicesIssue]);
Check("issued invoice is read-only", issuedHtml.Contains("Draft fields are locked after issue", StringComparison.Ordinal) && !issuedHtml.Contains("Save draft", StringComparison.Ordinal));

var unknownHtml = await RenderInvoiceEditor(unknownId, [Permissions.InvoicesManage, Permissions.InvoicesIssue]);
Check("pre-recognition draft is refused issue explicitly", unknownHtml.Contains("predates recognition modes", StringComparison.Ordinal));

var deniedHtml = await RenderInvoiceEditor(draftId, [Permissions.CustomersRead]);
Check("an unrelated role cannot read invoices", deniedHtml.Contains("cannot view invoices", StringComparison.Ordinal) && !deniedHtml.Contains("Save draft", StringComparison.Ordinal));

// 6. The invoice list loads only after the first interactive render (a static prerender has no session
//    cookie, the same rule the customer page follows), so its rows and filters are proven in the browser
//    fixture at Task 45 rather than faked here.

// 7. The shell itself (specification task 13, T13.01/T13.03): the shared navigation renders the same catalogue for
//    every host, filtered by what the session may actually do, and a screen that is not built yet is announced with
//    its reason instead of being offered as a link that leads nowhere.
async Task<string> RenderShell(string[]? permissions, bool deviceHost)
{
    var shellServices = new ServiceCollection();
    shellServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    shellServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
    shellServices.AddMudServices();

    var accounts = new StubAccountApiClient(permissions);
    shellServices.AddSingleton<IAccountApiClient>(accounts);
    shellServices.AddSingleton<INavContributor>(deviceHost
        ? new StubContributor([new NavItem("Point of sale", "pos", Icons.Material.Filled.PointOfSale)])
        : new StubContributor([]));
    shellServices.AddSingleton<NavigationManager, StubNavigationManager>();

    var session = await accounts.GetSessionAsync();

    await using var shellProvider = shellServices.BuildServiceProvider();
    await using var shellRenderer = new HtmlRenderer(shellProvider, shellProvider.GetRequiredService<ILoggerFactory>());

    return await shellRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var output = await shellRenderer.RenderComponentAsync<NavMenu>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session }));
        return output.ToHtmlString();
    });
}

var ownerHtml = await RenderShell([.. Permissions.All], deviceHost: false);
Check("shell groups the modules an owner can reach", ownerHtml.Contains("Administration", StringComparison.Ordinal)
    && ownerHtml.Contains("Users and handover tickets", StringComparison.Ordinal)
    && ownerHtml.Contains("Purchasing", StringComparison.Ordinal));
Check("browser point of sale says why it is not a browser screen", ownerHtml.Contains("does not hold a drawer", StringComparison.Ordinal));
Check("a planned screen names the task that ships it", ownerHtml.Contains("Arrives with task", StringComparison.Ordinal));

var deviceHtml = await RenderShell([.. Permissions.All], deviceHost: true);
Check("the terminal's own point of sale is a real link", deviceHtml.Contains("href=\"pos\"", StringComparison.Ordinal)
    && !deviceHtml.Contains("does not hold a drawer", StringComparison.Ordinal));

var cashierHtml = await RenderShell([Permissions.SalesPost, Permissions.CustomersRead, Permissions.InvoicesIssue], deviceHost: false);
Check("a cashier sees trading and not administration", cashierHtml.Contains("Customers", StringComparison.Ordinal)
    && !cashierHtml.Contains("Users and handover tickets", StringComparison.Ordinal)
    && !cashierHtml.Contains("Business and branches", StringComparison.Ordinal));

var viewerHtml = await RenderShell([Permissions.CustomersRead], deviceHost: false);
Check("a viewer is not offered invoicing or the catalogue", !viewerHtml.Contains("Invoices", StringComparison.Ordinal)
    && !viewerHtml.Contains("Products and variants", StringComparison.Ordinal)
    && viewerHtml.Contains("Customers", StringComparison.Ordinal));

// No navigation entry may point at a page that does not exist: usable rows carry a real address, planned rows
// carry none. This is the dead-link rule the task's definition of done depends on.
var addresses = System.Text.RegularExpressions.Regex.Matches(ownerHtml, "href=\"([^\"]*)\"")
    .Select(match => match.Groups[1].Value)
    .Where(value => value.Length > 0 && value != "#" && !value.StartsWith("_framework", StringComparison.OrdinalIgnoreCase))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
var known = addresses.Where(address => address is "/" or "customers" or "invoices" or "login" || ShellNavigation.Knows(address.TrimStart('/'))).ToArray();
Check("every navigation address is a shipped page or a known route", known.Length == addresses.Length,
    string.Join(", ", addresses.Except(known, StringComparer.OrdinalIgnoreCase)));

// 8. The shared state panels (T13.03): each state says something different, an ended session offers the way back,
//    an unconfirmed outcome never claims success, and an offline terminal does not look like an empty business.
async Task<string> RenderState(QtsBilling.Ui.Layout.ShellStateKind kind, bool withRetry)
{
    var stateServices = new ServiceCollection();
    stateServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    stateServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
    stateServices.AddMudServices();
    stateServices.AddSingleton<NavigationManager, StubNavigationManager>();

    await using var stateProvider = stateServices.BuildServiceProvider();
    await using var stateRenderer = new HtmlRenderer(stateProvider, stateProvider.GetRequiredService<ILoggerFactory>());

    return await stateRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var parameters = new Dictionary<string, object?> { ["Kind"] = kind };
        if (withRetry)
        {
            parameters["RetryCallback"] = new Func<Task>(() => Task.CompletedTask);
        }

        var output = await stateRenderer.RenderComponentAsync<QtsBilling.Ui.Components.ShellStates>(
            ParameterView.FromDictionary(parameters));
        return output.ToHtmlString();
    });
}

var stateHeadings = new HashSet<string>(StringComparer.Ordinal);

foreach (var kind in new[]
{
    QtsBilling.Ui.Layout.ShellStateKind.Loading,
    QtsBilling.Ui.Layout.ShellStateKind.Empty,
    QtsBilling.Ui.Layout.ShellStateKind.Unauthenticated,
    QtsBilling.Ui.Layout.ShellStateKind.Forbidden,
    QtsBilling.Ui.Layout.ShellStateKind.Conflict,
    QtsBilling.Ui.Layout.ShellStateKind.Offline,
    QtsBilling.Ui.Layout.ShellStateKind.UnknownOutcome,
    QtsBilling.Ui.Layout.ShellStateKind.Failure,
})
{
    var stateHtml = await RenderState(kind, withRetry: kind != QtsBilling.Ui.Layout.ShellStateKind.Unauthenticated);
    var heading = System.Text.RegularExpressions.Regex.Match(stateHtml, "<p class=\"mud-typography mud-typography-subtitle1[^>]*>([^<]+)<");
    var text = heading.Success ? heading.Groups[1].Value.Trim() : string.Empty;

    Check($"state {kind} renders its own sentence", text.Length > 0 && stateHeadings.Add(text), text);
    Check($"state {kind} never claims a success", !stateHtml.Contains("completed successfully", StringComparison.OrdinalIgnoreCase));
}

var expiredHtml = await RenderState(QtsBilling.Ui.Layout.ShellStateKind.Unauthenticated, withRetry: false);
Check("an ended session offers the way back", expiredHtml.Contains("Sign in again", StringComparison.Ordinal)
    && expiredHtml.Contains("href=\"login\"", StringComparison.Ordinal));

var conflictHtml = await RenderState(QtsBilling.Ui.Layout.ShellStateKind.Conflict, withRetry: true);
Check("a lost race offers a reload, not a blind repeat", conflictHtml.Contains("Try again", StringComparison.Ordinal)
    && conflictHtml.Contains("Nothing was saved over the other edit", StringComparison.Ordinal));

var unknownHtml2 = await RenderState(QtsBilling.Ui.Layout.ShellStateKind.UnknownOutcome, withRetry: true);
Check("an unconfirmed outcome tells the operator to check first", unknownHtml2.Contains("never captured twice", StringComparison.Ordinal));

// 9. The administration screens (specification task 14). A shared page may only reach its host's typed clients, and a
//    prerender must not ask the server for anything it cannot answer yet - the stubs count their own calls, so a
//    screen that reads during initialisation fails here rather than in front of an operator on a real host.
var adminServices = new ServiceCollection();
adminServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
adminServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
adminServices.AddMudServices();
adminServices.AddSingleton<NavigationManager, StubNavigationManager>();
adminServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([.. Permissions.All]));

var stubSetup = new StubBusinessSetupApiClient();
var stubUsers = new StubAdministrationUserApiClient();
var stubDevices = new StubDeviceAdministrationApiClient();
var stubAccounts = new StubAccountingApiClient();
adminServices.AddSingleton<IBusinessSetupApiClient>(stubSetup);
adminServices.AddSingleton<IAdministrationUserApiClient>(stubUsers);
adminServices.AddSingleton<IDeviceAdministrationApiClient>(stubDevices);
adminServices.AddSingleton<IAccountingApiClient>(stubAccounts);

await using var adminProvider = adminServices.BuildServiceProvider();
await using var adminRenderer = new HtmlRenderer(adminProvider, adminProvider.GetRequiredService<ILoggerFactory>());

async Task<string> RenderAdmin<TComponent>()
    where TComponent : IComponent
    => await adminRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var output = await adminRenderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
        return output.ToHtmlString();
    });

async Task<string> RenderWith<TComponent>(Dictionary<string, object?> parameters)
    where TComponent : IComponent
    => await adminRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var output = await adminRenderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
        return output.ToHtmlString();
    });

var businessHtml = await RenderAdmin<QtsBilling.Ui.Pages.Administration.BusinessSettings>();
var usersHtml = await RenderAdmin<QtsBilling.Ui.Pages.Administration.Users>();
var devicesHtml = await RenderAdmin<QtsBilling.Ui.Pages.Administration.Devices>();

Check("no administration screen asks the server for data during the prerender",
    stubSetup.Calls == 0 && stubUsers.Calls == 0 && stubDevices.Calls == 0 && stubAccounts.Calls == 0,
    $"setup {stubSetup.Calls}, users {stubUsers.Calls}, terminals {stubDevices.Calls}, accounts {stubAccounts.Calls}");

Check("each administration screen shows a stated state rather than a blank page",
    businessHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && usersHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && devicesHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));

Check("the business screen states the structural rule before any field is touched",
    businessHtml.Contains("The base currency and the settlement scale belong to the", StringComparison.Ordinal));
Check("the users screen states that a handover code is shown once",
    usersHtml.Contains("cannot be shown again", StringComparison.Ordinal));
Check("the terminal screen states that there is no wipe control",
    devicesHtml.Contains("no wipe button", StringComparison.OrdinalIgnoreCase));
Check("the terminal screen states the custody precondition",
    devicesHtml.Contains("awaiting review", StringComparison.OrdinalIgnoreCase));

// The custody notice as the terminal screen renders it: the money, and both blocked commands, in one place.
var custodyHtml = await RenderWith<QtsBilling.Ui.Components.DeviceCustodyNotice>(new Dictionary<string, object?>
{
    ["Device"] = stubDevices.Holding,
    ["ExplainCommands"] = true,
});

Check("a terminal holding cash is announced as unresolved, with the amount", custodyHtml.Contains("data-qts-custody=\"unresolved\"", StringComparison.Ordinal)
    && custodyHtml.Contains("240.75", StringComparison.Ordinal));
Check("the custody notice explains both blocked commands", custodyHtml.Contains("clearance", StringComparison.OrdinalIgnoreCase)
    && custodyHtml.Contains("Retirement waits for reconciliation", StringComparison.Ordinal));

var settledHtml = await RenderWith<QtsBilling.Ui.Components.DeviceCustodyNotice>(new Dictionary<string, object?>
{
    ["Device"] = stubDevices.Settled,
});
Check("a settled terminal says so instead of being silent", settledHtml.Contains("data-qts-custody=\"settled\"", StringComparison.Ordinal)
    && settledHtml.Contains("Nothing outstanding", StringComparison.Ordinal));

// The handover panel: the secret once, with nothing that could keep it.
var handoverCode = "smoke-handover-code";
var ticketHtml = await RenderWith<QtsBilling.Ui.Components.SetupTicketPanel>(new Dictionary<string, object?>
{
    ["Ticket"] = new QtsBilling.Contracts.Administration.SetupTicketDto(Guid.NewGuid(), handoverCode, "Setup", DateTimeOffset.UtcNow.AddMinutes(30)),
    ["AccountName"] = "Night Cashier",
});
Check("the handover panel shows the code and names the account it belongs to",
    ticketHtml.Contains(handoverCode, StringComparison.Ordinal) && ticketHtml.Contains("Night Cashier", StringComparison.Ordinal));
Check("the handover panel offers no way to keep the code",
    !ticketHtml.Contains("clipboard", StringComparison.OrdinalIgnoreCase) && !ticketHtml.Contains("Copy", StringComparison.Ordinal));

// The reference selector: a name in, a name out, and no identifier for a human to read.
var branchId = Guid.NewGuid();
var selectorHtml = await RenderWith<QtsBilling.Ui.Components.ReferenceSelector>(new Dictionary<string, object?>
{
    ["Label"] = "Branch the terminal will serve",
    ["Search"] = new Func<string, CancellationToken, Task<IReadOnlyList<QtsBilling.Ui.Models.ReferenceItem>>>(
        (term, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<QtsBilling.Ui.Models.ReferenceItem>>(
                [QtsBilling.Ui.Models.ReferenceItem.From(branchId, "Main counter", "MAIN")]);
        }),
});
Check("the reference selector asks for a name, never an identifier",
    selectorHtml.Contains("Branch the terminal will serve", StringComparison.Ordinal) && !selectorHtml.Contains(branchId.ToString("D"), StringComparison.Ordinal));

// The screens' own decisions, where they live (task 14 recipe steps 2-3).
var accounts = (await stubUsers.ListUsersAsync()).Items;
var soleOwner = accounts.Single(row => row.Role == "Owner");
Check("the last owner cannot be taken out of service, and says why",
    AdministrationRules.LastOwnerBlock(accounts, soleOwner) is { Length: > 0 });
Check("the last owner cannot be demoted either, while keeping ownership stays allowed",
    AdministrationRules.LastOwnerBlock(accounts, soleOwner, "Manager") is { Length: > 0 }
    && AdministrationRules.LastOwnerBlock(accounts, soleOwner, "Owner") is null);
Check("a new cashier is offered no discount allowance", AdministrationRules.DefaultDiscountRate("Cashier") == 0m
    && AdministrationRules.DefaultDiscountRate("Manager") == 1m);
Check("a refusal names the authority that is missing",
    AdministrationRules.MissingReason("Adding a person", Permissions.UsersManage).Contains(Permissions.UsersManage, StringComparison.Ordinal));

// 10. The party workbench (specification task 15): suppliers join customers in the shared library, and the paged
//     list state that both screens now use is proven directly - bounded pages, a superseded load that cannot paint,
//     and four different answers for four different failures.
var stubSuppliers = new StubSupplierApiClient();
var partyServices = new ServiceCollection();
partyServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
partyServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
partyServices.AddMudServices();
partyServices.AddSingleton<NavigationManager, StubNavigationManager>();
partyServices.AddSingleton<ISupplierApiClient>(stubSuppliers);
partyServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([Permissions.PurchasingManage]));

await using var partyProvider = partyServices.BuildServiceProvider();
await using var partyRenderer = new HtmlRenderer(partyProvider, partyProvider.GetRequiredService<ILoggerFactory>());

var suppliersHtml = await partyRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await partyRenderer.RenderComponentAsync<Suppliers>(ParameterView.Empty);
    return output.ToHtmlString();
});

Check("the supplier workbench is a shared page with its own route",
    typeof(Suppliers).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Any(a => a.Template.Trim('/') == "suppliers"),
    typeof(Suppliers).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(a => a.Template).FirstOrDefault() ?? "missing");
Check("the supplier workbench renders its heading and actions",
    suppliersHtml.Contains("Suppliers", StringComparison.Ordinal)
        && suppliersHtml.Contains("Search suppliers", StringComparison.Ordinal)
        && suppliersHtml.Contains("Add supplier", StringComparison.Ordinal));
Check("the supplier workbench reads nothing during the prerender", stubSuppliers.Reads.Count == 0, $"reads: {stubSuppliers.Reads.Count}");
Check("the supplier workbench says a retired supplier keeps its history",
    suppliersHtml.Contains("keeps every purchase order, receipt and bill that names it", StringComparison.Ordinal));
Check("the supplier workbench does not offer fields the contract cannot carry",
    suppliersHtml.Contains("does not carry those fields yet", StringComparison.Ordinal));

// The paged state itself, tested where it lives rather than through a browser.
var asked = new List<(string? Search, string? Cursor)>();
var repaints = 0;
var paged = new PagedListState<string>(
    async (search, cursor, pageSize, token) =>
    {
        await Task.Yield();
        asked.Add((search, cursor));
        token.ThrowIfCancellationRequested();

        return new PagedRows<string>(
            cursor is null ? ["first page row"] : ["second page row"],
            cursor is null ? "cursor-two" : null);
    },
    () => repaints++);

await paged.ReloadAsync();
Check("a first page renders rows, not a spinner", paged.State == QtsBilling.Ui.Layout.ShellStateKind.Content && paged.Rows.Count == 1,
    $"{paged.State}, rows {paged.Rows.Count}");

await paged.NextPageAsync();
Check("the next page is asked for with the server's cursor", paged.CanGoPrevious
    && asked.Any(entry => entry.Cursor == "cursor-two")
    && paged.Rows[0] == "second page row", string.Join("|", asked.Select(entry => entry.Cursor ?? "first")));

await paged.SearchAsync("acme");
Check("a new search term restarts at the first page", asked[^1] == ("acme", null) && paged.CanGoPrevious == false);

var empty = new PagedListState<string>((_, _, _, _) => Task.FromResult(new PagedRows<string>([], null)), () => repaints++);
await empty.SearchAsync("nothing");
Check("no match is its own answer, and says the search ran everywhere", empty.State == QtsBilling.Ui.Layout.ShellStateKind.Empty
    && empty.Message is { Length: > 0 } && empty.Message.Contains("the server", StringComparison.OrdinalIgnoreCase), empty.Message);

var refused = new PagedListState<string>(
    (_, _, _, _) => Task.FromException<PagedRows<string>>(new ApiException(403, "FORBIDDEN", "purchasing.manage is required.")),
    () => repaints++);
await refused.ReloadAsync();
Check("a refusal stays a refusal, not an empty list", refused.State == QtsBilling.Ui.Layout.ShellStateKind.Forbidden
    && refused.Message == "purchasing.manage is required.");

var ended = new PagedListState<string>(
    (_, _, _, _) => Task.FromException<PagedRows<string>>(new ApiException(401, "UNAUTHENTICATED", "Sign in again.")),
    () => repaints++);
await ended.ReloadAsync();
Check("an ended session offers the way back, not a blank dashboard", ended.State == QtsBilling.Ui.Layout.ShellStateKind.Unauthenticated);

var raced = new PagedListState<string>(
    (search, cursor, _, _) => cursor is null
        ? Task.FromResult(new PagedRows<string>(["row one"], "cursor-two"))
        : Task.FromException<PagedRows<string>>(new ApiException(409, "CONCURRENCY_CONFLICT", "the page moved.")),
    () => repaints++);
await raced.ReloadAsync();
await raced.NextPageAsync();
Check("a cursor the server no longer honours falls back to the first page and says so",
    raced.State == QtsBilling.Ui.Layout.ShellStateKind.Conflict && raced.Rows[0] == "row one" && !raced.CanGoPrevious,
    $"{raced.State}: {raced.Message}");

// A load the operator superseded must not paint over the one that replaced it.
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stale = new PagedListState<string>(
    async (search, _, _, token) =>
    {
        await release.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new PagedRows<string>([$"row for {search}"], null);
    },
    () => repaints++);

var firstLoad = stale.SearchAsync("slow");
var secondLoad = stale.SearchAsync("fast");
release.SetResult();
await secondLoad;
await Task.WhenAny(firstLoad, Task.Delay(250));
Check("an older search cannot repaint the list the operator replaced", stale.Rows.Count == 1 && stale.Rows[0] == "row for fast",
    string.Join(",", stale.Rows));

await paged.DisposeAsync();
await stale.DisposeAsync();

// 11. The product workbench (specification task 18): the catalog list and its editor are shipped pages that read
//     nothing during the prerender, hold the arithmetic that explains a price in one shared rule file, and state a
//     keyboard contract a scanner can satisfy without a camera or a network service.
var stubCatalog = new StubProductApiClient();
var stubReference = new StubMasterDataApiClient();
var stubPricing = new StubPricingApiClient();
var catalogServices = new ServiceCollection();
catalogServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
catalogServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
catalogServices.AddMudServices();
catalogServices.AddSingleton<NavigationManager, StubNavigationManager>();
catalogServices.AddSingleton<IProductApiClient>(stubCatalog);
catalogServices.AddSingleton<IMasterDataApiClient>(stubReference);
catalogServices.AddSingleton<IPricingApiClient>(stubPricing);
catalogServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(Guid.NewGuid()));
catalogServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([Permissions.ProductsWrite]));

await using var catalogProvider = catalogServices.BuildServiceProvider();
await using var catalogRenderer = new HtmlRenderer(catalogProvider, catalogProvider.GetRequiredService<ILoggerFactory>());

var productsHtml = await catalogRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await catalogRenderer.RenderComponentAsync<Products>(ParameterView.Empty);
    return output.ToHtmlString();
});

var editorHtml = await catalogRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await catalogRenderer.RenderComponentAsync<ProductEditor>(
        ParameterView.FromDictionary(new Dictionary<string, object?> { ["ProductId"] = Guid.NewGuid() }));
    return output.ToHtmlString();
});

Check("the catalog list and its editor are shared pages with their own routes",
    typeof(Products).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "catalog/products")
    && typeof(ProductEditor).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/').StartsWith("catalog/products/{", StringComparison.Ordinal)),
    string.Join("|", typeof(Products).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(route => route.Template)
        .Concat(typeof(ProductEditor).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(route => route.Template))));
Check("neither catalog page reads anything during the prerender",
    stubCatalog.Calls == 0 && stubReference.Calls == 0 && stubPricing.Calls == 0,
    $"catalog {stubCatalog.Calls}, references {stubReference.Calls}, pricing {stubPricing.Calls}");
Check("both catalog pages show a stated state instead of a blank page",
    productsHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && editorHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the catalog list asks for a name, SKU or barcode and never for an identifier",
    productsHtml.Contains("Search products by name, SKU or variant barcode", StringComparison.Ordinal)
        && !productsHtml.Contains("Product id", StringComparison.Ordinal));
Check("the catalog list states the three rules a save is refused by",
    productsHtml.Contains("cannot be given to two variants", StringComparison.Ordinal)
        && productsHtml.Contains("the database decides", StringComparison.Ordinal)
        && productsHtml.Contains("stays reachable for the documents", StringComparison.Ordinal));
Check("the catalog workbench fetches nothing from a network service in its own markup",
    !productsHtml.Contains("http://", StringComparison.Ordinal) && !productsHtml.Contains("https://", StringComparison.Ordinal)
        && !editorHtml.Contains("http://", StringComparison.Ordinal) && !editorHtml.Contains("https://", StringComparison.Ordinal));

// The rules the editor renders its numbers from, proven where they live.
Check("an exclusive price reads as price plus tax", CatalogRules.Preview(100m, 0.15m, CatalogRules.TaxExclusive) is { Base: 100m, Tax: 15m, Gross: 115m });
Check("an inclusive price reads as the customer's total with tax taken out",
    CatalogRules.Preview(115m, 0.15m, CatalogRules.TaxInclusive) is { Base: 100m, Tax: 15m, Gross: 115m });
Check("a rate that is not configured adds nothing", CatalogRules.Preview(50m, 0m, null).Tax == 0m);
Check("the barcode rule names Enter, a keyboard wedge, and no camera",
    CatalogRules.BarcodeHelp.Contains("Enter", StringComparison.Ordinal)
        && CatalogRules.BarcodeHelp.Contains("keyboard wedge", StringComparison.Ordinal)
        && CatalogRules.BarcodeHelp.Contains("nothing here opens a camera", StringComparison.Ordinal));
Check("the catalog screen sends quantities elsewhere", CatalogRules.CorrectionRoute.Contains("stock counts", StringComparison.Ordinal)
    && CatalogRules.CorrectionRoute.Contains("never from this screen", StringComparison.Ordinal));
Check("retiring a default variant needs a replacement chosen", CatalogRules.NeedsReplacementDefault(true, true)
    && !CatalogRules.NeedsReplacementDefault(false, true) && !CatalogRules.NeedsReplacementDefault(true, false));
Check("an archive discloses only the quantity the screen actually read",
    CatalogRules.ArchiveSentence("Case of 24", 2, "Case", 24m, true).Contains("24.00 unit(s) of Case", StringComparison.Ordinal)
    && !CatalogRules.ArchiveSentence("Consulting", 1, null, 0m, false).Contains("unit(s)", StringComparison.Ordinal));

// 12. The register workbench (specification task 19): the drawer screen is a shipped page that reads nothing
//     during the prerender, states the custody rule before anything is clicked, and keeps the over/short wording
//     in the shared rules the server's own answer is reported with.
var stubDrawers = new StubCashRegisterApiClient();
var drawerServices = new ServiceCollection();
drawerServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
drawerServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
drawerServices.AddMudServices();
drawerServices.AddSingleton<NavigationManager, StubNavigationManager>();
drawerServices.AddSingleton<ICashRegisterApiClient>(stubDrawers);
drawerServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(Guid.NewGuid()));
drawerServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([Permissions.RegistersManage]));

await using var drawerProvider = drawerServices.BuildServiceProvider();
await using var drawerRenderer = new HtmlRenderer(drawerProvider, drawerProvider.GetRequiredService<ILoggerFactory>());

var registersHtml = await drawerRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await drawerRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Sales.Registers>(ParameterView.Empty);
    return output.ToHtmlString();
});

Check("the register workbench is a shared page with its own route",
    typeof(QtsBilling.Ui.Pages.Sales.Registers).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "sales/registers"));
Check("the register workbench reads nothing during the prerender",
    stubDrawers.Calls == 0 && stubDrawers.CustodyCalls == 0,
    $"registers {stubDrawers.Calls}, custody {stubDrawers.CustodyCalls}");
Check("the register workbench states the custody rule before a single click",
    registersHtml.Contains("carried forward to a named custodian", StringComparison.Ordinal)
        && registersHtml.Contains("never netted out", StringComparison.Ordinal)
        && registersHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the register workbench asks for no public resource and no identifier a human must type",
    !registersHtml.Contains("http://", StringComparison.Ordinal) && !registersHtml.Contains("https://", StringComparison.Ordinal)
        && !registersHtml.Contains("Register id", StringComparison.Ordinal));

// The drawer sentences the close panel renders, proven where they are written.
Check("a matching count promises no journal", QtsBilling.Ui.Pages.Sales.RegisterRules.VarianceSentence(100m, 100m, "QAR")
    .Contains("no journal is produced", StringComparison.Ordinal));
Check("a shortage is named as a shortage and the rejected-sale rule is stated with it",
    QtsBilling.Ui.Pages.Sales.RegisterRules.VarianceSentence(97.5m, 100m, "QAR").Contains("Short by 2.50 QAR", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.RegisterRules.VarianceSentence(97.5m, 100m, "QAR").Contains("never netted out", StringComparison.OrdinalIgnoreCase));
Check("money the drawer holds by fractions is not rounded away",
    QtsBilling.Ui.Pages.Sales.RegisterRules.Money(0.005m) == "0.0050"
        && QtsBilling.Ui.Pages.Sales.RegisterRules.Money(2.5m) == "2.50");
Check("only what the server cannot see needs a declared figure",
    !QtsBilling.Ui.Pages.Sales.RegisterRules.NeedsDeclaredAmount(new QtsBilling.Contracts.Sales.UnresolvedCashItemDto(
        QtsBilling.Contracts.Sales.CashCustodySourceState.ProvisionalPayment, Guid.NewGuid(), "payment", 30m, "QAR", null, null, null, "x"))
    && QtsBilling.Ui.Pages.Sales.RegisterRules.NeedsDeclaredAmount(new QtsBilling.Contracts.Sales.UnresolvedCashItemDto(
        QtsBilling.Contracts.Sales.CashCustodySourceState.DeviceCustody, Guid.NewGuid(), "terminal", 0m, "QAR", null, null, null, "x")));
Check("a command key stays inside the header bound the server validates",
    QtsBilling.Ui.Pages.Sales.RegisterRules.NewCommandKey("drawer-close").Length <= 64);

Console.WriteLine(failures == 0
    ? "== smoke PASSED: the same customer component builds and renders with injected feature interfaces =="
    : $"== smoke FAILED: {failures} check(s) failed ==");

return failures == 0 ? 0 : 1;

static InvoiceDetailDto InvoiceSample(Guid id, InvoiceState status, InvoiceRecognitionMode mode, Guid locationId, Guid customerId) => new(
    new InvoiceSummaryDto(
        id,
        status == InvoiceState.Draft ? null : "INV-0001",
        status,
        customerId,
        "Acme Ltd",
        DateOnly.FromDateTime(DateTime.Today),
        null,
        "USD",
        SubtotalAmount: 100m,
        DiscountAmount: 0m,
        TaxAmount: 15m,
        TotalAmount: 115m,
        PaidAmount: 0m,
        CreditedAmount: 0m,
        BalanceDue: status == InvoiceState.Issued ? 115m : 0m,
        Note: null,
        IssuedAtUtc: status == InvoiceState.Draft ? null : DateTimeOffset.UtcNow,
        VoidedAtUtc: null,
        Version: 1,
        RecognitionMode: mode),
    [
        new InvoiceLineDto(
            Guid.CreateVersion7(),
            1,
            null,
            "Consulting",
            null,
            1m,
            100m,
            0m,
            TaxRate: 0.15m,
            TaxAmount: 15m,
            LineTotal: 115m,
            TaxMode: "Exclusive",
            RevenueBaseAmount: 100m),
    ],
    [],
    null,
    LocationId: locationId);

internal sealed class StubCustomerApiClient : ICustomerApiClient
{
    /// <summary>How often the screen asked for a page, so a prerender that reads is visible as a failure.</summary>
    public int ListCalls { get; private set; }

    public Task<PagedResult<CustomerDto>> GetCustomersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default)
    {
        ListCalls++;

        return Task.FromResult(PagedResult<CustomerDto>.Empty(pageSize));
    }

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

    public Task<CashSessionDetailDto> OpenCashDrawerAsync(Guid registerId, decimal openingFloat, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PosCatalogPage> GetCatalogAsync(Guid locationId, string? search = null, string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new PosCatalogPage([], null));

    public Task<SaleCaptureResult> CaptureCashSaleAsync(Guid locationId, Guid registerId, LocalCashSaleDraft draft, CancellationToken cancellationToken = default)
        => Task.FromResult(new SaleCaptureResult(Guid.NewGuid(), "S-SMOKE", "USD", 0m, 0m, DateTimeOffset.UtcNow, false));
}

internal sealed class StubAccountApiClient : IAccountApiClient
{
    private readonly string[] _permissions;

    public StubAccountApiClient(string[]? permissions = null)
    {
        _permissions = permissions ?? [.. Permissions.All];
    }

    public Guid BusinessId { get; } = Guid.CreateVersion7();

    public Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke does not sign in.");

    public Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SessionDto?>(new SessionDto(
            Guid.CreateVersion7(),
            "owner@example.test",
            "Smoke Owner",
            BusinessId,
            "Smoke Business",
            ["Owner"],
            [.. _permissions],
            PasswordResetRequired: false)
        {
            // A second company makes the shell's context switch reachable in the rendered markup.
            AvailableBusinesses =
            [
                new BusinessSummaryDto(BusinessId, "Smoke Business", "Owner"),
                new BusinessSummaryDto(Guid.CreateVersion7(), "Smoke Storage", "Manager"),
            ],
        });

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SessionDto> SwitchBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke does not switch business; it renders the confirmation.");

    public Task<PasswordResetTicketDto?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
        => Task.FromResult<PasswordResetTicketDto?>(null);

    public Task CompletePasswordResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CompleteSetupTicketAsync(QtsBilling.Contracts.Administration.CompleteSetupTicketRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>A host's navigation contribution: the desktop terminal ships the point of sale, the browser ships nothing.</summary>
internal sealed class StubContributor(IReadOnlyList<NavItem> items) : INavContributor
{
    public IReadOnlyList<NavItem> Items { get; } = items;
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

/// <summary>
/// The invoice lifecycle as the shared pages see it: reads, saves and issue only. Money effects belong to
/// the server, so the smoke refuses the mutating commands instead of pretending to perform them.
/// </summary>
internal sealed class StubInvoiceApiClient(
    InvoiceDetailDto draft,
    InvoiceDetailDto issued,
    InvoiceDetailDto unknown) : IInvoiceApiClient
{
    public Task<PagedResult<InvoiceSummaryDto>> ListAsync(
        InvoiceState? status = null,
        Guid? customerId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new PagedResult<InvoiceSummaryDto>(
            [draft.Invoice, issued.Invoice, unknown.Invoice],
            NextCursor: null,
            pageSize));

    public Task<InvoiceDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id switch
    {
        var requested when requested == issued.Invoice.Id => issued,
        var requested when requested == unknown.Invoice.Id => unknown,
        _ => draft,
    });

    public Task<InvoiceDetailDto> CreateAsync(SaveInvoiceRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(draft with { Invoice = draft.Invoice with { Status = InvoiceState.Draft } });

    public Task<InvoiceDetailDto> UpdateAsync(Guid id, SaveInvoiceRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(draft);

    public Task<InvoiceDetailDto> IssueAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(issued);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetPrintHtmlAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult("<html><body>invoice</body></html>");

    public Task<InvoiceDetailDto> VoidAsync(Guid id, InvoiceVoidRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never voids a document.");

    public Task<InvoiceDetailDto> CreditAsync(Guid id, InvoiceCreditNoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never credits a document.");

    public Task<InvoicePaymentResult> AddPaymentAsync(Guid id, InvoicePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never records money.");

    public Task<PaymentTransitionDto> ClearPaymentAsync(Guid paymentId, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never clears money.");

    public Task<PaymentTransitionDto> BouncePaymentAsync(Guid paymentId, PaymentBounceRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never bounces money.");
}

/// <summary>Only the location list is needed to render the editor; stock commands stay refused here.</summary>
internal sealed class StubInventoryApiClient(Guid locationId) : IInventoryApiClient
{
    public Task<IReadOnlyList<BusinessLocationDto>> GetLocationsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BusinessLocationDto>>([new BusinessLocationDto(locationId, "Main store", "MAIN", null, true, 1)]);

    public Task<PagedResult<InventoryBalanceDto>> GetBalancesAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 50,
        Guid? locationId = null,
        Guid? variantId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PagedResult<InventoryBalanceDto>.Empty(pageSize));

    public Task<IReadOnlyList<StockTransferSummaryDto>> GetTransfersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StockTransferSummaryDto>>([]);

    public Task<StockTransferDetailDto> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never moves stock.");

    public Task<StockTransferDetailDto> CreateTransferAsync(CreateStockTransferRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never moves stock.");

    public Task<StockTransferDetailDto> DispatchTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never moves stock.");

    public Task<StockTransferDetailDto> ReceiveTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never moves stock.");

    public Task<IReadOnlyList<StockCountSummaryDto>> GetStockCountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StockCountSummaryDto>>([]);

    public Task<StockCountDetailDto> GetStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never counts stock.");

    public Task<StockCountDetailDto> CreateStockCountAsync(CreateStockCountRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never counts stock.");

    public Task<StockCountDetailDto> RecordStockCountLinesAsync(Guid countId, RecordStockCountLinesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never counts stock.");

    public Task<StockCountDetailDto> ApproveStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never counts stock.");

    public Task<StockCountDetailDto> CancelStockCountAsync(Guid countId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never counts stock.");
}

/// <summary>
/// The catalog contract as the render smoke sees it. Every read is counted, so a page that reaches for data during
/// the prerender fails the run instead of appearing to work (specification task 13's rule, task 18's pages).
/// </summary>
internal sealed class StubProductApiClient : IProductApiClient
{
    public int Calls { get; private set; }

    public Task<PagedResult<ProductDto>> GetProductsAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(PagedResult<ProductDto>.Empty(pageSize));
    }

    public Task<ProductDetailDto> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ProductDetailDto(
            new ProductDto(id, "Stub product", "STUB-1", null, Guid.NewGuid(), true, true, 3, 0.15m, CatalogRules.TaxExclusive),
            []));
    }

    public Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductDetailDto> UpdateProductAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductDetailDto> CreateVariantAsync(Guid id, CreateProductVariantRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductVariantDto> UpdateVariantAsync(Guid id, Guid variantId, UpdateVariantRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductDetailDto> ArchiveVariantAsync(Guid id, Guid variantId, ArchiveVariantRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductDetailDto> ArchiveProductAsync(Guid id, ArchiveProductRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");

    public Task<ProductDetailDto> RestoreProductAsync(Guid id, RestoreProductRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes the catalog.");
}

/// <summary>
/// The register contract as the render smoke sees it: reads are counted so a page that reaches for the network
/// during the prerender fails the run, and no drawer command is executed here (specification task 19).
/// </summary>
internal sealed class StubCashRegisterApiClient : ICashRegisterApiClient
{
    public int Calls { get; private set; }

    public int CustodyCalls { get; private set; }

    public Task<IReadOnlyList<CashRegisterDto>> GetRegistersAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<CashRegisterDto>>([]);
    }

    public Task<CashRegisterDto> CreateRegisterAsync(CreateCashRegisterRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never creates a register.");

    public Task<CashSessionDetailDto> OpenSessionAsync(
        Guid registerId, OpenCashRegisterSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never opens a drawer.");

    public Task<CashSessionDetailDto> GetCurrentSessionAsync(Guid registerId, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new QtsBilling.Contracts.Abstractions.ApiException(404, QtsBilling.Contracts.Abstractions.ApiErrorCodes.CashRegisterNotOpen, "no open drawer");
    }

    public Task<PagedResult<CashRegisterSessionDto>> ListSessionsAsync(
        Guid registerId, string? cursor = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(QtsBilling.Contracts.Abstractions.PagedResult<CashRegisterSessionDto>.Empty(pageSize));
    }

    public Task<CashSessionDetailDto> GetSessionAsync(Guid registerId, Guid sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never reads one session.");

    public Task<CashMovementDto> AddMovementAsync(
        Guid registerId, Guid sessionId, CashMovementRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never moves drawer cash.");

    public Task<DrawerCustodyDto> GetCustodyAsync(Guid registerId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        CustodyCalls++;
        throw new NotSupportedException("The smoke never reads a custody position.");
    }

    public Task<IReadOnlyList<CashCustodianDto>> GetCustodiansAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CashCustodianDto>>([]);

    public Task<DrawerCustodyDto> CreateCustodyTransferAsync(
        Guid registerId, Guid sessionId, CreateCustodyTransferRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never carries cash forward.");

    public Task<CashSessionDetailDto> CloseSessionAsync(
        Guid registerId, Guid sessionId, CloseCashRegisterSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never closes a drawer.");
}

/// <summary>Reference lists are read-only here, with one entry each so a picker has something to name.</summary>
internal sealed class StubMasterDataApiClient : IMasterDataApiClient
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<MasterDataDto>> ListAsync(
        MasterDataResource resource,
        bool? includeInactive = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<MasterDataDto>>(
            [new MasterDataDto(Guid.NewGuid(), $"Stub {IMasterDataApiClient.RouteOf(resource)}", null, null, null, true, 1)]);
    }

    public Task<MasterDataDto> CreateAsync(MasterDataResource resource, SaveMasterDataRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes reference data.");

    public Task<MasterDataDto> UpdateAsync(MasterDataResource resource, Guid id, SaveMasterDataRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes reference data.");

    public Task DeactivateAsync(MasterDataResource resource, Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never retires reference data.");
}

/// <summary>One tax rate, read only, so the editor's price preview has a real rate to split on.</summary>
internal sealed class StubPricingApiClient : IPricingApiClient
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<TaxRateDto>> ListTaxRatesAsync(bool? includeInactive = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TaxRateDto>>(
            [new TaxRateDto(Guid.NewGuid(), "VAT 15", 0.15m, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
    }

    public Task<TaxRateDto> GetTaxRateAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never reads one rate.");

    public Task<TaxRateDto> CreateTaxRateAsync(SaveTaxRateRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes a rate.");

    public Task<TaxRateDto> UpdateTaxRateAsync(Guid id, SaveTaxRateRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes a rate.");

    public Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never retires a rate.");

    public Task<IReadOnlyList<DiscountDto>> ListDiscountsAsync(
        bool? includeInactive = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<DiscountDto>>([]);
    }

    public Task<DiscountDto> GetDiscountAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never reads one discount.");

    public Task<DiscountDto> CreateDiscountAsync(SaveDiscountRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes a discount.");

    public Task<DiscountDto> UpdateDiscountAsync(Guid id, SaveDiscountRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never writes a discount.");

    public Task DeactivateDiscountAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never retires a discount.");
}
