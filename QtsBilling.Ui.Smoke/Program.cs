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
using QtsBilling.Contracts.Orders;
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
Check("browser POS is LocalCore: no card-acquiring control is offered",
    !posHtml.Contains("card-intents", StringComparison.OrdinalIgnoreCase)
        && !posHtml.Contains("Card payment", StringComparison.OrdinalIgnoreCase)
        && !posHtml.Contains("tap or insert", StringComparison.OrdinalIgnoreCase)
        && !posHtml.Contains("PSP", StringComparison.OrdinalIgnoreCase));
Check("browser POS says its own total is the estimate and the receipt is the answer",
    posHtml.Contains("the screen's estimate", StringComparison.Ordinal)
        && posHtml.Contains("posted receipt", StringComparison.Ordinal));

Console.WriteLine($"     rendered markup: {html.Length} chars");

// 5. The invoice editor must render every permission and lifecycle branch through injected clients alone
//    (spec T02.01/T02.04): issue reachable on a draft, readers not handed an editable form, issued drafts
//    frozen, and a pre-recognition draft honestly refused.
var draftId = Guid.CreateVersion7();
var issuedId = Guid.CreateVersion7();
var unknownId = Guid.CreateVersion7();
var settledId = Guid.CreateVersion7();
var smokeLocationId = Guid.CreateVersion7();
var smokeCustomerId = Guid.CreateVersion7();

// The settlement workbench is proven from a document that has already taken money: part collected, one tender
// still on its way, one line credited, and the business's own scale and calendar attached to it (task 24).
var settledDetail = InvoiceSample(settledId, InvoiceState.PartiallyPaid, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId) with
{
    Invoice = InvoiceSample(settledId, InvoiceState.PartiallyPaid, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId).Invoice with
    {
        InvoiceNumber = "INV-0042",
        Status = InvoiceState.PartiallyPaid,
        PaidAmount = 40m,
        CreditedAmount = 15m,
        BalanceDue = 60m,
    },
    Payments =
    [
        new InvoicePaymentDto(Guid.CreateVersion7(), "Cash", "Confirmed", 40m, "USD", null, DateTimeOffset.UtcNow.AddHours(-3)),
        new InvoicePaymentDto(Guid.CreateVersion7(), "Cheque", "Pending", 25m, "USD", "CHEQUE-9", DateTimeOffset.UtcNow.AddHours(-1)),
    ],
    AwaitingClearanceAmount = 25m,
    Presentation = new InvoicePresentationDto(3, "Pacific/Auckland", DateOnly.FromDateTime(DateTime.UtcNow)),
    Source = new InvoiceSourceDto("sale", Guid.CreateVersion7(), "S-1001"),
};

StubInvoiceApiClient? invoiceStub = null;
StubDocumentOutput? outputStub = null;

async Task<string> RenderInvoiceEditor(Guid? invoiceId, string[] permissions)
{
    var editorServices = new ServiceCollection();
    editorServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    editorServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
    editorServices.AddMudServices();
    editorServices.AddSingleton<ICustomerApiClient, StubCustomerApiClient>();
    editorServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient(permissions));
    editorServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(smokeLocationId));
    editorServices.AddSingleton<ICashRegisterApiClient, StubCashRegisterApiClient>();
    var documentOutput = new StubDocumentOutput();
    editorServices.AddSingleton<IDocumentOutput>(documentOutput);
    var invoiceApi = new StubInvoiceApiClient(
        draft: InvoiceSample(draftId, InvoiceState.Draft, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId),
        issued: InvoiceSample(issuedId, InvoiceState.Issued, InvoiceRecognitionMode.Service, smokeLocationId, smokeCustomerId),
        unknown: InvoiceSample(unknownId, InvoiceState.Draft, InvoiceRecognitionMode.HistoricalUnknown, smokeLocationId, smokeCustomerId),
        settled: settledDetail);
    editorServices.AddSingleton<IInvoiceApiClient>(invoiceApi);
    editorServices.AddSingleton<NavigationManager, StubNavigationManager>();
    invoiceStub = invoiceApi;
    outputStub = documentOutput;

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

// 5b. The settlement workbench (specification task 24, recipe steps 1-4) on a document that has already taken
//     money: four figures read apart, the uncleared tender named for what it is, the business's own scale used
//     instead of an assumed two decimals, printing offered as a local action with no link into a protected route,
//     and every button held back from a role that cannot actually press it.
var settlementHtml = await RenderInvoiceEditor(settledId, [Permissions.InvoicesManage, Permissions.InvoicesIssue, Permissions.AccountingPost, Permissions.SalesPost]);
Check("the workbench reads collected, awaiting, credited and due as four separate figures",
    settlementHtml.Contains("data-qts-figure=\"paid\"", StringComparison.Ordinal)
        && settlementHtml.Contains("data-qts-figure=\"awaiting\"", StringComparison.Ordinal)
        && settlementHtml.Contains("data-qts-figure=\"credited\"", StringComparison.Ordinal)
        && settlementHtml.Contains("data-qts-figure=\"due\"", StringComparison.Ordinal));
Check("figures are read at the business's own settlement scale, not at two decimals someone assumed",
    settlementHtml.Contains("60.000", StringComparison.Ordinal)
        && settlementHtml.Contains("25.000", StringComparison.Ordinal)
        && !settlementHtml.Contains("60.00 USD", StringComparison.Ordinal)
        && settlementHtml.Contains("data-qts-scale=\"3\"", StringComparison.Ordinal));
Check("an uncleared tender is awaiting clearance and never counted as collected money",
    settlementHtml.Contains("Tenders awaiting clearance", StringComparison.Ordinal)
        && settlementHtml.Contains("CHEQUE-9", StringComparison.Ordinal)
        && settlementHtml.Contains("not yet this business's money", StringComparison.Ordinal)
        && settlementHtml.Contains("A cheque or transfer that has not cleared", StringComparison.Ordinal));
Check("the document names the sale it was made from, by its number",
    settlementHtml.Contains("S-1001", StringComparison.Ordinal) && settlementHtml.Contains("sales/", StringComparison.Ordinal));
Check("printing is a local action, not a link into a bearer-protected route",
    settlementHtml.Contains("Print or save as PDF", StringComparison.Ordinal)
        && !settlementHtml.Contains("/api/v1/invoices", StringComparison.Ordinal)
        && settlementHtml.Contains("local print dialogue", StringComparison.Ordinal));
Check("a render never reaches for the printer or the drawer on its own",
    invoiceStub!.PrintFetches == 0 && outputStub!.Presented.Count == 0);
Check("a document that holds money is not offered a void, and still offers what it can settle",
    !settlementHtml.Contains("Void this document", StringComparison.Ordinal)
        && settlementHtml.Contains("Record a tender", StringComparison.Ordinal)
        && settlementHtml.Contains("Credit this document", StringComparison.Ordinal));
