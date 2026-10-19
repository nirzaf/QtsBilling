namespace QtsBilling.Contracts.Sync;

/// <summary>How a device conflict is resolved by the operator.</summary>
public enum SyncConflictResolution
{
    /// <summary>Take the server value and drop the local one.</summary>
    UseServer = 0,

    /// <summary>Keep the locally captured operation and let the server deal with it.</summary>
    KeepLocal = 1,
}

/// <summary>
/// A change the server published that touches a record a pending local operation also touches. The device
/// never resolves it silently: the operator decides (QB-22).
/// </summary>
public sealed record SyncConflict(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Reason,
    string ServerValue,
    string LocalValue,
    DateTimeOffset DetectedAtUtc);

/// <summary>The device sync status shown to the operator.</summary>
public sealed record SyncStatus(
    int PendingOperations,
    int Conflicts,
    long LastRevision,
    int LastOrdinal,
    DateTimeOffset? LastSuccessUtc,
    bool Online,
    string? LastError);

/// <summary>The result of one push or pull run.</summary>
public sealed record SyncRunReport(
    int Pushed,
    int Pulled,
    int Conflicts,
    int Failed,
    string? Error);
