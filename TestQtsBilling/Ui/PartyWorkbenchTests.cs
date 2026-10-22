using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The party workbench (specification task 15, T15.01 - T15.03): customers and suppliers maintained through the
/// shared screens' own typed clients, at more than one page, with deactivation that keeps history and an edit race
/// that cannot silently overwrite another operator's correction.
/// </summary>
public sealed class PartyWorkbenchTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T15_01_a_party_beyond_the_first_page_is_reachable_and_no_row_is_shown_twice_or_lost()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customers = new CustomerApiClient(owner);

        // Five customers with a term only the last one carries, at a page size of two.
        var marker = $"T15 {Guid.NewGuid():N}"[..12];
        var names = new[]
        {
            $"{marker} alpha",
            $"{marker} bravo",
            $"{marker} charlie",
            $"{marker} delta",
            $"{marker} echo-last",
        };

        foreach (var name in names)
        {
            await customers.CreateCustomerAsync(new SaveCustomerRequest(name, null, null, null));
        }

        // Walk every page with the server's cursor. The set must be exactly the five records: no repeat, none missing.
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await customers.GetCustomersAsync(search: marker, cursor: cursor, pageSize: 2);
            pages++;

            Assert.True(page.PageSize == 2, $"the server ignored the requested page size: {page.PageSize}");
            Assert.True(page.Items.Count <= 2, "a page came back larger than asked for");

            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;

            if (pages > 10)
            {
                throw new InvalidOperationException("the customer cursor never terminated");
            }
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // The last record is reachable by search alone - no fetch-all, no first-hundred assumption.
        var searched = await customers.GetCustomersAsync(search: "echo-last", pageSize: 2);
        Assert.Single(searched.Items);
        Assert.Contains(marker, searched.Items[0].Name, StringComparison.Ordinal);

        // Editing that last record, reached only through the search, works on the row the screen is holding.
        var last = searched.Items[0];
        var corrected = await customers.UpdateCustomerAsync(
            last.Id, new SaveCustomerRequest(last.Name + " (corrected)", last.Email, last.Phone, last.Address), last.Version);
        Assert.Equal(last.Name + " (corrected)", corrected.Name);

        // Suppliers behave the same way: searched and paged on the server, with the cursor honoured.
        var suppliers = new SupplierApiClient(owner);
        var supplierMarker = $"T15 supplier {Guid.NewGuid():N}"[..16];

        for (var index = 1; index <= 4; index++)
        {
            await suppliers.CreateSupplierAsync(new SaveSupplierRequest($"{supplierMarker} {index}", null, null, null));
        }

        var supplierIds = new List<Guid>();
        cursor = null;
        var supplierPages = 0;

        do
        {
            var page = await suppliers.GetSuppliersAsync(search: supplierMarker, cursor: cursor, pageSize: 2);
            supplierPages++;
            supplierIds.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;

            if (supplierPages > 10)
            {
                throw new InvalidOperationException("the supplier cursor never terminated");
            }
        }
        while (cursor is not null);

        Assert.Equal(2, supplierPages);
        Assert.Equal(4, supplierIds.Count);
        Assert.Equal(supplierIds.Count, supplierIds.Distinct().Count());

        // The page sizes the workbench asks for stay bounded: the server clamps to 200, and the screen never
        // requests the whole file.
        var bounded = await customers.GetCustomersAsync(search: marker, pageSize: 500);
        Assert.True(bounded.PageSize <= 200, $"the server let a page grow to {bounded.PageSize}");
    }

    [Fact]
    public async Task T15_02_a_deactivated_party_keeps_its_history_and_is_refused_for_new_work()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customers = new CustomerApiClient(owner);

        var name = $"T15 retiring {Guid.NewGuid():N}"[..18];
        var customer = await customers.CreateCustomerAsync(new SaveCustomerRequest(name, "retire@party.test", null, null));

        // 1. A real issued invoice names them, with the amount they owed at the time.
        var invoiceId = await CreateIssuedInvoiceAsync(owner, customer.Id, name);
        var before = await owner.GetFromJsonAsync<InvoiceDetailDto>($"/api/v1/invoices/{invoiceId}", ContractJson.Options)
            ?? throw new InvalidOperationException("the invoice could not be read back");
        Assert.Equal(name, before.Invoice.CustomerName);

        // 2. Deactivate. Nothing is deleted: the record and its documents survive.
        await customers.DeactivateCustomerAsync(customer.Id);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == customer.Id);
            Assert.False(stored.IsActive);
            Assert.Equal(name, stored.Name);
        });

        // 3. The historical document still renders the snapshot the operator relied on.
        var after = await owner.GetFromJsonAsync<InvoiceDetailDto>($"/api/v1/invoices/{invoiceId}", ContractJson.Options)
            ?? throw new InvalidOperationException("the invoice disappeared with the customer");
        Assert.Equal(name, after.Invoice.CustomerName);
        Assert.Equal(InvoiceState.Issued, after.Invoice.Status);
        Assert.Equal(before.Invoice.TotalAmount, after.Invoice.TotalAmount);
        Assert.Equal(before.Invoice.BalanceDue, after.Invoice.BalanceDue);

        // 4. New use is refused, and the refusal says which half failed.
        var rejected = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customer.Id,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "new work for a retired party",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 50m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("inactive", rejectedBody, StringComparison.OrdinalIgnoreCase);

        // 5. The workbench still reaches them - the default list hides the record, and the history view asks for it.
        var hidden = await customers.GetCustomersAsync(search: name, pageSize: 25);
        Assert.DoesNotContain(hidden.Items, item => item.Id == customer.Id);

        var shown = await customers.GetCustomersAsync(search: name, pageSize: 25, includeInactive: true);
        Assert.Contains(shown.Items, item => item.Id == customer.Id);

        // 6. A retired supplier cannot be chosen for a new order, and the order already placed still names it.
        var suppliers = new SupplierApiClient(owner);
        var supplierName = $"T15 retired supplier {Guid.NewGuid():N}"[..22];
        var supplier = await suppliers.CreateSupplierAsync(new SaveSupplierRequest(supplierName, null, null, null));
        var orderId = await CreateOrderAsync(owner, supplier.Id);

        await suppliers.DeactivateSupplierAsync(supplier.Id);

        var order = await owner.GetFromJsonAsync<PurchaseOrderDetailDto>($"/api/v1/purchase-orders/{orderId}", ContractJson.Options)
            ?? throw new InvalidOperationException("the order disappeared with the supplier");
        Assert.Equal(supplierName, order.Order.SupplierName);

        var newOrder = await owner.PostAsJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier.Id,
            locationId = _fixture.BusinessA.LocationId,
            orderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            expectedDate = (DateOnly?)null,
            note = "T15 new order for a retired supplier",
            lines = new[] { new { variantId = _fixture.BusinessA.VariantId, quantity = 5m, unitCost = 60m, discountAmount = 0m, taxRate = 0m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, newOrder.StatusCode);
        Assert.Contains("supplier", (await newOrder.Content.ReadAsStringAsync()).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T15_03_a_stale_save_is_refused_and_the_operators_input_survives_for_review()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customers = new CustomerApiClient(owner);
        var suppliers = new SupplierApiClient(owner);

        var marker = $"T15 race {Guid.NewGuid():N}"[..14];
        var customer = await customers.CreateCustomerAsync(new SaveCustomerRequest(marker, null, null, null));

        // Two operators read the same row. The first save wins; the second is refused rather than silently applied.
        var row = new CustomerRow(customer);
        var other = new CustomerRow(customer);

        row.Name = $"{marker} by operator one";
        other.Name = $"{marker} by operator two";
        other.Phone = "+974 5550 1234";

        var first = await customers.UpdateCustomerAsync(row.Id, row.ToRequest(), row.Dto.Version);
        Assert.Equal($"{marker} by operator one", first.Name);
        Assert.True(first.Version > customer.Version, "a saved customer must carry a new version");

        var stale = await Assert.ThrowsAsync<ApiException>(
            () => customers.UpdateCustomerAsync(other.Id, other.ToRequest(), other.Dto.Version));
        Assert.True(stale.IsConflict);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);

        // The stored record is the winner's, and the loser's typed values are still on their row: the screen keeps
        // them so the operator can re-apply, instead of discarding an edit to show a conflict.
        var stored = await customers.GetCustomersAsync(search: marker, includeInactive: true);
        var storedCustomer = Assert.Single(stored.Items);
        Assert.Equal($"{marker} by operator one", storedCustomer.Name);
        Assert.Null(storedCustomer.Phone);

        Assert.Equal($"{marker} by operator two", other.Name);
        Assert.Equal("+974 5550 1234", other.Phone);
        Assert.Equal(customer.Version, other.Dto.Version);
        other.ConflictNote = "This row changed elsewhere.";
        Assert.Equal($"{marker} by operator two", other.Name);

        // Re-applying against the version that now exists succeeds, which is the recovery the note asks for.
        var reapplied = await customers.UpdateCustomerAsync(storedCustomer.Id, other.ToRequest(), storedCustomer.Version);
        Assert.Equal($"{marker} by operator two", reapplied.Name);

        // Suppliers carry the same guard: the version that was read is the version that must be sent back.
        var supplierName = $"T15 supplier race {Guid.NewGuid():N}"[..20];
        var supplier = await suppliers.CreateSupplierAsync(new SaveSupplierRequest(supplierName, null, null, null));
        var supplierRow = new SupplierRow(supplier);
        supplierRow.Address = "Old warehouse";

        var supplierFirst = await suppliers.UpdateSupplierAsync(supplier.Id, supplierRow.ToRequest(), supplierRow.Dto.Version);
        Assert.Equal("Old warehouse", supplierFirst.Address);

        var supplierStale = await Assert.ThrowsAsync<ApiException>(
            () => suppliers.UpdateSupplierAsync(supplier.Id, new SaveSupplierRequest(supplierName, null, null, "Second edit"), supplier.Version));
        Assert.True(supplierStale.IsConflict);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, supplierStale.Code);

        // A party id from another business is not visible and not writable: the answer is the same 404 the
        // customer screen would show, with no hint that the record exists somewhere else.
        using var otherBusiness = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var foreign = new CustomerApiClient(otherBusiness);
        var foreignRead = await Assert.ThrowsAsync<ApiException>(
            () => foreign.UpdateCustomerAsync(customer.Id, new SaveCustomerRequest("hijacked", null, null, null), first.Version));
        Assert.True(foreignRead.IsNotFound);
        Assert.NotEqual(403, foreignRead.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var untouched = await db.Customers.AsNoTracking().SingleAsync(row2 => row2.Id == customer.Id);
            Assert.NotEqual("hijacked", untouched.Name);
        });
    }

    [Fact]
    public async Task T15_04_the_supplier_screen_is_offered_only_to_what_the_route_allows_and_every_host_registers_it()
    {
        var root = InvoiceEditorTests.RepositoryRoot();

        // 1. The catalogue row and the route agree: purchasing.manage is what the screen offers and what the API asks.
        var suppliersRow = ShellNavigation.Routes.Single(row => row.Href == "suppliers");
        Assert.Equal(ShellAvailability.Available, suppliersRow.Availability);
        Assert.Equal(Permissions.PurchasingManage, suppliersRow.RequiredPermission);

        using var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);
        var cashierSession = await new AccountApiClient(cashier).GetSessionAsync()
            ?? throw new InvalidOperationException("the cashier has no session");

        Assert.DoesNotContain(Permissions.PurchasingManage, cashierSession.Permissions);
        Assert.DoesNotContain(ShellNavigation.Visible(cashierSession).Select(row => row.Href), href => href == "suppliers");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/suppliers")).StatusCode);

        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ownerSession = await new AccountApiClient(owner).GetSessionAsync()
            ?? throw new InvalidOperationException("the owner has no session");
        Assert.Contains(ShellNavigation.Visible(ownerSession).Select(row => row.Href), href => href == "suppliers");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/suppliers")).StatusCode);

        // 2. Both hosts that render the party screens can resolve their clients.
        var pages = new[] { typeof(QtsBilling.Ui.Pages.Customers), typeof(QtsBilling.Ui.Pages.Suppliers) };
        var injected = pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(ICustomerApiClient).Assembly)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(ICustomerApiClient), injected);
        Assert.Contains(typeof(ISupplierApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the party screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp/QtsWebApp.Client/Program.cs", @"QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} routes the party screens but never registers: {string.Join(", ", unnamed)}");
        }

        // 3. The screen reads one bounded page at a time: the shared state asks for 25, and the server's own clamp
        //    is what protects a browser from a whole-file fetch.
        var asked = 0;
        var state = new PagedListState<CustomerDto>((search, cursor, pageSize, token) =>
        {
            asked++;
            return Task.FromResult(new PagedRows<CustomerDto>([new CustomerDto(Guid.NewGuid(), "x", null, null, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], "next"));
        }, () => { });

        await state.ReloadAsync();
        Assert.Equal(1, asked);
        Assert.True(state.CanGoNext);

        await state.NextPageAsync();
        Assert.Equal(2, asked);
        Assert.True(state.CanGoPrevious);
        await state.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> CreateIssuedInvoiceAsync(HttpClient client, Guid customerId, string _customerName)
    {
        var created = await client.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "party workbench draft",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 100m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));

        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"the draft could not be created: {(int)created.StatusCode} {body}");

        var invoiceId = JsonDocument.Parse(body).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();

        var issued = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceId}/issue", new { });
        var issuedBody = await issued.Content.ReadAsStringAsync();
        Assert.True(issued.IsSuccessStatusCode, $"the draft could not be issued: {(int)issued.StatusCode} {issuedBody}");

        return invoiceId;
    }

    private async Task<Guid> CreateOrderAsync(HttpClient client, Guid supplierId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId,
            locationId = _fixture.BusinessA.LocationId,
            orderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            expectedDate = (DateOnly?)null,
            note = "T15 purchase order",
            lines = new[] { new { variantId = _fixture.BusinessA.VariantId, quantity = 10m, unitCost = 60m, discountAmount = 0m, taxRate = 0m } },
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the order could not be created: {(int)response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("order", out var nested) && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty("id", out var nestedId))
        {
            return nestedId.GetGuid();
        }

        return root.GetProperty("id").GetGuid();
    }
}
