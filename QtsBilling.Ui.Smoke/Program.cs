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
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Pages;
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
var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IJSRuntime>(new StubJsRuntime());
services.AddMudServices();
services.AddSingleton<ICustomerApiClient, StubCustomerApiClient>();
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
    private readonly string[] _permissions;

    public StubAccountApiClient(string[]? permissions = null)
    {
        _permissions = permissions ?? [.. Permissions.All];
    }

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
            [.. _permissions],
            PasswordResetRequired: false));

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PasswordResetTicketDto?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
        => Task.FromResult<PasswordResetTicketDto?>(null);

    public Task CompletePasswordResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CompleteSetupTicketAsync(QtsBilling.Contracts.Administration.CompleteSetupTicketRequest request, CancellationToken cancellationToken = default)
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