Check("the events of the document are read back as history, not as an editable list",
    settlementHtml.Contains("What has happened to this document", StringComparison.Ordinal)
        && settlementHtml.Contains("Cheque recorded", StringComparison.Ordinal)
        && settlementHtml.Contains("Cash recorded", StringComparison.Ordinal));
Check("no field asks the operator for an identifier a human could not know",
    !settlementHtml.Contains("Invoice id", StringComparison.Ordinal) && !settlementHtml.Contains("Register id", StringComparison.Ordinal));

var noPostHtml = await RenderInvoiceEditor(settledId, [Permissions.InvoicesManage, Permissions.InvoicesIssue]);
Check("a role that may not post to the ledger is told what is missing instead of being handed the buttons",
    !noPostHtml.Contains("Confirm as collected", StringComparison.Ordinal)
        && noPostHtml.Contains("needs the accounting-posting authority", StringComparison.Ordinal));
Check("a document with nothing collected may still be voided, with its reason asked for",
    issuedHtml.Contains("Void this document", StringComparison.Ordinal) && issuedHtml.Contains("Reason for the void", StringComparison.Ordinal));
Check("a draft settles nothing: the workbench is not offered before issue",
    !draftHtml.Contains("Record a tender", StringComparison.Ordinal) && draftHtml.Contains("Issue invoice", StringComparison.Ordinal));

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

// 12. The sale history and refund workbench (specification task 21): both screens are shipped shared pages, they
//     read nothing during the prerender, the reprint is marked as a copy of a snapshot, the refund panel binds every
//     limit to the server's own position, and an account without the refund authority is told what it lacks instead
//     of being handed a button that would be refused.
var saleLocationId = Guid.CreateVersion7();
var saleRegisterId = Guid.CreateVersion7();
var saleLineId = Guid.CreateVersion7();
var stubSalesManager = new StubSaleApiClient(saleLocationId, saleRegisterId, saleLineId);
var salesServices = new ServiceCollection();
salesServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
salesServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
salesServices.AddMudServices();
salesServices.AddSingleton<NavigationManager, StubNavigationManager>();
salesServices.AddSingleton<ISaleApiClient>(stubSalesManager);
salesServices.AddSingleton<ICustomerApiClient>(new StubCustomerApiClient());
salesServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(saleLocationId));
salesServices.AddSingleton<IBusinessSetupApiClient>(new StubBusinessSetupApiClient());
salesServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([.. Permissions.All]));

await using var salesProvider = salesServices.BuildServiceProvider();
await using var salesRenderer = new HtmlRenderer(salesProvider, salesProvider.GetRequiredService<ILoggerFactory>());

var salesHtml = await salesRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await salesRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Sales.Sales>(ParameterView.Empty);
    return output.ToHtmlString();
});

var detailHtml = await salesRenderer.Dispatcher.InvokeAsync(async () =>
{
    var parameters = new Dictionary<string, object?> { ["SaleId"] = Guid.CreateVersion7() };
    var output = await salesRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Sales.SaleDetail>(
        ParameterView.FromDictionary(parameters));
    return output.ToHtmlString();
});

Check("the sale history and the sale detail are shared pages with their own routes",
    typeof(QtsBilling.Ui.Pages.Sales.Sales).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "sales")
        && typeof(QtsBilling.Ui.Pages.Sales.SaleDetail).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "sales/{SaleId:guid}"));
Check("the sales workbench reads nothing during the prerender",
    stubSalesManager.Calls == 0, $"reads: {stubSalesManager.Calls}");
