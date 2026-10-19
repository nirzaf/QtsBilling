using QtsBilling.Contracts.Pos;

namespace QtsWebApp.Features.Pos;

/// <summary>
/// The browser host does not capture sales offline: the device-local queue, the provisional receipt and
/// the offline permission belong to the desktop client (QB-19). The shared point-of-sale component
/// asks this service, so the browser shows an honest "not available here" state instead of pretending
/// to capture a sale it cannot store.
/// </summary>
public sealed class WebLocalPosService : ILocalPosService
{
    public bool IsOfflineCaptureSupported => false;

    public Task<IReadOnlyList<LocalCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LocalCatalogItem>>([]);

    public Task<LocalSaleCaptureResult> CaptureCashSaleAsync(
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new LocalSaleCaptureResult(
            false,
            draft.OperationId,
            null,
            0m,
            LocalCaptureCodes.NotSupported,
            "Offline cash capture is only available in the desktop client. Post the sale to the server instead."));

    public Task<IReadOnlyList<LocalPendingSale>> GetPendingSalesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LocalPendingSale>>([]);

    public Task<LocalQueueSummary> GetQueueSummaryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new LocalQueueSummary(0, 0, 0m));

    public Task<int> RefreshCatalogAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
