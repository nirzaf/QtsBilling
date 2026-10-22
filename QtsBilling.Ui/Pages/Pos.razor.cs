using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Sync;
using QtsModels.ValueObjects;

namespace QtsBilling.Ui.Pages;

/// <summary>
/// The device-local point of sale (QB-19). It captures cash sales into the local queue through the
/// <see cref="ILocalPosService"/> of the host, so this component never touches a database or the network.
/// The checkout keeps one operation id until it is accepted, so a double click or a retry can never
/// create two operations.
/// </summary>
public partial class Pos
{
    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private ILocalPosService? PosService => Services.GetService(typeof(ILocalPosService)) as ILocalPosService;

    private IPosCheckoutOrchestrator? OnlineCheckout => Services.GetService(typeof(IPosCheckoutOrchestrator)) as IPosCheckoutOrchestrator;

    private ISyncCoordinator? Sync => Services.GetService(typeof(ISyncCoordinator)) as ISyncCoordinator;

    private ISyncConflictService? ConflictService => Services.GetService(typeof(ISyncConflictService)) as ISyncConflictService;

    private readonly List<CartLine> _lines = [];
    private List<LocalCatalogItem> _catalog = [];
    private List<LocalPendingSale> _pending = [];
    private Dictionary<Guid, string> _names = [];
    private LocalPendingSale? _receipt;
    private string _search = string.Empty;
    private string? _error;
    private decimal? _cashReceived;
    private Guid? _operationId;
    private bool _busy;
    private bool _syncing;
    private SyncStatus? _status;
    private List<SyncConflict> _conflicts = [];
    private List<BusinessLocationDto> _locations = [];
    private List<CashRegisterDto> _registers = [];
    private Guid? _onlineLocationId;
    private Guid? _onlineRegisterId;
    private bool _loadingContext;
    private bool _catalogLoading;
    private decimal _openingFloat;

    /// <summary>
    /// One key per drawer-open intention (specification D06, task 19). It is held while the intention stands, so a
    /// retry after a lost answer re-applies the same command instead of opening a second drawer; choosing another
    /// register is a different intention and takes a new key.
    /// </summary>
    private string _drawerOpenKey = Guid.NewGuid().ToString("N");

    private Guid? _drawerOpenRegisterId;
    private string? _catalogCursor;
    private LocalCashSaleDraft? _onlineAttempt;
    private Guid? _onlineAttemptLocationId;
    private Guid? _onlineAttemptRegisterId;
    private bool _onlineAttemptRejected;
    private SaleCaptureResult? _postedReceipt;

    private IReadOnlyList<LocalCatalogItem> Filtered => string.IsNullOrWhiteSpace(_search)
        ? _catalog
        : [.. _catalog.Where(item =>
            item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || item.Sku.Contains(_search, StringComparison.OrdinalIgnoreCase))];

    private decimal Total => _lines.Sum(LineTotal);

    private decimal Change => (_cashReceived ?? 0m) - Total;

    private IReadOnlyList<CashRegisterDto> RegistersForSelectedLocation => _registers
        .Where(register => register.LocationId == _onlineLocationId)
        .OrderBy(register => register.Name)
        .ToArray();

    private CashRegisterDto? SelectedOnlineRegister => _registers.FirstOrDefault(register => register.Id == _onlineRegisterId);

    private bool CanPostOnline => SelectedOnlineRegister?.CurrentSession is { Status: CashRegisterSessionState.Open };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var service = PosService;

        if (service is null)
        {
            await LoadOnlineContextAsync();
            return;
        }

        try
        {
            _catalog = [.. await service.GetCatalogAsync()];
            _names = _catalog.ToDictionary(item => item.VariantId, item => item.Name);
            _pending = [.. await service.GetPendingSalesAsync()];
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            StateHasChanged();
        }

