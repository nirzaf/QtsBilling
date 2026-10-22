using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// The purchase-order workbench (specification task 26, recipe steps 1 and 3). A draft is edited freely; placing it
/// is the separate act that commits this business to a supplier and closes its terms. Once placed, the screen only
/// reads: ordered, received, outstanding and the cost and tax snapshots the server stored, with the deliveries
/// recorded against it. Nothing here moves stock - that is the receiving screen - and nothing here types a bill.
/// </summary>
public partial class PurchaseOrderEditor
{
    [Parameter] public Guid OrderId { get; set; }

    [Inject] private IPurchasingApiClient Purchasing { get; set; } = default!;
    [Inject] private ISupplierApiClient Suppliers { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IProductApiClient Products { get; set; } = default!;
    [Inject] private IDocumentOutput Output { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private PurchaseOrderDetailDto? _detail;
    private IReadOnlyList<PurchaseReceiptDto> _receipts = [];
    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private readonly List<DraftLine> _lines = [];

    private ReferenceItem? _supplier;
    private ReferenceItem? _location;
    private ReferenceItem? _adding;
    private DateTime? _orderDate;
    private DateTime? _expected;
    private string? _note;
    private string? _cancelReason;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _printing;
    private bool _canManage;
    private bool _cancelling;
    private bool _started;
    private Guid _loadedId;
    private int _loadVersion;

    private bool IsDraft => _detail?.Order.Status == PurchaseOrderState.Draft;

    private bool IsOpen => _detail is not null
        && _detail.Order.Status is PurchaseOrderState.Ordered or PurchaseOrderState.PartiallyReceived;

    /// <summary>The branch's own calendar, so a delivery document is read on the day the branch traded.</summary>
    private InvoicePresentationDto? Presentation => _detail is { } detail
        ? PurchasingRules.PresentationOf(BranchZone(detail.Order.LocationId))
        : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // The same component instance is reused between two order addresses, so a parameter change without a new
        // render cycle must re-read: one order is never shown under another's address.
        if (_started && OrderId != _loadedId)
        {
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _state = ShellStateKind.Loading;
        _message = null;
        _detail = null;
        _error = null;

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.PurchasingManage, StringComparer.Ordinal);

            if (!_canManage)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading and placing purchase orders needs the '{Permissions.PurchasingManage}' authority, which this account does not hold in this business.";
                return;
            }

            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            var detail = await Purchasing.GetOrderAsync(OrderId).ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            Apply(detail);
            _loadedId = detail.Order.Id;

            _receipts = detail.Order.Status == PurchaseOrderState.Draft
                ? []
                : await Purchasing.ListReceiptsAsync(detail.Order.Id).ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                404 => ShellStateKind.Empty,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = PurchasingRules.RefusalSentence(exception, "this purchase order");
        }
        finally
        {
            if (version == _loadVersion)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>Restates the editable draft from the read-back order, so what is on screen is the server's own record.</summary>
    private void Apply(PurchaseOrderDetailDto detail)
    {
        _detail = detail;
        _supplier = ReferenceItem.From(detail.Order.SupplierId, detail.Order.SupplierName ?? "the supplier", null);
        _location = ReferenceItem.From(detail.Order.LocationId, BranchText(detail.Order.LocationId), null);
        _orderDate = detail.Order.OrderDate.ToDateTime(TimeOnly.MinValue);
        _expected = detail.Order.ExpectedDate?.ToDateTime(TimeOnly.MinValue);
        _note = detail.Order.Note;
        _cancelling = false;

        _lines.Clear();
        _lines.AddRange(detail.Lines.Select(line => new DraftLine(
            line.VariantId,
            line.ProductName,
            line.QuantityOrdered,
            line.UnitCost,
            line.DiscountAmount,
            line.TaxRate * 100m)));
    }

    private string BranchText(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.Name ?? "the ordered branch";

    private string? BranchZone(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.TimeZone;

    private void SupplierChanged(ReferenceItem? supplier) => _supplier = supplier;
    private void LocationChanged(ReferenceItem? location) => _location = location;

    private async Task AddLineAsync(ReferenceItem? product)
    {
        _adding = product;

        if (product is null || _lines.Any(line => line.VariantId == product.Id))
        {
            return;
        }

        try
        {
            var detail = await Products.GetProductAsync(product.Id).ConfigureAwait(false);
            var variant = detail.Variants.FirstOrDefault(row => row.IsDefault) ?? detail.Variants.FirstOrDefault();

            if (variant is null)
            {
                _error = $"{product.Name} has no catalogue variant to order. Add one on the product screen first.";
                return;
            }

            if (_lines.Count == 1 && _lines[0].VariantId == Guid.Empty)
            {
                _lines.RemoveAt(0);
            }

            _lines.Add(new DraftLine(variant.Id, variant.Name, 1m, variant.PurchasePrice ?? variant.SellingPrice, 0m, detail.Product.TaxRate * 100m));
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "the product");
        }
    }

    private void RemoveLine(DraftLine line)
    {
        if (_lines.Count > 1)
        {
            _lines.Remove(line);
        }
    }

    private Task SaveAsync() => SaveCoreAsync(place: false);

    private Task PlaceAsync() => SaveCoreAsync(place: true);

    private async Task SaveCoreAsync(bool place)
    {
        if (_supplier is null || _location is null || _busy)
        {
            _error = "A purchase order needs a supplier and a branch to deliver into.";
            return;
        }

        var invalid = _lines.FirstOrDefault(line => line.VariantId == Guid.Empty || line.Quantity <= 0m
            || line.UnitCost < 0m || line.DiscountAmount < 0m || line.TaxRate < 0m || line.TaxRate > 100m);

        if (invalid is not null)
        {
            _error = $"Check \"{invalid.Description}\": every line needs a catalogue product, a positive quantity, and a cost, discount and tax rate within their bounds.";
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;

        var request = new SavePurchaseOrderRequest(
            _supplier.Id,
            _location.Id,
            _orderDate is { } ordered ? DateOnly.FromDateTime(ordered) : null,
            _expected is { } expectedDate ? DateOnly.FromDateTime(expectedDate) : null,
            string.IsNullOrWhiteSpace(_note) ? null : _note.Trim(),
            _lines.Select(line => new PurchaseOrderLineRequest(line.VariantId, line.Quantity, line.UnitCost, line.DiscountAmount, line.TaxRate / 100m)).ToArray());

        try
        {
            var saved = _detail is null
                ? await Purchasing.CreateOrderAsync(request).ConfigureAwait(false)
                : await Purchasing.UpdateOrderAsync(_detail.Order.Id, request).ConfigureAwait(false);

            if (place)
            {
                saved = await Purchasing.ConfirmOrderAsync(saved.Order.Id).ConfigureAwait(false);
            }

            _notice = place
                ? $"{saved.Order.OrderNumber} is placed. Nothing has arrived and nothing has been posted: record the delivery when the goods do."
                : $"{saved.Order.OrderNumber} saved as a draft.";

            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            // What was typed stays on the screen so the operator can read the refusal and fix the figure, or
            // read the order back if someone else moved it first.
            _error = PurchasingRules.RefusalSentence(exception, "this purchase order");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ToggleCancel()
    {
        _cancelling = !_cancelling;
        _error = null;
    }

    private async Task CancelAsync()
    {
        if (_detail is null || _busy || string.IsNullOrWhiteSpace(_cancelReason))
        {
            return;
        }

        _busy = true;
        _error = null;

        try
        {
            var cancelled = await Purchasing.CancelOrderAsync(_detail.Order.Id, new PurchaseOrderActionRequest(_cancelReason.Trim()))
                .ConfigureAwait(false);

            _cancelling = false;
            _cancelReason = null;
            _notice = $"{cancelled.Order.OrderNumber} is cancelled for what had not arrived. Deliveries already recorded stand, with their stock and their journals.";
            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "this purchase order");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_detail is null || !IsDraft || _busy)
        {
            return;
        }

        _busy = true;
        _error = null;

        try
        {
            await Purchasing.DeleteOrderAsync(_detail.Order.Id).ConfigureAwait(false);
            Navigation.NavigateTo("purchasing/orders");
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, "this draft");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PrintAsync()
    {
        if (_detail is null || _printing)
        {
            return;
        }

        _printing = true;
        _error = null;

        try
        {
            var document = PurchaseDocument.ForOrder(_detail, BranchText(_detail.Order.LocationId), reprint: !IsDraft);
            _notice = await Output.PresentAsync(document).ConfigureAwait(false)
                ? $"The stored snapshot of {_detail.Order.OrderNumber} was handed to this host's own print dialogue. Nothing was uploaded and no copy was written by the application."
                : "This host could not open a print dialogue. Nothing was printed, written or fetched from elsewhere.";
        }
        catch (ArgumentException exception)
        {
            _error = $"The document could not be prepared: {exception.Message}";
        }
        finally
        {
            _printing = false;
        }
    }

    private async Task PrintReceiptAsync(PurchaseReceiptDto receipt)
    {
        if (_printing)
        {
            return;
        }

        _printing = true;
        _error = null;

        try
        {
            var document = PurchaseDocument.ForReceipt(receipt, BranchText(receipt.LocationId), reprint: true);
            _notice = await Output.PresentAsync(document).ConfigureAwait(false)
                ? $"{receipt.ReceiptNumber} was handed to this host's own print dialogue from its stored lines."
                : "This host could not open a print dialogue, so nothing was printed.";
        }
        catch (ArgumentException exception)
        {
            _error = $"The document could not be prepared: {exception.Message}";
        }
        finally
        {
            _printing = false;
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
            .Where(candidate => candidate.IsActive && (string.IsNullOrWhiteSpace(term) || candidate.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(candidate => ReferenceItem.From(candidate.Id, candidate.Name, candidate.Code))
            .ToList());

    private async Task<IReadOnlyList<ReferenceItem>> SearchProductsAsync(string term, CancellationToken token)
    {
        var page = await Products.GetProductsAsync(search: term, pageSize: 25, cancellationToken: token).ConfigureAwait(false);

        return page.Items.Select(product => ReferenceItem.From(product.Id, product.Name, product.Sku)).ToList();
    }

    /// <summary>A line being edited. The variant id arrives from a picker, never from a keyboard.</summary>
    private sealed class DraftLine(Guid variantId, string description, decimal quantity, decimal unitCost, decimal discountAmount, decimal taxRate)
    {
        public Guid VariantId { get; } = variantId;

        public string Description { get; set; } = description;

        public decimal Quantity { get; set; } = quantity;

        public decimal UnitCost { get; set; } = unitCost;

        public decimal DiscountAmount { get; set; } = discountAmount;

        /// <summary>Tax as a percentage (15 = 15 %), converted once at the wire boundary.</summary>
        public decimal TaxRate { get; set; } = taxRate;
    }
}