Check("the sale history says what its rows are before any filter is touched",
    salesHtml.Contains("A sale listed here was written by the server", StringComparison.Ordinal)
        && salesHtml.Contains("it stays a recovery case", StringComparison.Ordinal)
        && salesHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the history search is stated as a server search over every record, not the loaded page",
    QtsBilling.Ui.Pages.Sales.SalesRules.SearchHelp(20).Contains("Searches the sale number on the server", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.SalesRules.SearchHelp(20).Contains("20 rows at a time", StringComparison.Ordinal));
Check("the sale detail renders its loading answer rather than an invented file",
    detailHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && detailHtml.Contains("never allocates a second number", StringComparison.Ordinal) == false);
Check("the sales workbench asks for no public resource and no identifier a human must type",
    !salesHtml.Contains("http://", StringComparison.Ordinal) && !salesHtml.Contains("https://", StringComparison.Ordinal)
        && !detailHtml.Contains("http://", StringComparison.Ordinal)
        && !detailHtml.Contains("Sale id", StringComparison.Ordinal));

// The sentences the two panels state, proven where they are written (the same rule the register workbench follows:
// a shared page shows what a rule class states, so the wording is testable without a browser).
Check("a reprint is stated as a copy that posts nothing and allocates no number",
    QtsBilling.Ui.Pages.Sales.SalesRules.ReprintNote.Contains("marked COPY", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.SalesRules.ReprintNote.Contains("never allocates a second number", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.SalesRules.ReprintNote.Contains("never today's", StringComparison.OrdinalIgnoreCase));
Check("the drawer rule and the preview rule are stated before an operator can act",
    QtsBilling.Ui.Pages.Sales.SalesRules.DrawerRuleNote.Contains("refuses a payout bigger than the cash", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.SalesRules.PreviewNote.Contains("the server decides the figure", StringComparison.Ordinal));
Check("an account without the refund authority is told exactly what it lacks",
    QtsBilling.Ui.Pages.Sales.SalesRules.RefundAuthorityNote(false)
        .Contains(Permissions.SalesRefund, StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Sales.SalesRules.RefundAuthorityNote(true).Length == 0);
Check("a refund cannot be offered before the payout is deliberately confirmed",
    !QtsBilling.Ui.Pages.Sales.SalesRules.CanSubmit(
        "customer changed their mind", saleRegisterId, false,
        new Dictionary<Guid, decimal> { [saleLineId] = 1m }, StubSaleApiClient.SamplePosition(saleRegisterId, saleLineId))
        && QtsBilling.Ui.Pages.Sales.SalesRules.CanSubmit(
            "customer changed their mind", saleRegisterId, true,
            new Dictionary<Guid, decimal> { [saleLineId] = 1m }, StubSaleApiClient.SamplePosition(saleRegisterId, saleLineId)));
Check("a refund with no quantity, no drawer or a blank reason is not offered",
    !QtsBilling.Ui.Pages.Sales.SalesRules.CanSubmit(
        " ", saleRegisterId, true, new Dictionary<Guid, decimal> { [saleLineId] = 1m }, StubSaleApiClient.SamplePosition(saleRegisterId, saleLineId))
        && !QtsBilling.Ui.Pages.Sales.SalesRules.CanSubmit(
            "goods undeliverable", null, true, new Dictionary<Guid, decimal> { [saleLineId] = 1m }, StubSaleApiClient.SamplePosition(saleRegisterId, saleLineId))
        && !QtsBilling.Ui.Pages.Sales.SalesRules.CanSubmit(
            "goods undeliverable", saleRegisterId, true, new Dictionary<Guid, decimal>(), StubSaleApiClient.SamplePosition(saleRegisterId, saleLineId)));

var stubSalesCashier = new StubSaleApiClient(saleLocationId, saleRegisterId, saleLineId);
var cashierServices = new ServiceCollection();
cashierServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
cashierServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
cashierServices.AddMudServices();
cashierServices.AddSingleton<NavigationManager, StubNavigationManager>();
cashierServices.AddSingleton<ISaleApiClient>(stubSalesCashier);
cashierServices.AddSingleton<ICustomerApiClient>(new StubCustomerApiClient());
cashierServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(saleLocationId));
cashierServices.AddSingleton<IBusinessSetupApiClient>(new StubBusinessSetupApiClient());
cashierServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([Permissions.SalesPost]));

await using var cashierProvider = cashierServices.BuildServiceProvider();
await using var cashierRenderer = new HtmlRenderer(cashierProvider, cashierProvider.GetRequiredService<ILoggerFactory>());

var cashierDetailHtml = await cashierRenderer.Dispatcher.InvokeAsync(async () =>
{
    var parameters = new Dictionary<string, object?> { ["SaleId"] = Guid.CreateVersion7() };
    var output = await cashierRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Sales.SaleDetail>(
        ParameterView.FromDictionary(parameters));
    return output.ToHtmlString();
});
Check("an account without the refund authority still resolves the screen and reads nothing early",
    cashierDetailHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && stubSalesCashier.Calls == 0,
    $"reads: {stubSalesCashier.Calls}");

Check("a preview of the whole remainder repeats the server's residual instead of recomputing it",
    QtsBilling.Ui.Pages.Sales.SalesRules.PreviewLineTotal(StubSaleApiClient.SampleLine(saleLineId), 3m) == 46.5m);
Check("an unconfirmed payment is named as money that has not arrived",
    QtsBilling.Ui.Pages.Sales.SalesRules.StateText(StubSaleApiClient.SampleRow(saleLocationId, PaymentStatus.Pending))
        .Contains("not cash in a till", StringComparison.OrdinalIgnoreCase)
        && QtsBilling.Ui.Pages.Sales.SalesRules.IsUncollected(StubSaleApiClient.SampleRow(saleLocationId, PaymentStatus.Pending)));
Check("a refusal that proves nothing committed frees the attempt, a lost answer does not",
    QtsBilling.Ui.Pages.Sales.SalesRules.CommittedNothing(ApiErrorCodes.InsufficientDrawerCash)
        && !QtsBilling.Ui.Pages.Sales.SalesRules.CommittedNothing(ApiErrorCodes.TransientRetry));
Check("a refund key stays inside the header bound the server validates",
    QtsBilling.Ui.Pages.Sales.SalesRules.NewRefundKey("S-2026-0001").Length <= 64);

// 13. The quote and sales-order workbenches (specification task 22): three shipped shared pages, silent during the
//     prerender, stating what a commitment is before anything is clicked, and the lifecycle rules - which actions a
//     status allows, which need a reason, that a sent quote is only ever superseded by a new revision, and that a
//     reprint or a "send" never claims a delivery happened.
var quoteBranchId = Guid.CreateVersion7();
var quoteId = Guid.CreateVersion7();
var orderId = Guid.CreateVersion7();
var stubQuotes = new StubQuoteApiClient(quoteBranchId, quoteId);
var orderServices = new ServiceCollection();
orderServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
orderServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
orderServices.AddMudServices();
orderServices.AddSingleton<NavigationManager, StubNavigationManager>();
orderServices.AddSingleton<IQuoteApiClient>(stubQuotes);
orderServices.AddSingleton<ISalesOrderApiClient>(new StubSalesOrderApiClient(quoteBranchId, quoteId, orderId));
orderServices.AddSingleton<ICustomerApiClient>(new StubCustomerApiClient());
orderServices.AddSingleton<IProductApiClient>(new StubProductApiClient());
orderServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(quoteBranchId));
orderServices.AddSingleton<IPricingApiClient>(new StubPricingApiClient());
orderServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([.. Permissions.All]));

await using var orderProvider = orderServices.BuildServiceProvider();
await using var orderRenderer = new HtmlRenderer(orderProvider, orderProvider.GetRequiredService<ILoggerFactory>());

var quotesHtml = await orderRenderer.Dispatcher.InvokeAsync(async () =>
    (await orderRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Orders.Quotes>(ParameterView.Empty)).ToHtmlString());

var quoteEditorHtml = await orderRenderer.Dispatcher.InvokeAsync(async () =>
{
    var parameters = new Dictionary<string, object?> { ["QuoteId"] = quoteId };

    return (await orderRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Orders.QuoteEditor>(
        ParameterView.FromDictionary(parameters))).ToHtmlString();
});

var ordersHtml = await orderRenderer.Dispatcher.InvokeAsync(async () =>
    (await orderRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Orders.SalesOrders>(ParameterView.Empty)).ToHtmlString());

Check("the quote and order screens are shared pages with their own routes",
    typeof(QtsBilling.Ui.Pages.Orders.Quotes).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "orders/quotes")
        && typeof(QtsBilling.Ui.Pages.Orders.QuoteEditor).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "orders/quotes/{QuoteId:guid}")
        && typeof(QtsBilling.Ui.Pages.Orders.SalesOrders).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "orders/sales-orders")
        && typeof(QtsBilling.Ui.Pages.Orders.SalesOrders).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "orders/sales-orders/{OrderId:guid}"));
Check("the order workbenches read nothing during the prerender",
    stubQuotes.Calls == 0 && stubQuotes.WriteCalls == 0,
    $"reads {stubQuotes.Calls}, writes {stubQuotes.WriteCalls}");
Check("the quote and order lists state that a commitment moves no stock and recognises no revenue",
    quotesHtml.Contains("moves no stock and recognises no revenue", StringComparison.Ordinal)
        && ordersHtml.Contains("moves no stock and recognises no revenue", StringComparison.Ordinal)
        && quotesHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && ordersHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the quote editor resolves its contracts and paints its loading answer, not a finished document",
    quoteEditorHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && !quoteEditorHtml.Contains("Convert to a sales order", StringComparison.Ordinal));

Check("only the actions a status allows are offered, and the ones that need a reason are known",
    QtsBilling.Ui.Pages.Orders.OrderRules.AllowedQuoteActions("Draft", false)
        .SequenceEqual([QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.SaveDraft,
            QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.Send,
            QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.Cancel])
        && !QtsBilling.Ui.Pages.Orders.OrderRules.AllowedQuoteActions("Accepted", alreadyConverted: true).Any()
        && QtsBilling.Ui.Pages.Orders.OrderRules.AllowedQuoteActions("Accepted", false)
            .Contains(QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.Convert)
        && !QtsBilling.Ui.Pages.Orders.OrderRules.AllowedQuoteActions("Rejected", false).Any()
        && QtsBilling.Ui.Pages.Orders.OrderRules.QuoteActionNeedsReason(QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.Reject)
        && !QtsBilling.Ui.Pages.Orders.OrderRules.QuoteActionNeedsReason(QtsBilling.Ui.Pages.Orders.OrderRules.QuoteAction.Accept)
        && QtsBilling.Ui.Pages.Orders.OrderRules.AllowedOrderActions("Draft")
            .SequenceEqual([QtsBilling.Ui.Pages.Orders.OrderRules.OrderAction.Confirm])
        && QtsBilling.Ui.Pages.Orders.OrderRules.AllowedOrderActions("Fulfilled").Count == 0);