        await LoadSyncStateAsync();
    }

    private async Task LoadOnlineContextAsync()
    {
        var checkout = OnlineCheckout;
        if (checkout is null || _loadingContext)
        {
            return;
        }

        _loadingContext = true;
        _error = null;
        try
        {
            var context = await checkout.GetContextAsync();
            _locations = [.. context.Locations];
            _registers = [.. context.Registers];

            if (_onlineLocationId is null || !_locations.Any(location => location.Id == _onlineLocationId.Value))
            {
                var preferred = _registers.FirstOrDefault(register =>
                    register.CurrentSession is { Status: CashRegisterSessionState.Open }
                    && _locations.Any(location => location.Id == register.LocationId));
                _onlineLocationId = preferred?.LocationId ?? _locations.FirstOrDefault()?.Id;
            }

            if (_onlineRegisterId is null || !RegistersForSelectedLocation.Any(register => register.Id == _onlineRegisterId.Value
                && register.CurrentSession is { Status: CashRegisterSessionState.Open }))
            {
                _onlineRegisterId = RegistersForSelectedLocation
                    .FirstOrDefault(register => register.CurrentSession is { Status: CashRegisterSessionState.Open })?.Id
                    ?? RegistersForSelectedLocation.FirstOrDefault()?.Id;
            }

            if (_onlineLocationId is { } locationId)
            {
                await LoadOnlineCatalogPageAsync(locationId, reset: true);
            }
            else
            {
                _catalog = [];
                _error = "This account has no active business location available for point of sale.";
            }
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (Exception exception)
        {
            _error = $"Unable to load the online point of sale: {exception.Message}";
        }
        finally
        {
            _loadingContext = false;
            StateHasChanged();
        }
    }

    private async Task OnOnlineLocationChanged(Guid? locationId)
    {
        if (_onlineAttempt is not null || _busy || locationId == _onlineLocationId)
        {
            return;
        }

        if (_lines.Count > 0)
        {
            _lines.Clear();
            _cashReceived = null;
            Snackbar.Add("The current sale was cleared because the location changed.", Severity.Info);
        }

        _onlineLocationId = locationId;
        _onlineRegisterId = RegistersForSelectedLocation
            .FirstOrDefault(register => register.CurrentSession is { Status: CashRegisterSessionState.Open })?.Id
            ?? RegistersForSelectedLocation.FirstOrDefault()?.Id;
        _postedReceipt = null;
        _search = string.Empty;
        if (locationId is { } selectedLocationId)
        {
            await LoadOnlineCatalogPageAsync(selectedLocationId, reset: true);
        }
        else
        {
            _catalog = [];
        }
    }

    private Task OnOnlineRegisterChanged(Guid? registerId)
    {
        if (_onlineAttempt is null && !_busy)
        {
            _onlineRegisterId = registerId;
        }

        return Task.CompletedTask;
    }

    private async Task OpenDrawerAsync(Guid registerId)
    {
        var checkout = OnlineCheckout;
        if (checkout is null || _busy || _onlineAttempt is not null || _openingFloat < 0m)
        {
            return;
        }

        _busy = true;
        _error = null;

        if (_drawerOpenRegisterId != registerId)
        {
            _drawerOpenRegisterId = registerId;
            _drawerOpenKey = Guid.NewGuid().ToString("N");
        }

        try
        {
            await checkout.OpenCashDrawerAsync(registerId, _openingFloat, _drawerOpenKey);
            _openingFloat = 0m;

            // The next drawer is a new intention, so it must not replay this receipt.
            _drawerOpenKey = Guid.NewGuid().ToString("N");
            Snackbar.Add("Cash drawer opened.", Severity.Success);
            await LoadOnlineContextAsync();
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (Exception exception)
        {
            _error = $"Unable to open the cash drawer: {exception.Message}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task SearchOnlineCatalogAsync()
    {
        if (_onlineLocationId is { } locationId && _onlineAttempt is null)
        {
            await LoadOnlineCatalogPageAsync(locationId, reset: true);
        }
    }

    private async Task RefreshOnlineCatalogAsync()
    {
        if (_onlineAttempt is not null)
        {
            return;
        }

        await LoadOnlineContextAsync();
    }

    private async Task LoadMoreOnlineCatalogAsync()
    {
        if (_onlineLocationId is { } locationId && _catalogCursor is not null && _onlineAttempt is null)
        {
            await LoadOnlineCatalogPageAsync(locationId, reset: false);
        }
    }

    private async Task LoadOnlineCatalogPageAsync(Guid locationId, bool reset)
    {
        var checkout = OnlineCheckout;
        if (checkout is null || _catalogLoading)
        {
            return;
        }

        _catalogLoading = true;
        _error = null;
        if (reset)
        {
            _catalog = [];
            _catalogCursor = null;
        }

        try
        {
            var page = await checkout.GetCatalogAsync(locationId, _search, reset ? null : _catalogCursor);
            _catalog.AddRange(page.Items);
            _catalogCursor = page.NextCursor;
            _names = _catalog.ToDictionary(item => item.VariantId, item => item.Name);
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (Exception exception)
        {
            _error = $"Unable to load products: {exception.Message}";
        }
        finally
        {
            _catalogLoading = false;
            StateHasChanged();
        }
    }

    private async Task LoadSyncStateAsync()
    {
        var coordinator = Sync;

        if (coordinator is null)
        {
            return;
        }

        try
        {
            _status = await coordinator.GetStatusAsync();
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (Exception exception)
        {
            // The status is a convenience: it must never break the point of sale.
            _error = exception.Message;
        }

        var conflicts = ConflictService;

        if (conflicts is not null)
        {
            try
            {
                _conflicts = [.. await conflicts.GetConflictsAsync()];
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
        }

        // The status arrives after the first render, so the card needs its own render.
        StateHasChanged();
    }

    private async Task SyncNowAsync()
    {
        var coordinator = Sync;

        if (coordinator is null || _syncing)
        {
            return;
        }

        _syncing = true;
        _error = null;

        try
        {
            var report = await coordinator.RunAsync();

            if (!string.IsNullOrWhiteSpace(report.Error))
            {
                _error = report.Error;
            }
            else
            {
                Snackbar.Add(
                    $"Synchronized: {report.Pushed} sent, {report.Pulled} changes, {report.Conflicts} conflict(s).",
                    Severity.Success);
            }
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _syncing = false;
            await LoadAsync();
            StateHasChanged();
        }
    }

    private async Task ResolveConflictAsync(SyncConflict conflict, SyncConflictResolution resolution)
    {
        var conflicts = ConflictService;

        if (conflicts is null || _busy)
        {
            return;
        }

        _busy = true;

        try
        {
            await conflicts.ResolveConflictAsync(conflict.Id, resolution);
            await LoadSyncStateAsync();
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void AddLine(LocalCatalogItem item)
    {
        _postedReceipt = null;
        var existing = _lines.FirstOrDefault(line => line.VariantId == item.VariantId);

        if (existing is null)
        {
            _lines.Add(new CartLine(item.VariantId, item.Name, item.Sku, item.UnitPrice, item.TaxRate, item.Revision, 1m, item.TaxMode, item.PricingPolicyVersion));
        }
        else
        {
            existing.Quantity += 1m;
        }
    }

    private void RemoveLine(CartLine line)
    {
        _postedReceipt = null;
        _lines.Remove(line);
    }

    private async Task FilterAsync() => StateHasChanged();

    private async Task RefreshCatalogAsync()
    {
        var service = PosService;

        if (service is null)
        {
            return;
        }

        _busy = true;
        _error = null;

        try
        {
            var count = await service.RefreshCatalogAsync();
            Snackbar.Add($"Catalog refreshed: {count} item(s) cached.", Severity.Success);
            await LoadAsync();
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task CaptureAsync()
    {
        var service = PosService;

        if (_busy || _lines.Count == 0)
        {
            return;
        }

        if (service is null)
        {
            await CaptureOnlineAsync();
            return;
        }

        _busy = true;
        _error = null;

        try
        {
            // One operation id per checkout: it is only replaced after the capture was accepted.
            _operationId ??= Guid.CreateVersion7();

            var total = Total;
            var draft = new LocalCashSaleDraft(
                _operationId.Value,
                _cashReceived ?? total,
                [
                    .. _lines.Select((line, ordinal) => new LocalSaleLineDraft(
                        ordinal,
                        line.VariantId,
                        line.Quantity,
                        line.UnitPrice,
                        line.TaxRate,
                        LineTax(line),
                        0m,
                        line.Revision)
                    {
                        TaxMode = line.TaxMode,
                        PricingPolicyVersion = line.PricingPolicyVersion,
                    })
                ]);

            var result = await service.CaptureCashSaleAsync(draft);

            if (!result.Accepted)
            {
                _error = result.ErrorMessage ?? "The sale could not be captured.";
                return;
            }

            _operationId = null;
            _lines.Clear();
            _cashReceived = null;
            _pending = [.. await service.GetPendingSalesAsync()];
            _receipt = _pending.FirstOrDefault(sale => sale.OperationId == result.OperationId);
            Snackbar.Add($"Captured locally as {result.ProvisionalReference}.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task CaptureOnlineAsync()
    {
        var checkout = OnlineCheckout;
        if (checkout is null)
        {
            _error = "The online point-of-sale service is unavailable.";
            return;
        }

        if (_onlineAttempt is null)
        {
            if (_onlineLocationId is not { } locationId || _onlineRegisterId is not { } registerId)
            {
                _error = "Choose a location and an open cash register before posting the sale.";
                return;
            }

            if ((_cashReceived ?? Total) < Total)
            {
                _error = "Cash received must cover the sale total.";
                return;
            }

            _operationId ??= Guid.CreateVersion7();
            _onlineAttempt = new LocalCashSaleDraft(
                _operationId.Value,
                _cashReceived ?? Total,
                [.. _lines.Select((line, ordinal) => new LocalSaleLineDraft(
                    ordinal,
                    line.VariantId,
                    line.Quantity,
                    line.UnitPrice,
                    line.TaxRate,
                    LineTax(line),
                    0m,
                    line.Revision)
                {
                    TaxMode = line.TaxMode,
                    PricingPolicyVersion = line.PricingPolicyVersion,
                })]);
            _onlineAttemptLocationId = locationId;
            _onlineAttemptRegisterId = registerId;
            _onlineAttemptRejected = false;
        }

        _busy = true;
        _error = null;
        try
        {
            var result = await checkout.CaptureCashSaleAsync(
                _onlineAttemptLocationId!.Value,
                _onlineAttemptRegisterId!.Value,
                _onlineAttempt);

            if (result.Provisional)
            {
                _error = "The server returned a provisional result for an online sale. The sale remains locked so it can be safely retried.";
                return;
            }

            _postedReceipt = result;
            _onlineAttempt = null;
            _onlineAttemptLocationId = null;
            _onlineAttemptRegisterId = null;
            _onlineAttemptRejected = false;
            _operationId = null;
            _lines.Clear();
            _cashReceived = null;
            Snackbar.Add($"Sale {result.SaleNumber} posted.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
            _onlineAttemptRejected = exception.Code is ApiErrorCodes.InvalidPrice
                or ApiErrorCodes.InsufficientStock
                or ApiErrorCodes.CashRegisterNotOpen
                or ApiErrorCodes.ValidationFailed;
        }
        catch (Exception exception)
        {
            _error = $"The sale result could not be confirmed: {exception.Message} Retry to check the same sale safely.";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task StartFreshAfterRejectionAsync()
    {
        if (!_onlineAttemptRejected || _busy)
        {
            return;
        }

        _onlineAttempt = null;
        _onlineAttemptLocationId = null;
        _onlineAttemptRegisterId = null;
        _onlineAttemptRejected = false;
        _operationId = null;
        _lines.Clear();
        _cashReceived = null;
        Snackbar.Add("The rejected sale was cleared. Refreshing current prices and drawer status.", Severity.Info);
        await LoadOnlineContextAsync();
    }

    private void ClearPostedReceipt() => _postedReceipt = null;

    private async Task OpenPostedReceiptAsync()
    {
        if (_postedReceipt is null)
        {
            return;
        }

        var receiptUrl = $"/api/v1/pos/sales/{_postedReceipt.SaleId:D}/receipt?format=html";
        try
        {
            await JSRuntime.InvokeVoidAsync("open", receiptUrl, "_blank", "noopener,noreferrer");
        }
        catch (JSException exception)
        {
            _error = $"The receipt could not be opened: {exception.Message}";
        }
    }

    /// <summary>
    /// The line amount, computed with the shared commercial calculation: the device and the server use
    /// the same function, so an offline amount and the posted amount agree (specification D03/F12).
    /// </summary>
    private static decimal LineTotal(CartLine line) => LineAmounts(line).PayableAmount;

    /// <summary>The tax of the line, with the same shared calculation the server uses.</summary>
    private static decimal LineTax(CartLine line) => LineAmounts(line).TaxAmount;

    private static CommercialLineAmounts LineAmounts(CartLine line)
    {
        try
        {
            return CommercialAmountCalculator.ComputeLine(
                line.Quantity,
                line.UnitPrice,
                line.TaxMode,
                line.TaxRate,
                0m,
                field: "line");
        }
        catch (CommercialAmountException)
        {
            // A line that cannot be computed exactly shows no tax: the server rejects the capture and
            // the cashier sees the error, rather than the device inventing an amount.
            return new CommercialLineAmounts(0m, 0m, 0m, 0m, 0m, line.TaxRate, line.TaxMode, null);
        }
    }

    private sealed class CartLine(
        Guid variantId,
        string name,
        string sku,
        decimal unitPrice,
        decimal taxRate,
        long revision,
        decimal quantity,
        CommercialTaxMode taxMode,
        string? pricingPolicyVersion)
    {
        public Guid VariantId { get; } = variantId;

        public string Name { get; } = name;

        public string Sku { get; } = sku;

        public decimal UnitPrice { get; } = unitPrice;

        public decimal TaxRate { get; } = taxRate;

        public long Revision { get; } = revision;

        public decimal Quantity { get; set; } = quantity;

        /// <summary>How the tax is composed into the price (the cached catalog policy).</summary>
        public CommercialTaxMode TaxMode { get; } = taxMode;

        public string? PricingPolicyVersion { get; } = pricingPolicyVersion;
    }
}

