namespace QtsBilling.Contracts.Sync;

/// <summary>
/// The device-side synchronization loop (QB-22). The coordinator pushes the local outbox to the server and
/// pulls the change feed into the local cache. It is implemented by the desktop host; the browser host has
/// no local queue and therefore no coordinator.
/// </summary>
public interface ISyncCoordinator
{
    /// <summary>True when this host keeps a local queue and therefore synchronizes.</summary>
    bool IsSupported { get; }

    /// <summary>Pushes the due local operations and pulls the change feed.</summary>
    Task<SyncRunReport> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Pushes the due local operations only.</summary>
    Task<SyncRunReport> PushAsync(CancellationToken cancellationToken = default);

    /// <summary>Pulls the change feed only.</summary>
    Task<SyncRunReport> PullAsync(CancellationToken cancellationToken = default);

    /// <summary>The current status of the local queue and the cursor.</summary>
    Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>The conflict queue of the device (QB-22).</summary>
public interface ISyncConflictService
{
    Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default);

    Task ResolveConflictAsync(
        Guid conflictId,
        SyncConflictResolution resolution,
        CancellationToken cancellationToken = default);
}