Check("sending a quote records communication and never claims a delivery happened",
    QtsBilling.Ui.Pages.Orders.OrderRules.SendNote.Contains("Nothing is emailed", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Orders.OrderRules.SendNote.Contains("no delivery is claimed", StringComparison.OrdinalIgnoreCase)
        && QtsBilling.Ui.Pages.Orders.OrderRules.RevisionNote.Contains("never rewritten", StringComparison.OrdinalIgnoreCase)
        && QtsBilling.Ui.Pages.Orders.OrderRules.RevisionNote.Contains("new revision", StringComparison.Ordinal));
Check("an amendment restates only what is outstanding, never a delivered quantity",
    QtsBilling.Ui.Pages.Orders.OrderRules.Outstanding(StubQuoteApiClient.SampleLine(Guid.CreateVersion7(), 5m, 2m, 0m)) == 3m
        && QtsBilling.Ui.Pages.Orders.OrderRules.Outstanding(StubQuoteApiClient.SampleLine(Guid.CreateVersion7(), 5m, 5m, 0m)) == 0m
        && !QtsBilling.Ui.Pages.Orders.OrderRules.IsOpen(StubQuoteApiClient.SampleLine(Guid.CreateVersion7(), 4m, 4m, 0m))
        && QtsBilling.Ui.Pages.Orders.OrderRules.OrderActionNeedsReason(QtsBilling.Ui.Pages.Orders.OrderRules.OrderAction.CancelRemainder));
Check("a refused command is explained by its own cause, and a lost answer is not called a refusal",
    QtsBilling.Ui.Pages.Orders.OrderRules.RefusalSentence(
            new ApiException(409, ApiErrorCodes.QuoteExpired, "x"), "quote")
        .Contains("Re-quote the customer", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Orders.OrderRules.RefusalSentence(
                new ApiException(409, ApiErrorCodes.CreditLimitExceeded, "limit"), "order")
            .Contains("not overridable from this screen", StringComparison.Ordinal)
        && !QtsBilling.Ui.Pages.Orders.OrderRules.CommittedNothing(ApiErrorCodes.TransientRetry));
Check("a conversion key is one intention's key and fits the header bound",
    QtsBilling.Ui.Pages.Orders.OrderRules.NewConversionKey("QT-2026-0007").Length <= 64
        && QtsBilling.Ui.Pages.Orders.OrderRules.NewConversionKey("QT-2026-0007").StartsWith("convert-QT-2026-0007-", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Orders.OrderRules.NewCommandKey("quote-draft").Length <= 64);

// 14. The procurement workbench (specification task 26): three shipped shared pages that read nothing during the
//     prerender, state that an order is a commitment rather than a receipt, and never offer a field that could
//     overwrite a received quantity. The ledger link a goods receipt carries is stated from the server's own
//     projection, so the screen cannot name an entry that was never posted.
var purchaseBranchId = Guid.CreateVersion7();
var purchaseOrderId = Guid.CreateVersion7();
var stubPurchasing = new StubPurchasingApiClient(purchaseBranchId, purchaseOrderId);
var purchasingServices = new ServiceCollection();
purchasingServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
purchasingServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
purchasingServices.AddMudServices();
purchasingServices.AddSingleton<NavigationManager, StubNavigationManager>();
purchasingServices.AddSingleton<IPurchasingApiClient>(stubPurchasing);
purchasingServices.AddSingleton<ISupplierApiClient>(new StubSupplierApiClient());
purchasingServices.AddSingleton<IProductApiClient>(new StubProductApiClient());
purchasingServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(purchaseBranchId));
purchasingServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([.. Permissions.All]));
purchasingServices.AddSingleton<IDocumentOutput>(new StubDocumentOutput());

await using var purchasingProvider = purchasingServices.BuildServiceProvider();
await using var purchasingRenderer = new HtmlRenderer(purchasingProvider, purchasingProvider.GetRequiredService<ILoggerFactory>());

var purchaseListHtml = await purchasingRenderer.Dispatcher.InvokeAsync(async () =>
    (await purchasingRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Purchasing.PurchaseOrders>(ParameterView.Empty)).ToHtmlString());

var purchaseEditorHtml = await purchasingRenderer.Dispatcher.InvokeAsync(async () =>
{
    var parameters = new Dictionary<string, object?> { ["OrderId"] = purchaseOrderId };

    return (await purchasingRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Purchasing.PurchaseOrderEditor>(
        ParameterView.FromDictionary(parameters))).ToHtmlString();
});

var receivingQueueHtml = await purchasingRenderer.Dispatcher.InvokeAsync(async () =>
    (await purchasingRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Purchasing.Receiving>(ParameterView.Empty)).ToHtmlString());

var receivingHtml = await purchasingRenderer.Dispatcher.InvokeAsync(async () =>
{
    var parameters = new Dictionary<string, object?> { ["OrderId"] = purchaseOrderId };

    return (await purchasingRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Purchasing.Receiving>(
        ParameterView.FromDictionary(parameters))).ToHtmlString();
});

Check("the procurement screens are shared pages with their own routes",
    typeof(QtsBilling.Ui.Pages.Purchasing.PurchaseOrders).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "purchasing/orders")
        && typeof(QtsBilling.Ui.Pages.Purchasing.PurchaseOrderEditor).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "purchasing/orders/{OrderId:guid}")
        && typeof(QtsBilling.Ui.Pages.Purchasing.Receiving).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "purchasing/receiving")
        && typeof(QtsBilling.Ui.Pages.Purchasing.Receiving).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
            .Any(route => route.Template.Trim('/') == "purchasing/receiving/{OrderId:guid}"));
Check("the procurement screens read nothing and post nothing during the prerender",
    stubPurchasing.Calls == 0 && stubPurchasing.WriteCalls == 0,
    $"reads {stubPurchasing.Calls}, writes {stubPurchasing.WriteCalls}");
Check("every procurement screen paints its loading answer rather than a finished document",
    purchaseListHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && purchaseEditorHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && receivingQueueHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && receivingHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal));
Check("the order list states the commitment rule and the billing rule it will not cross",
    purchaseListHtml.Contains("Placing an order commits this business to a supplier", StringComparison.Ordinal)
        && purchaseListHtml.Contains("payables workbench (task 27)", StringComparison.Ordinal)
        && purchaseEditorHtml.Contains("Placing an order commits this business to a supplier", StringComparison.Ordinal));
