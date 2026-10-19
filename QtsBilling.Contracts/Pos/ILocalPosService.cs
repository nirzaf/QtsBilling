namespace QtsBilling.Contracts.Pos;

/// <summary>
/// The point-of-sale capability a host provides to the shared UI. A desktop host implements it against
/// the device-local store (offline capture); the browser host reports that offline capture is not
/// available there. The shared UI never talks to a database or to the network through this contract.
/// </summary>
public interface ILocalPosService
{
    /// <summary>True when this host can capture cash sales into the local queue.</summary>
    bool IsOfflineCaptureSupported { get; }

    /// <summary>The cached, locally sellable catalog.</summary>
    Task<IReadOnlyList<LocalCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a cash sale locally. The operation is only accepted after the money rows are durably
    /// committed; a failure leaves nothing behind and must not be reported as a success.
    /// </summary>
    Task<LocalSaleCaptureResult> CaptureCashSaleAsync(
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>The locally captured sales that are still waiting for the server.</summary>
    Task<IReadOnlyList<LocalPendingSale>> GetPendingSalesAsync(CancellationToken cancellationToken = default);

    /// <summary>Queue status for the device status bar.</summary>
    Task<LocalQueueSummary> GetQueueSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the local cache from the server. Only possible while the device is online; the number of
    /// cached items is returned.
    /// </summary>
    Task<int> RefreshCatalogAsync(CancellationToken cancellationToken = default);
}
