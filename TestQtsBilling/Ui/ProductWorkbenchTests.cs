using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Catalog;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The product and variant workbench (specification task 18, T18.01 - T18.03). Each case drives the typed clients and
/// the model objects the shared screens use, against the real host, so what a create panel sends, what a refusal
/// leaves on the screen, and how many rows a search may ask for at once are proven where an operator would meet
/// them. Rendered labels, focus and the prerender rule are the smoke harness's job and are checked there.
/// </summary>
public sealed class ProductWorkbenchTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T18_01_a_stock_item_and_a_service_item_made_through_the_screen_are_both_sellable_and_only_one_moves_stock()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // 1. The screen's own form objects build the requests, so what a create panel maps is what is proven.
        var stockForm = new ProductForm
        {
            Name = $"T18 case of water {suffix}",
            Sku = $"T18-STK-{suffix}",
            UnitId = _fixture.BusinessA.UnitId,
            TaxRateId = _fixture.BusinessA.TaxRateId,
            TrackStock = true,
            TaxType = CatalogRules.TaxExclusive,
        };

        var stock = await catalog.CreateProductAsync(stockForm.ToCreate("Case of 24", 120m, 60m));
        var stockVariant = stock.Variants.Single();

        var serviceForm = new ProductForm
        {
            Name = $"T18 consulting hour {suffix}",
            Sku = $"T18-SVC-{suffix}",
            UnitId = _fixture.BusinessA.UnitId,
            TaxRateId = _fixture.BusinessA.TaxRateId,
            TrackStock = false,
            TaxType = CatalogRules.TaxInclusive,
        };

        var service = await catalog.CreateProductAsync(serviceForm.ToCreate(variantName: null, sellingPrice: 230m, purchasePrice: null));
        var serviceVariant = service.Variants.Single();

        Assert.False(service.Product.TrackStock);
        Assert.True(stock.Product.TrackStock);

        // 2. Both are found by the searches the list runs: the product's own key, and a variant barcode that only
        //    exists after the editor added it - which is the counter's scan path.
        var byTerm = await catalog.GetProductsAsync(search: suffix);
        Assert.Contains(byTerm.Items, row => row.Id == stock.Product.Id);
        Assert.Contains(byTerm.Items, row => row.Id == service.Product.Id);

        var barcode = $"T18BC{suffix}";
        await catalog.CreateVariantAsync(stock.Product.Id, new CreateProductVariantRequest(
            $"T18 single bottle {suffix}", $"T18-BTL-{suffix}", barcode, 6m, 2.5m, stock.Product.Version));

        var byBarcode = await catalog.GetProductsAsync(search: barcode);
        Assert.Equal(stock.Product.Id, Assert.Single(byBarcode.Items).Id);

        // 3. Each kind is selectable on the document its own flow allows: the stocked variant on a direct-goods
        //    invoice that recognises its cost, the catalog service on a service invoice that never touches the shelf
        //    (specification task 18, T18.01).
        await RecordMovementAsync(owner, stockVariant.Id, StockMovementKind.PurchaseReceipt, 10m, 60m);

        var customerId = await CreateCustomerAsync(owner, $"T18 workbench customer {suffix}");

        var goodsInvoiceId = await DraftAndIssueAsync(
            owner,
            customerId,
            InvoiceRecognitionMode.DirectGoods,
            "T18 goods document",
            [new InvoiceLineRequest(stockVariant.Id, null, 2m, 120m, 0m, 0.15m)]);

        var serviceInvoiceId = await DraftAndIssueAsync(
            owner,
            customerId,
            InvoiceRecognitionMode.Service,
            "T18 service document",
            [new InvoiceLineRequest(serviceVariant.Id, null, 1m, 230m, 0m, 0.15m)]);

        var goodsLines = await LineRowsAsync(goodsInvoiceId);
        var serviceLines = await LineRowsAsync(serviceInvoiceId);

        // Each line carries the catalog name it was chosen by (the server snapshots the product's own name, which
        // is what the counter saw in the picker), so the document reads without a lookup into the live catalog.
        Assert.Equal(stock.Product.Name, Assert.Single(goodsLines).ProductNameSnapshot);
        Assert.Equal(stockVariant.Id, Assert.Single(goodsLines).VariantId);
        Assert.Equal(service.Product.Name, Assert.Single(serviceLines).ProductNameSnapshot);
        Assert.Equal(serviceVariant.Id, Assert.Single(serviceLines).VariantId);
        Assert.Equal(230m, Assert.Single(serviceLines).UnitPrice);

        // 4. The two flows stay different: a stocked variant is refused on a service document and a non-stocked one
        //    on a direct-goods document, and each refusal names the document it belongs to.
        var stockOnService = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId, customerId, DateOnly.FromDateTime(DateTime.UtcNow), DueDate: null,
            "T18 wrong side one", [new InvoiceLineRequest(stockVariant.Id, null, 1m, 120m, 0m, 0.15m)],
            InvoiceRecognitionMode.Service), ContractJson.Options);
        var stockOnServiceBody = await stockOnService.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, stockOnService.StatusCode);
        Assert.Contains("direct-goods document", stockOnServiceBody, StringComparison.Ordinal);

        var serviceOnGoods = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId, customerId, DateOnly.FromDateTime(DateTime.UtcNow), DueDate: null,
            "T18 wrong side two", [new InvoiceLineRequest(serviceVariant.Id, null, 1m, 230m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods), ContractJson.Options);
        var serviceOnGoodsBody = await serviceOnGoods.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, serviceOnGoods.StatusCode);
        Assert.Contains("track stock", serviceOnGoodsBody, StringComparison.OrdinalIgnoreCase);

        // Only the stock item moved: a receipt, then the issued sale. The service item has no movement and no
        // balance row anywhere, which is what "no stock side effect" has to mean on the ledger.
        var movements = await _fixture.WithFreshContextAsync(db => db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.VariantId == stockVariant.Id || movement.VariantId == serviceVariant.Id)
            .Select(movement => new { movement.VariantId, movement.MovementType, movement.QuantityDelta })
            .ToListAsync());

        Assert.Equal(2, movements.Count(movement => movement.VariantId == stockVariant.Id));
        Assert.Contains(movements, movement => movement.VariantId == stockVariant.Id
            && movement.MovementType == MovementType.Sale && movement.QuantityDelta == -2m);
        Assert.DoesNotContain(movements, movement => movement.VariantId == serviceVariant.Id);

        var balances = await _fixture.WithFreshContextAsync(db => db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.VariantId == stockVariant.Id || balance.VariantId == serviceVariant.Id)
            .Select(balance => new { balance.VariantId, balance.OnHand })
            .ToListAsync());

        Assert.Equal(1, balances.Count(balance => balance.VariantId == stockVariant.Id));
        Assert.Equal(8m, balances.Single(balance => balance.VariantId == stockVariant.Id).OnHand);
        Assert.DoesNotContain(balances, balance => balance.VariantId == serviceVariant.Id);
    }

    [Fact]
    public async Task T18_02_a_taken_barcode_and_a_lost_race_are_two_different_answers_and_neither_loses_what_was_typed()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var form = new ProductForm
        {
            Name = $"T18 refusal {suffix}",
            Sku = $"T18-REF-{suffix}",
            UnitId = _fixture.BusinessA.UnitId,
            TrackStock = false,
        };

        var created = await catalog.CreateProductAsync(form.ToCreate("Original", 40m, null));

        // 1. A barcode another variant already owns.
        var barcode = $"T18CLASH{suffix}";
        await catalog.CreateVariantAsync(created.Product.Id, new CreateProductVariantRequest(
            $"T18 scanned {suffix}", null, barcode, 41m, null, created.Product.Version));

        // The screen re-arms its buffer from the record it just showed, so this attempt carries the version that
        // is actually stored: the refusal below is about the barcode, not about a version that had moved.
        var armed = await catalog.GetProductAsync(created.Product.Id);

        var buffer = new VariantForm
        {
            Name = $"T18 duplicate scan {suffix}",
            Barcode = barcode,
            SellingPrice = 42m,
            ExpectedProductVersion = armed.Product.Version,
        };

        var clash = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateVariantAsync(created.Product.Id, buffer.ToCreate()));
        Assert.True(clash.IsConflict);
        Assert.Equal(ApiErrorCodes.DuplicateBarcode, clash.Code);

        var clashNote = CatalogRules.RefusalNote(clash);
        Assert.Contains("already holds it", clashNote, StringComparison.Ordinal);
        Assert.Contains("barcode", clashNote, StringComparison.OrdinalIgnoreCase);

        // The refusal is a correction to make, not a form to refill.
        Assert.Equal($"T18 duplicate scan {suffix}", buffer.Name);
        Assert.Equal(barcode, buffer.Barcode);

        var variants = await catalog.GetProductAsync(created.Product.Id);
        Assert.DoesNotContain(variants.Variants, variant => variant.Barcode == barcode && variant.Name == buffer.Name);

        // 2. A save that arrives after somebody else's is a different answer, and it says reload rather than change.
        var read = await catalog.GetProductAsync(created.Product.Id);

        var firstOperator = new ProductForm();
        firstOperator.Load(read.Product);
        firstOperator.Name = $"T18 renamed by the first {suffix}";

        var secondOperator = new ProductForm();
        secondOperator.Load(read.Product);
        secondOperator.Name = $"T18 renamed by the second {suffix}";

        var won = await catalog.UpdateProductAsync(created.Product.Id, firstOperator.ToUpdate());
        Assert.Equal($"T18 renamed by the first {suffix}", won.Product.Name);

        var stale = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateProductAsync(created.Product.Id, secondOperator.ToUpdate()));
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);

        secondOperator.HoldNote(CatalogRules.RefusalNote(stale));
        Assert.Contains("Somebody else saved this record first", secondOperator.Note, StringComparison.Ordinal);
        Assert.Equal($"T18 renamed by the second {suffix}", secondOperator.Name);

        // The version it still holds is the stale one, so a retry cannot be an accident: the screen must read the
        // record again and apply the same intention against what is stored now.
        Assert.Equal(read.Product.Version, secondOperator.ToUpdate().ExpectedVersion);
        Assert.NotEqual(read.Product.Version, won.Product.Version);

        var stored = await catalog.GetProductAsync(created.Product.Id);
        Assert.Equal($"T18 renamed by the first {suffix}", stored.Product.Name);

        secondOperator.Version = stored.Product.Version;
        secondOperator.Note = null;

        var applied = await catalog.UpdateProductAsync(created.Product.Id, secondOperator.ToUpdate());
        Assert.Equal($"T18 renamed by the second {suffix}", applied.Product.Name);
        Assert.True(applied.Product.Version > stored.Product.Version);
    }

    [Fact]
    public async Task T18_03_a_catalog_larger_than_a_page_walks_in_bounded_requests_and_the_barcode_key_is_the_screens_own()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var term = $"T18 bulk {suffix}";

        var made = new List<Guid>();

        for (var index = 1; index <= 12; index++)
        {
            var form = new ProductForm
            {
                Name = $"{term} item {index}",
                Sku = $"T18-BULK-{suffix}-{index}",
                UnitId = _fixture.BusinessA.UnitId,
                TrackStock = false,
            };

            var created = await catalog.CreateProductAsync(form.ToCreate(null, 10m + index, null));
            made.Add(created.Product.Id);
        }

        Assert.Equal(12, made.Distinct().Count());

        // The list state the screen ships walks the whole result: five at a time, one page per request, no row
        // twice, none missed, and the cursor terminates.
        var asked = new List<(string? Search, string? Cursor, int PageSize)>();
        var collected = new List<ProductListItem>();

        var state = new PagedListState<ProductListItem>(
            async (search, cursor, pageSize, token) =>
            {
                asked.Add((search, cursor, pageSize));
                var page = await catalog.GetProductsAsync(search, cursor, pageSize, false, token).ConfigureAwait(false);

                return new PagedRows<ProductListItem>(
                    [.. page.Items.Select(product => new ProductListItem(product, _ => null))],
                    page.NextCursor);
            },
            () => { },
            pageSize: 5);

        await state.SearchAsync(term);
        collected.AddRange(state.Rows);

        while (state.CanGoNext)
        {
            await state.NextPageAsync();
            collected.AddRange(state.Rows);
        }

        Assert.True(state.Rows.Count <= 5, $"a page returned {state.Rows.Count} rows when five were asked for");
        Assert.All(asked, request => Assert.Equal(5, request.PageSize));
        Assert.Equal(3, asked.Count);
        Assert.Equal(12, collected.Count);
        Assert.Equal(12, collected.Select(row => row.Id).Distinct().Count());
        Assert.True(SameSet(made, collected));

        // A greedy page size is the server's decision, not the screen's: it is clamped, and the cursor still says
        // there is more to read.
        var greedy = await catalog.GetProductsAsync(search: term, pageSize: 500);
        Assert.True(greedy.Items.Count >= 12 && greedy.Items.Count < 500,
            $"the server returned {greedy.Items.Count} rows for a request of 500");

        // The keyboard rule lives in the screen: Enter in the add-variant fields is the save action, and the help
        // text sends the operator to a keyboard wedge rather than a camera or a network service.
        var handler = typeof(ProductEditor).GetMethod("AddVariantKeydown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(handler is not null, "the product editor offers no keyboard handler for a scanned barcode");
        Assert.Equal(typeof(KeyboardEventArgs), handler!.GetParameters()[0].ParameterType);
        Assert.Contains("Enter", CatalogRules.BarcodeHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("http", CatalogRules.BarcodeHelp, StringComparison.Ordinal);

        await state.DisposeAsync();
    }

    [Fact]
    public void T18_04_the_catalog_pages_are_routed_shipped_screens_and_every_host_registers_what_they_inject()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        Type[] pages = [typeof(Products), typeof(ProductEditor)];

        string[] shipped = [.. pages
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template.Trim('/'))];

        Assert.Contains("catalog/products", shipped);
        Assert.Contains("catalog/products/{ProductId:guid}", shipped);

        // The catalogue offers the address, so the component must exist rather than be a disabled promise.
        var row = ShellNavigation.Routes.Single(candidate => candidate.Href == "catalog/products");
        Assert.Equal(ShellAvailability.Available, row.Availability);
        Assert.True(ShellNavigation.IsNavigable(row));
        Assert.Contains("catalog/products", shipped);

        var injected = pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(IProductApiClient).Assembly)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IProductApiClient), injected);
        Assert.Contains(typeof(IMasterDataApiClient), injected);
        Assert.Contains(typeof(IPricingApiClient), injected);
        Assert.Contains(typeof(IInventoryApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();

        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the catalog screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp/QtsWebApp.Client/Program.cs", @"QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} routes the catalog screens but never registers: {string.Join(", ", unnamed)}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool SameSet(List<Guid> made, List<ProductListItem> collected)
        => made.OrderBy(id => id).SequenceEqual(collected.Select(row => row.Id).OrderBy(id => id));

    /// <summary>One posted line as the ledger left it, in the shape the workbench's document has to show.</summary>
    private sealed record LineRow(Guid? VariantId, string ProductNameSnapshot, decimal Quantity, decimal UnitPrice);

    private async Task<IReadOnlyList<LineRow>> LineRowsAsync(Guid invoiceId)
        => await _fixture.WithFreshContextAsync(db => db.InvoiceLines.AsNoTracking()
            .Where(line => line.InvoiceId == invoiceId)
            .OrderBy(line => line.LineNo)
            .Select(line => new LineRow(line.VariantId, line.ProductNameSnapshot, line.Quantity, line.UnitPrice))
            .ToListAsync());

    /// <summary>Drafts and issues one document the way the invoice screen does it, and returns its id.</summary>
    private async Task<Guid> DraftAndIssueAsync(
        HttpClient client,
        Guid customerId,
        InvoiceRecognitionMode mode,
        string note,
        InvoiceLineRequest[] lines)
    {
        var draft = await client.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            note,
            lines,
            mode), ContractJson.Options);
        var draftBody = await draft.Content.ReadAsStringAsync();
        Assert.True(draft.StatusCode == HttpStatusCode.Created, $"the draft could not be created: {(int)draft.StatusCode} {draftBody}");

        var invoiceId = JsonDocument.Parse(draftBody).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issued = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceId}/issue", new { });
        Assert.True(issued.IsSuccessStatusCode, $"the document could not be issued: {(int)issued.StatusCode} {await issued.Content.ReadAsStringAsync()}");

        return invoiceId;
    }

    private async Task RecordMovementAsync(
        HttpClient client, Guid variantId, StockMovementKind kind, decimal quantity, decimal unitCost)
    {
        var response = await client.PostAsJsonAsync("/api/v1/inventory/movements", new RecordMovementRequest(
            _fixture.BusinessA.LocationId,
            VariantId: variantId,
            MovementType: kind,
            QuantityDelta: quantity,
            UnitCost: unitCost,
            ReferenceType: "t18_setup",
            ReferenceId: Guid.NewGuid(),
            Memo: "task 18 workbench setup"), ContractJson.Options);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the movement could not be recorded: {(int)response.StatusCode} {body}");
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers",
            new QtsBilling.Contracts.Customers.SaveCustomerRequest(name, null, null, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"the customer could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }
}