Check("the order screen says a received quantity cannot be typed, and receiving says what a GRN does",
    purchaseEditorHtml.Contains("no field here lets a person overwrite", StringComparison.Ordinal)
        && receivingQueueHtml.Contains("quantities land in the named branch", StringComparison.Ordinal));
Check("an order's lifecycle is stated by its state, and outstanding is never below zero",
    QtsBilling.Ui.Pages.Purchasing.PurchasingRules.AllowedActions(QtsBilling.Contracts.Purchasing.PurchaseOrderState.Ordered)
        .Contains(QtsBilling.Ui.Pages.Purchasing.PurchaseOrderAction.Receive)
        && !QtsBilling.Ui.Pages.Purchasing.PurchasingRules.AllowedActions(QtsBilling.Contracts.Purchasing.PurchaseOrderState.Ordered)
            .Contains(QtsBilling.Ui.Pages.Purchasing.PurchaseOrderAction.Edit)
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.Outstanding(
            StubPurchasingApiClient.SampleLine(Guid.CreateVersion7(), 8m, 3m)) == 5m
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.Outstanding(
            StubPurchasingApiClient.SampleLine(Guid.CreateVersion7(), 8m, 9m)) == 0m
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.Returnable(
            StubPurchasingApiClient.SampleLine(Guid.CreateVersion7(), 8m, 3m)) == 3m);
Check("the ledger link a receipt carries is the server's own, and an unposted document says so",
    QtsBilling.Ui.Pages.Purchasing.PurchasingRules.LedgerText(
        StubPurchasingApiClient.SampleReceipt("GRN-0001", "JE-0001", ["1200", "2101"])) == "JE-0001 · 1200, 2101"
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.LedgerText(
            StubPurchasingApiClient.SampleReceipt("GRN-0002", null, null)).Contains("nothing posted", StringComparison.Ordinal));
Check("a lost receipt answer keeps its key, a proven refusal releases it",
    !QtsBilling.Ui.Pages.Purchasing.PurchasingRules.CommittedNothing(ApiErrorCodes.InternalError)
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.CommittedNothing(ApiErrorCodes.OverReceipt)
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.CommittedNothing(ApiErrorCodes.SameKeyDifferentPayload)
        && QtsBilling.Ui.Pages.Purchasing.PurchasingRules.NewKey("receive", "PO-0001").Length <= 64);
Check("the printable purchase document is composed locally from snapshots and names no location",
    QtsBilling.Ui.Pages.Purchasing.PurchaseDocument.ForOrder(
            StubPurchasingApiClient.SampleDetail(purchaseBranchId, purchaseOrderId), "Main branch", reprint: true).Content
        .Contains("COPY - composed from the stored snapshot", StringComparison.Ordinal)
        && QtsBilling.Ui.Pages.Purchasing.PurchaseDocument.ForReceipt(
                StubPurchasingApiClient.SampleReceipt("GRN-0001", "JE-0001", ["1200"]), "Main branch", reprint: false).Content
            .Contains("Ledger entry", StringComparison.Ordinal));

// 15. The payables workbench (specification task 27): a shipped shared page that reads nothing during the prerender,
//     states that a bill is matched to a delivery rather than typed into existence, and offers credit only within what
//     a bill line can still yield. The reconciliation it quotes is the arithmetic, not a claim: a control account that
//     does not tie to the documents is said so.
var payablesServices = new ServiceCollection();
payablesServices.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
payablesServices.AddSingleton<IJSRuntime>(new StubJsRuntime());
payablesServices.AddMudServices();
payablesServices.AddSingleton<NavigationManager, StubNavigationManager>();
payablesServices.AddSingleton<IPurchasingApiClient>(stubPurchasing);
payablesServices.AddSingleton<ISupplierApiClient>(new StubSupplierApiClient());
payablesServices.AddSingleton<IInventoryApiClient>(new StubInventoryApiClient(purchaseBranchId));
payablesServices.AddSingleton<IAccountApiClient>(new StubAccountApiClient([.. Permissions.All]));

await using var payablesProvider = payablesServices.BuildServiceProvider();
await using var payablesRenderer = new HtmlRenderer(payablesProvider, payablesProvider.GetRequiredService<ILoggerFactory>());

var payablesHtml = await payablesRenderer.Dispatcher.InvokeAsync(async () =>
    (await payablesRenderer.RenderComponentAsync<QtsBilling.Ui.Pages.Purchasing.Payables>(ParameterView.Empty)).ToHtmlString());

Check("the payables screen is a shared page with its own route and an honest nav row",
    typeof(QtsBilling.Ui.Pages.Purchasing.Payables).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()
        .Any(route => route.Template.Trim('/') == "purchasing/payables")
        && QtsBilling.Ui.Layout.ShellNavigation.Routes
            .Single(route => route.Href == "purchasing/payables").Availability == QtsBilling.Ui.Layout.ShellAvailability.Available);
Check("the payables screen reads nothing and posts nothing during the prerender",
    stubPurchasing.Calls == 0 && stubPurchasing.WriteCalls == 0,
    $"reads {stubPurchasing.Calls}, writes {stubPurchasing.WriteCalls}");
Check("the payables screen states the match, credit and settlement rules before any account is loaded",
    payablesHtml.Contains("data-qts-state=\"loading\"", StringComparison.Ordinal)
        && payablesHtml.Contains("matched to the goods receipt lines it pays for", StringComparison.Ordinal)
        && payablesHtml.Contains("never removes stock by itself", StringComparison.Ordinal)
        && payablesHtml.Contains("imported opening payable", StringComparison.Ordinal));
Check("a bill offers only what its state allows, and a credit is bounded by what the line can still yield",
    QtsBilling.Ui.Pages.Purchasing.PayablesRules.AllowedActions(SupplierBillState.Draft)
        .SequenceEqual([QtsBilling.Ui.Pages.Purchasing.PayableAction.Issue])
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.AllowedActions(SupplierBillState.Issued)
            .Contains(QtsBilling.Ui.Pages.Purchasing.PayableAction.Credit)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.AllowedActions(SupplierBillState.Void).Count == 0
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.CreditableQuantity(
            StubPurchasingApiClient.SampleBillLine(10m, 4m)) == 6m
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.CreditableQuantity(
            StubPurchasingApiClient.SampleBillLine(10m, 10m)) == 0m
        && !QtsBilling.Ui.Pages.Purchasing.PayablesRules.IsOpen(StubPurchasingApiClient.SampleBillLine(10m, 12m)));
Check("a held credit is named as a balance owed back, and a mismatched control account is stated as unreconciled",
    QtsBilling.Ui.Pages.Purchasing.PayablesRules.ReconciliationText(
        StubPurchasingApiClient.SampleStatement(Guid.CreateVersion7(), DateOnly.FromDateTime(DateTime.UtcNow)))
        .Contains("Reconciled", StringComparison.Ordinal)
    && QtsBilling.Ui.Pages.Purchasing.PayablesRules.HeldCreditRule.Contains("negative payable", StringComparison.OrdinalIgnoreCase)
    && QtsBilling.Ui.Pages.Purchasing.PayablesRules.KindText("opening").Contains("imported opening", StringComparison.Ordinal));
