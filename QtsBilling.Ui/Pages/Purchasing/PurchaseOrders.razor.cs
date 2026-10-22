using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// The purchase-order list (specification task 26, recipe step 1): what has been promised to suppliers and what
/// they still owe. Supplier, branch and product are chosen by name from their own screens or pickers, so nothing
/// here asks a person for an identifier (R07). Creating a draft opens the order workbench, where its lines,
/// discounts and tax are set and where the order is placed as a separate act.
/// </summary>
public partial class PurchaseOrders : IAsyncDisposable
{
    private const int PageSize = 20;

    [Inject] private IPurchasingApiClient Purchasing { get; set; } = default!;
    [Inject] private ISupplierApiClient Suppliers { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IProductApiClient Products { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly PagedListState<PurchaseOrderSummaryDto> _list;

    private string? _error;
    private bool _canRead;
    private bool _started;
    private bool _creating;
    private IReadOnlyList<BusinessLocationDto> _locations = [];

    /// <summary>The honest state of the whole screen: the shared shell component paints it (task 13, T13.03).</summary>
    private ShellStateKind State => !_started
        ? ShellStateKind.Loading
        : !_canRead
            ? ShellStateKind.Forbidden
            : _list.State;

    private string? StateBody => _error ?? _list.Message;

    private PurchaseOrderState? _status;
    private ReferenceItem? _supplierFilter;

    private ReferenceItem? _newSupplier;
    private ReferenceItem? _newLocation;
    private ReferenceItem? _newProduct;
    private Guid? _newVariantId;
    private DateTime? _expected;
    private decimal _newLineQuantity = 1m;
    private decimal _newLineCost;

    public PurchaseOrders()
        => _list = new PagedListState<PurchaseOrderSummaryDto>(LoadPageAsync, StateHasChanged, PageSize);

    /// <summary>The list row carries no line quantities, so only what the order's own state can prove is claimed here.</summary>
    private static string OutstandingValueText(PurchaseOrderSummaryDto summary) => summary.Status switch
    {
        PurchaseOrderState.Draft or PurchaseOrderState.Ordered => PurchasingRules.MoneyText(summary.TotalAmount, summary.Currency),
        PurchaseOrderState.Received or PurchaseOrderState.Cancelled => PurchasingRules.MoneyText(0m, summary.Currency),

        // A partly received order owes part of its value, and only its lines know how much. The order screen reads
        // them; the list refuses to invent a figure from a status (specification D05: no estimate dressed as a fact).
        _ => "on the order",
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);
            _canRead = session?.Permissions.Contains(Permissions.PurchasingManage, StringComparer.Ordinal) == true;

            if (_canRead)
            {
                _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);
                _newLocation = _locations.Count == 1 ? ReferenceItem.From(_locations[0].Id, _locations[0].Name, _locations[0].Code) : null;
                await _list.ReloadAsync().ConfigureAwait(false);
            }
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "the purchase-order list");
        }
        finally
        {
            StateHasChanged();
        }
    }

    private async Task<PagedRows<PurchaseOrderSummaryDto>> LoadPageAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await Purchasing.ListOrdersAsync(
            _status, _supplierFilter?.Id, null, search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<PurchaseOrderSummaryDto>(page.Items, page.NextCursor);
    }

    private Task OnSearchChanged(string term) => _list.SearchAsync(term);

    private async Task OnStatusChanged(PurchaseOrderState? status)
    {
        _status = status;
        await _list.FirstPageAsync().ConfigureAwait(false);
    }

    private async Task SupplierFilterChanged(ReferenceItem? supplier)
    {
        _supplierFilter = supplier;
        await _list.FirstPageAsync().ConfigureAwait(false);
    }

    private Task RefreshAsync() => _list.ReloadAsync();
    private Task NextAsync() => _list.NextPageAsync();
    private Task PreviousAsync() => _list.PreviousPageAsync();

    private void ToggleCreate()
    {
        _creating = !_creating;
        _error = null;
    }

    private string BranchName(Guid locationId)
        => _locations.FirstOrDefault(location => location.Id == locationId)?.Name ?? "the ordered branch";

    private string? BranchZone(Guid locationId)
        => _locations.FirstOrDefault(location => location.Id == locationId)?.TimeZone;

    private static Color StatusColor(PurchaseOrderState status) => status switch
    {
        PurchaseOrderState.Draft => Color.Default,
        PurchaseOrderState.Ordered => Color.Info,
        PurchaseOrderState.PartiallyReceived => Color.Warning,
        PurchaseOrderState.Received => Color.Success,
        _ => Color.Dark,
    };

    private void NewSupplierChanged(ReferenceItem? supplier) => _newSupplier = supplier;
    private void NewLocationChanged(ReferenceItem? location) => _newLocation = location;

    private async Task NewProductChanged(ReferenceItem? product)
    {
        _newProduct = product;
        _newVariantId = null;

        if (product is null)
        {
            return;
        }

        try
        {
            // An order line must name a variant, so the product's default is taken and shown - never a variant key.
            var detail = await Products.GetProductAsync(product.Id).ConfigureAwait(false);
            var variant = detail.Variants.FirstOrDefault(row => row.IsDefault) ?? detail.Variants.FirstOrDefault();

            if (variant is null)
            {
                _error = $"{product.Name} has no catalogue variant to order. Add one on the product screen first.";
                return;
            }

            _newVariantId = variant.Id;
            _newLineCost = variant.PurchasePrice ?? variant.SellingPrice;
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "the product");
        }
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchSuppliersAsync(string term, CancellationToken token)
    {
        var page = await Suppliers.GetSuppliersAsync(search: term, pageSize: 25, includeInactive: false, cancellationToken: token)
            .ConfigureAwait(false);

        return page.Items.Select(supplier => ReferenceItem.From(supplier.Id, supplier.Name, supplier.Phone)).ToList();
    }

    private Task<IReadOnlyList<ReferenceItem>> SearchLocationsAsync(string term, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ReferenceItem>>(_locations
            .Where(location => location.IsActive && (string.IsNullOrWhiteSpace(term) || location.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(location => ReferenceItem.From(location.Id, location.Name, location.Code))
            .ToList());

    private async Task<IReadOnlyList<ReferenceItem>> SearchProductsAsync(string term, CancellationToken token)
    {
        var page = await Products.GetProductsAsync(search: term, pageSize: 25, cancellationToken: token).ConfigureAwait(false);

        return page.Items.Select(product => ReferenceItem.From(product.Id, product.Name, product.Sku)).ToList();
    }

    private async Task CreateAsync()
    {
        if (_newSupplier is null || _newLocation is null || _newVariantId is not { } variantId)
        {
            _error = "Choose the supplier, the branch the goods go to, and a catalogue product before creating the draft.";
            return;
        }

        _error = null;

        try
        {
            var created = await Purchasing.CreateOrderAsync(new SavePurchaseOrderRequest(
                _newSupplier.Id,
                _newLocation.Id,
                // The order is dated on the branch's own trading day, not on the machine's calendar (finding F21).
                InvoiceRules.BusinessClock(BranchZone(_newLocation.Id)),
                _expected is { } expected ? DateOnly.FromDateTime(expected) : null,
                null,
                [new PurchaseOrderLineRequest(variantId, _newLineQuantity, _newLineCost, 0m, 0m)])).ConfigureAwait(false);

            Navigation.NavigateTo($"purchasing/orders/{created.Order.Id:D}");
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "the new draft");
        }
    }

    public async ValueTask DisposeAsync() => await _list.DisposeAsync();
}
