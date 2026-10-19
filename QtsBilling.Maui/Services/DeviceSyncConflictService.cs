using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Sqlite;

namespace QtsBilling.Maui.Services;

/// <summary>
/// The conflict queue of the device (QB-22). A conflict is a server change that touches a record a pending
/// local operation also uses. The operator resolves it; the device never decides silently.
/// </summary>
public sealed class DeviceSyncConflictService : ISyncConflictService
{
    private readonly SqliteLocalStore _store;

    public DeviceSyncConflictService(SqliteLocalStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _store.GetConflictsAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new SyncConflict(
                row.Id,
                row.EntityType,
                row.EntityId,
                row.Reason,
                row.ServerValue ?? string.Empty,
                row.LocalValue ?? string.Empty,
                row.DetectedAtUtc))
        ];
    }

    public async Task ResolveConflictAsync(
        Guid conflictId,
        SyncConflictResolution resolution,
        CancellationToken cancellationToken = default)
        => await _store.ResolveConflictAsync(conflictId, resolution, cancellationToken).ConfigureAwait(false);
}