Check("a lost payables answer keeps its key while a proven refusal releases it",
    !QtsBilling.Ui.Pages.Purchasing.PayablesRules.CommittedNothing(ApiErrorCodes.InternalError)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.CommittedNothing(ApiErrorCodes.OverCredit)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.CommittedNothing(ApiErrorCodes.SupplierBillNotIssued)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.CommittedNothing(ApiErrorCodes.DuplicateSupplierReference)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.RefusalSentence(
            new ApiException(409, ApiErrorCodes.OverCredit, "x"), "this credit").Contains("read the bill back", StringComparison.OrdinalIgnoreCase)
        && QtsBilling.Ui.Pages.Purchasing.PayablesRules.NewKey("settle", Guid.CreateVersion7()).Length <= 64);

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

    public Task<SaleReceiptDto> GetPostedReceiptAsync(Guid saleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never reads a posted receipt.");

    public Task<PendingCheckoutAttempt?> ReadPendingAttemptAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<PendingCheckoutAttempt?>(null);

    public Task SavePendingAttemptAsync(PendingCheckoutAttempt attempt, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ClearPendingAttemptAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
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

/// <summary>Records what a host was asked to print, and never prints it (specification task 24).</summary>
internal sealed class StubDocumentOutput : IDocumentOutput
{
    public List<DocumentOutputRequest> Presented { get; } = [];

    public bool CanPresentDocuments { get; init; } = true;

    public Task<bool> PresentAsync(DocumentOutputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Presented.Add(request);
        return Task.FromResult(true);
    }
}

/// <summary>
/// The invoice lifecycle as the shared pages see it: reads, saves and issue only. Money effects belong to
/// the server, so the smoke refuses the mutating commands instead of pretending to perform them.
/// </summary>
internal sealed class StubInvoiceApiClient(
    InvoiceDetailDto draft,
    InvoiceDetailDto issued,
    InvoiceDetailDto unknown,
    InvoiceDetailDto? settled = null) : IInvoiceApiClient
{
    /// <summary>How often the printable snapshot was asked for. The smoke never prints, so a non-zero count here
    /// would mean a render reached for the printer on its own (specification task 24).</summary>
    public int PrintFetches { get; private set; }

    /// <summary>The bodies of the money commands the screen has tried to send, in order.</summary>
    public List<InvoicePaymentRequest?> MoneyAttempts { get; } = [];

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
        var requested when settled is not null && requested == settled.Invoice.Id => settled,
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
    {
        PrintFetches++;
        return Task.FromResult("<html><body>invoice</body></html>");
    }

    public Task<DocumentOutputRequest> GetPrintDocumentAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        PrintFetches++;
        return Task.FromResult(new DocumentOutputRequest("<html><body>invoice</body></html>", title));
    }

    public Task<InvoiceDetailDto> VoidAsync(Guid id, InvoiceVoidRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never voids a document.");

    public Task<InvoiceDetailDto> CreditAsync(Guid id, InvoiceCreditNoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never credits a document.");

    public Task<InvoicePaymentResult> AddPaymentAsync(Guid id, InvoicePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        MoneyAttempts.Add(request);
        throw new NotSupportedException("The smoke never records money.");
    }

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
/// <summary>
/// The sale history and refund contract as the render smoke sees it: reads are counted so a page that reaches for
/// data during the prerender fails the run, and neither a sale nor a refund is ever written here
/// (specification task 21).
/// </summary>
internal sealed class StubSaleApiClient(Guid branchId, Guid tillId, Guid lineId) : ISaleApiClient
{
    /// <summary>How often a screen asked for its data, so a prerender that reads is visible as a failure.</summary>
    public int Calls { get; private set; }

    /// <summary>The server's refund position, as the screen receives it.</summary>
    public static SaleRefundLinePositionDto SampleLine(Guid saleLineId) => new(
        saleLineId,
        1,
        "Trainer",
        "TRN-001",
        SoldQuantity: 4m,
        RefundedQuantity: 1m,
        RemainingQuantity: 3m,
        UnitPrice: 15m,
        DiscountAmount: 0m,
        TaxRate: 0.15m,
        TaxAmount: 4.5m,
        LineTotal: 60m,
        RemainingAmount: 46.5m,
        UnitCost: 9m,
        MovedStock: true,
        TaxMode: "Exclusive");

    public static SaleRefundPositionDto SamplePosition(Guid registerId, Guid saleLineId) => new(
        Guid.CreateVersion7(),
        "S-2026-0001",
        "Posted",
        "QAR",
        TotalAmount: 60m,
        TaxAmount: 6m,
        RefundedAmount: 13.5m,
        RemainingRefundableAmount: 46.5m,
        PaymentMethod: "Cash",
        PaymentStatus: "Confirmed",
        RefundableAtRegister: true,
        RefusalCode: null,
        RefusalReason: null,
        Lines: [SampleLine(saleLineId)],
        Drawers:
        [
            new RefundDrawerOptionDto(registerId, "Front till", Guid.CreateVersion7(), 120m, 46.5m, "QAR", "Open drawer holding 120"),
            new RefundDrawerOptionDto(Guid.CreateVersion7(), "Back till", null, null, null, "QAR", "No open drawer"),
        ]);

    public static SaleHistoryDto SampleRow(Guid locationId, PaymentStatus status) => new(
        Guid.CreateVersion7(),
        "S-2026-0002",
        SaleState.Posted,
        status,
        "Cash",
        locationId,
        "Main store",
        CustomerId: null,
        CustomerName: null,
        "QAR",
        TotalAmount: 115m,
        TaxAmount: 15m,
        RefundedAmount: 0m,
        RefundCount: 0,
        DateTimeOffset.UtcNow,
        "2026-10-22 09:00:00",
        CashierName: "Cashier");

    public Task<SaleCaptureResult> PostSaleAsync(
        SaleCaptureRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never posts a sale.");

    public Task<SaleReceiptDto> GetReceiptAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new SaleReceiptDto(
            saleId,
            "S-2026-0001",
            "Posted",
            null,
            "Trader",
            null,
            "Main store",
            null,
            "QAR",
            DateTimeOffset.UtcNow,
            "2026-10-22 09:00:00",
            "Cashier",
            "Front till",
            Guid.CreateVersion7(),
            [new SaleReceiptLineDto(1, "Trainer", "TRN-001", 4m, 15m, 0m, 0.15m, 4.5m, 60m)],
            60m,
            0m,
            6m,
            60m,
            [new SaleReceiptPaymentDto("Cash", "Confirmed", 60m, 70m, 10m, null)]));
    }

    public Task<PagedResult<SaleHistoryDto>> GetSalesAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? locationId = null,
        Guid? customerId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new PagedResult<SaleHistoryDto>(
            [SampleRow(branchId, PaymentStatus.Confirmed)], null, pageSize));
    }

    public Task<PostedSaleDto> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new PostedSaleDto(
            saleId,
            "S-2026-0001",
            SaleState.Posted,
            branchId,
            CustomerId: null,
            "QAR",
            SubtotalAmount: 60m,
            DiscountAmount: 0m,
            TaxAmount: 6m,
            TotalAmount: 66m,
            PaymentStatus.Confirmed,
            Provisional: false,
            Note: "regular sale",
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            ReversalOfSaleId: null,
            Lines:
            [
                new PostedSaleLineDto(
                    lineId, 1, Guid.CreateVersion7(), "Trainer", "TRN-001",
                    4m, 15m, 0m, 0.15m, 6m, 66m, "Exclusive", 9m),
            ],
            Payments:
            [
                new PostedPaymentDto(
                    Guid.CreateVersion7(), "Cash", PaymentStatus.Confirmed, 66m, "QAR",
                    70m, null, null, DateTimeOffset.UtcNow),
            ]));
    }

    public Task<SaleRefundPositionDto> GetRefundPositionAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(SamplePosition(tillId, lineId));
    }

    public Task<IReadOnlyList<RefundDto>> GetRefundsAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<RefundDto>>(
        [
            new RefundDto(
                Guid.CreateVersion7(), "R-2026-0001", saleId, "S-2026-0001", 13.5m, "QAR", "Cash",
                "goods returned", null, DateTimeOffset.UtcNow, Guid.CreateVersion7(), Guid.CreateVersion7(),
                "JE-2026-0001",
                [new RefundLineDto(lineId, 1, "Trainer", "TRN-001", 1m, 15m, 1.5m, 13.5m, 9m, "Restock")]),
        ]);
    }

    public Task<RefundDto> RefundSaleAsync(
        Guid saleId,
        RefundSaleRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The smoke never pays cash back.");
}

