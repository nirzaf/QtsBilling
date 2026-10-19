using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sync;

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

    private ILocalPosService? PosService => Services.GetService(typeof(ILocalPosService)) as ILocalPosService;

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

    private IReadOnlyList<LocalCatalogItem> Filtered => string.IsNullOrWhiteSpace(_search)
        ? _catalog
        : [.. _catalog.Where(item =>
            item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || item.Sku.Contains(_search, StringComparison.OrdinalIgnoreCase))];

    private decimal Total => _lines.Sum(LineTotal);

    private decimal Change => (_cashReceived ?? 0m) - Total;

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
        var existing = _lines.FirstOrDefault(line => line.VariantId == item.VariantId);

        if (existing is null)
        {
            _lines.Add(new CartLine(item.VariantId, item.Name, item.Sku, item.UnitPrice, item.TaxRate, item.Revision, 1m));
        }
        else
        {
            existing.Quantity += 1m;
        }
    }

    private void RemoveLine(CartLine line) => _lines.Remove(line);

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

        if (service is null || _busy || _lines.Count == 0)
        {
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
                        line.Revision))
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

    private static decimal LineTotal(CartLine line) => (line.Quantity * line.UnitPrice) + LineTax(line);

    private static decimal LineTax(CartLine line) => line.Quantity * line.UnitPrice * line.TaxRate;

    private sealed class CartLine(
        Guid variantId,
        string name,
        string sku,
        decimal unitPrice,
        decimal taxRate,
        long revision,
        decimal quantity)
    {
        public Guid VariantId { get; } = variantId;

        public string Name { get; } = name;

        public string Sku { get; } = sku;

        public decimal UnitPrice { get; } = unitPrice;

        public decimal TaxRate { get; } = taxRate;

        public long Revision { get; } = revision;

        public decimal Quantity { get; set; } = quantity;
    }
}