/// <summary>
/// The register contract as the render smoke sees it: reads are counted so a page that reaches for the network
/// during the prerender fails the run, and no drawer command is executed here (specification task 19).
/// </summary>
/// <summary>
/// The quote contract as the render smoke sees it: reads are counted so a page that reaches for data during the
/// prerender fails the run, and no lifecycle command is executed here (specification task 22).
/// </summary>
internal sealed class StubQuoteApiClient(Guid branchId, Guid quoteId) : IQuoteApiClient
{
    /// <summary>One priced line, shared with the order stub so both screens read the same shape.</summary>
    public static OrderLineDto SampleLine(Guid lineKey, decimal ordered = 5m, decimal delivered = 2m, decimal cancelled = 0m)
        => new(Guid.CreateVersion7(), lineKey, 1, Guid.CreateVersion7(), "Trainer", "TRN-001", ordered, delivered, cancelled,
            100m, 0m, 100m * ordered, 0.15m, 15m * ordered, 115m * ordered, "Exclusive", "catalog-v1");

    public static SalesQuoteDto QuoteSample(Guid branch, Guid id, string status, Guid lineKey)
    {
        var line = SampleLine(lineKey, 2m, 0m, 0m);
        QuoteRevisionDto[] revisions = status == "Draft"
            ? []
            : [new QuoteRevisionDto(
                Guid.CreateVersion7(), 1, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
                DateOnly.FromDateTime(DateTime.Today.AddDays(45)), "QAR", 200m, 0m, 30m, 230m, 0m, "30 days",
                "Sent to customer", Guid.CreateVersion7(), DateTimeOffset.UtcNow, [line])];

        return new SalesQuoteDto(
            id, branch, CustomerId: null, "QT-2026-0007", status,
            DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            DateOnly.FromDateTime(DateTime.Today.AddDays(45)), "QAR", 200m, 0m, 30m, 230m, 0m,
            revisions.Length, 4L, Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, status == "Accepted" ? DateTimeOffset.UtcNow : null, "30 days",
            [line], revisions, "Main store", null);
    }

    public static SalesOrderDto OrderSample(Guid branch, Guid sourceQuote, Guid order)
    {
        var line = SampleLine(Guid.CreateVersion7());

        return new SalesOrderDto(
            order, branch, CustomerId: null, sourceQuote, Guid.CreateVersion7(), "SO-2026-0003", "PartiallyFulfilled",
            DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(14)), "QAR",
            500m, 0m, 75m, 575m, 0m, 1, 7L, Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, null, "deliver in one go",
            [line],
            [new SalesOrderRevisionDto(
                Guid.CreateVersion7(), 1, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(14)),
                "QAR", 500m, 0m, 75m, 575m, 0m, "Order confirmed", Guid.CreateVersion7(), DateTimeOffset.UtcNow, [line])],
            "Main store", null);
    }

    public int Calls { get; private set; }

    public int WriteCalls { get; private set; }

    public Task<PagedResult<SalesQuoteSummaryDto>> ListAsync(
        string? status = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new PagedResult<SalesQuoteSummaryDto>(
            [new SalesQuoteSummaryDto(
                quoteId, branchId, null, "QT-2026-0007", "Sent",
                DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(30)), null,
                "QAR", 230m, 1, 4L, "Main store", null)],
            null,
            pageSize));
    }

    public Task<SalesQuoteDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Sent", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> CreateAsync(CreateQuoteRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Draft", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> UpdateAsync(Guid id, UpdateQuoteRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Draft", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> SendAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Sent", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> AcceptAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Accepted", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> RejectAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Rejected", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> AmendAsync(Guid id, QuoteAmendRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Sent", Guid.CreateVersion7()));
    }

    public Task<SalesQuoteDto> CancelAsync(Guid id, QuoteCommandRequest request, CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(QuoteSample(branchId, quoteId, "Cancelled", Guid.CreateVersion7()));
    }

    public Task<QuoteConversionResultDto> ConvertAsync(
        Guid id,
        ConvertQuoteRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        return Task.FromResult(new QuoteConversionResultDto(
            QuoteSample(branchId, quoteId, "Accepted", Guid.CreateVersion7()),
            OrderSample(branchId, quoteId, Guid.CreateVersion7())));
    }
}

internal sealed class StubSalesOrderApiClient(Guid branchId, Guid quoteId, Guid orderId) : ISalesOrderApiClient
{
    public int Calls { get; private set; }

    public Task<PagedResult<SalesOrderSummaryDto>> ListAsync(
        string? status = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new PagedResult<SalesOrderSummaryDto>(
            [new SalesOrderSummaryDto(
                orderId, branchId, null, quoteId, "SO-2026-0003", "PartiallyFulfilled",
                DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(14)),
                "QAR", 575m, 1, 7L, "Main store", null)],
            null,
            pageSize));
    }

    public Task<SalesOrderDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));
    }

    public Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));

    public Task<SalesOrderDto> UpdateAsync(Guid id, UpdateSalesOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));

    public Task<SalesOrderDto> ConfirmAsync(Guid id, ConfirmSalesOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));

    public Task<SalesOrderDto> AmendAsync(Guid id, AmendSalesOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));

    public Task<SalesOrderDto> CancelRemainderAsync(Guid id, CancelOrderRemainderRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(StubQuoteApiClient.OrderSample(branchId, quoteId, orderId));
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

/// <summary>
/// The procurement contract as the render smoke sees it (specification task 26). Reads are counted so a page that
/// reaches for its order during the prerender fails the run, and every money-moving command is refused rather than
/// simulated: a goods received note is a server fact with a journal behind it, so the smoke never pretends to post
/// one.
/// </summary>
internal sealed class StubPurchasingApiClient(Guid branchId, Guid orderId) : IPurchasingApiClient
{
    /// <summary>How often a screen asked for a read, so a prerender that loads is visible as a failure.</summary>
    public int Calls { get; private set; }

    /// <summary>How often a screen tried to move stock or terms. The smoke must never see this grow.</summary>
    public int WriteCalls { get; private set; }

    public static PurchaseOrderLineDto SampleLine(Guid id, decimal ordered, decimal received) => new(
        id, 1, Guid.CreateVersion7(), "Test item", "SKU-1", ordered, received, 60m, 0m, 0m, 0m, ordered * 60m);

    public static PurchaseReceiptDto SampleReceipt(string number, string? journal, IReadOnlyList<string>? accounts) => new(
        Guid.CreateVersion7(),
        number,
        "Receipt",
        Guid.CreateVersion7(),
        "PO-0001",
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        null,
        [new PurchaseReceiptLineDto(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test item", "SKU-1", 3m, 60m)],
        journal,
        accounts);

    public static PurchaseOrderDetailDto SampleDetail(Guid locationId, Guid id) => new(
        new PurchaseOrderSummaryDto(
            id,
            "PO-0001",
            PurchaseOrderState.Ordered,
            Guid.CreateVersion7(),
            "Van supply",
            locationId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "QAR",
            600m,
            0m,
            0m,
            600m,
            "ordered for the smoke",
            DateTimeOffset.UtcNow),
        [SampleLine(Guid.CreateVersion7(), 10m, 3m)]);

    public Task<PagedResult<PurchaseOrderSummaryDto>> ListOrdersAsync(
        PurchaseOrderState? status = null,
        Guid? supplierId = null,
        Guid? locationId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(new PagedResult<PurchaseOrderSummaryDto>(
            [SampleDetail(branchId, orderId).Order], NextCursor: null, pageSize));
    }

    public Task<PurchaseOrderDetailDto> GetOrderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(SampleDetail(branchId, id));
    }

    public Task<IReadOnlyList<PurchaseReceiptDto>> ListReceiptsAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<PurchaseReceiptDto>>(
            [SampleReceipt("GRN-0001", "JE-0001", ["1200", "2101"])]);
    }

    public Task<PurchaseOrderDetailDto> CreateOrderAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseOrderDetailDto>(Refused());

    public Task<PurchaseOrderDetailDto> UpdateOrderAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseOrderDetailDto>(Refused());

    public Task DeleteOrderAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromException(Refused());

    public Task<PurchaseOrderDetailDto> ConfirmOrderAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseOrderDetailDto>(Refused());

    public Task<PurchaseOrderDetailDto> CancelOrderAsync(Guid id, PurchaseOrderActionRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseOrderDetailDto>(Refused());

    public Task<PurchaseReceiptDto> ReceiveAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseReceiptDto>(Refused());

    public Task<PurchaseReceiptDto> ReturnAsync(Guid orderId, PurchaseReceiptRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<PurchaseReceiptDto>(Refused());

    public Task<IReadOnlyList<SupplierBillDto>> ListBillsAsync(
        Guid? supplierId = null,
        bool openOnly = false,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<SupplierBillDto>>([SampleDraftBill(branchId, orderId)]);
    }

    public Task<SupplierBillDto> GetBillAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(SampleDraftBill(branchId, id));
    }

    public Task<SupplierStatementDto> GetStatementAsync(Guid supplierId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(SampleStatement(supplierId, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    public Task<SupplierBillDto> CreateBillAsync(CreateSupplierBillRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<SupplierBillDto>(Refused());

    public Task<SupplierBillDto> IssueBillAsync(Guid id, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<SupplierBillDto>(Refused());

    public Task<SupplierBillDto> CreateCreditAsync(Guid billId, CreateSupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<SupplierBillDto>(Refused());

    public Task<SupplierBillDto> ApplyCreditAsync(Guid creditId, ApplySupplierCreditRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<SupplierBillDto>(Refused());

    public Task<SupplierPaymentDto> PaySupplierAsync(CreateSupplierPaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromException<SupplierPaymentDto>(Refused());

    public static SupplierBillLineDto SampleBillLine(decimal billed, decimal credited) => new(
        Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test item", billed, 60m, billed * 60m,
        0m, 0.15m, "Exclusive", billed * 60m, billed * 9m, billed * 69m, credited);

    public static SupplierBillDto SampleDraftBill(Guid branchId, Guid id) => new(
        id,
        "SB-2026-0001",
        SupplierBillState.Draft,
        Guid.CreateVersion7(),
        branchId,
        Guid.CreateVersion7(),
        "SUP-INV-77",
        DateOnly.FromDateTime(DateTime.UtcNow),
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
        "QAR",
        600m,
        0m,
        90m,
        0m,
        690m,
        0m,
        690m,
        null,
        [new SupplierBillLineDto(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test item", 10m, 60m, 600m, 0m, 0.15m, "Exclusive", 600m, 90m, 690m, 4m)]);

    public static SupplierStatementDto SampleStatement(Guid supplierId, DateOnly asOf) => new(
        supplierId,
        "Van supply",
        "QAR",
        asOf,
        DateTimeOffset.UtcNow,
        OpeningBalance: 0m,
        MovementDebit: 0m,
        MovementCredit: 690m,
        ClosingBalance: 690m,
        UnappliedCredits: 50m,
        PayableControlBalance: 640m,
        GrniBalance: 0m,
        OpenDocumentCount: 1,
        Truncated: false,
        Lines: [new SupplierStatementLineDto("bill", Guid.CreateVersion7(), "SB-2026-0001", asOf, asOf.AddDays(30), 0m, 690m, 690m, "Supplier invoice SUP-INV-77")],
        OpenItems: [new SupplierOpenItemDto("bill", Guid.CreateVersion7(), "SB-2026-0001", asOf, asOf.AddDays(30), 690m, 0m, 690m, "QAR", false, "Supplier invoice SUP-INV-77")],
        Credits: [new SupplierCreditDto(
            Guid.CreateVersion7(), "SC-2026-0001", Guid.CreateVersion7(), "SB-2026-0000", supplierId, Guid.CreateVersion7(),
            null, null, "goods short-delivered", "QAR", 43.48m, 6.52m, 50m, 0m, 50m, Guid.CreateVersion7(), "JE-2026-0009",
            ["1300", "2000", "5150"], DateTimeOffset.UtcNow, [])],
        SettlementScale: 2,
        TimeZoneId: "Asia/Qatar");

    private NotSupportedException Refused()
    {
        WriteCalls++;

        return new NotSupportedException("The smoke never commits a purchasing document.");
    }
}
