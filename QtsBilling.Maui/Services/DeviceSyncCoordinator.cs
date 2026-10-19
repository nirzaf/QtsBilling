using System.Text.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Sqlite;
using QtsBilling.Persistence.Sqlite.Entities;

namespace QtsBilling.Maui.Services;

/// <summary>
/// The device synchronization loop (QB-22). The push sends the due local operations to
/// <c>POST /api/v1/sync/operations</c> and stores the receipt; the pull walks the change feed and applies
/// it to the device cache. Nothing is deleted on the device because of a sync: a refused operation stays in
/// the queue for the operator, and a change that collides with a pending operation becomes a conflict.
/// </summary>
public sealed class DeviceSyncCoordinator : ISyncCoordinator
{
    private readonly ISyncApiClient _api;
    private readonly SqliteLocalStore _store;
    private readonly IAccountApiClient _accounts;
    private readonly IServerSettings _settings;
    private readonly IDeviceIdentity _identity;
    private readonly IDeviceConnectivity _connectivity;

    public DeviceSyncCoordinator(
        ISyncApiClient api,
        SqliteLocalStore store,
        IAccountApiClient accounts,
        IServerSettings settings,
        IDeviceIdentity identity,
        IDeviceConnectivity connectivity)
    {
        _api = api;
        _store = store;
        _accounts = accounts;
        _settings = settings;
        _identity = identity;
        _connectivity = connectivity;
    }

    public bool IsSupported => true;

    public async Task<SyncRunReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var push = await PushAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(push.Error))
        {
            return push;
        }

        var pull = await PullAsync(cancellationToken).ConfigureAwait(false);

        return new SyncRunReport(push.Pushed, pull.Pulled, push.Conflicts + pull.Conflicts, push.Failed + pull.Failed, pull.Error ?? push.Error);
    }

    public async Task<SyncRunReport> PushAsync(CancellationToken cancellationToken = default)
    {
        var pushed = 0;
        var failed = 0;
        var conflicts = 0;

        try
        {
            var due = await _store.GetDueOutboxAsync(DateTime.UtcNow, 50, cancellationToken).ConfigureAwait(false);

            foreach (var operation in due)
            {
                var envelope = BuildEnvelope(operation, out var payloadError);

                if (envelope is null)
                {
                    await _store.ApplyReceiptAsync(
                        new InboxReceipt
                        {
                            OperationId = operation.OperationId,
                            ServerResult = "Rejected",
                            ErrorCode = "INVALID_PAYLOAD",
                            AppliedAtUtc = DateTime.UtcNow,
                        },
                        cancellationToken).ConfigureAwait(false);

                    failed++;
                    continue;
                }

                var receipt = await _api.PushAsync(envelope, cancellationToken).ConfigureAwait(false);

                await _store.ApplyReceiptAsync(ToReceipt(receipt), cancellationToken).ConfigureAwait(false);

                if (receipt.Outcome is SyncOutcome.Posted or SyncOutcome.AlreadyPosted)
                {
                    pushed++;
                }
                else if (receipt.Outcome is SyncOutcome.NeedsReview or SyncOutcome.SameKeyDifferentPayload
                    or SyncOutcome.NotAuthorized or SyncOutcome.DeviceRevoked
                    or SyncOutcome.UnsupportedPayloadVersion)
                {
                    conflicts++;
                }
                else
                {
                    failed++;
                }
            }
        }
        catch (ApiException exception)
        {
            // The server refused the batch: the operations stay in the queue.
            return new SyncRunReport(pushed, 0, conflicts, failed, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SyncRunReport(pushed, 0, conflicts, failed, "The server is unreachable.");
        }

        return new SyncRunReport(pushed, 0, conflicts, failed, null);
    }

    public async Task<SyncRunReport> PullAsync(CancellationToken cancellationToken = default)
    {
        var pulled = 0;
        var conflicts = 0;

        try
        {
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);

            if (scope is null)
            {
                return new SyncRunReport(0, 0, 0, 0, "The device is not enrolled in a business yet.");
            }

            var (businessId, locationId) = scope.Value;
            var (revision, ordinal) = await _store.GetSyncCursorAsync(businessId, locationId, cancellationToken).ConfigureAwait(false);

            var page = await _api.GetChangesAsync(revision, (int)ordinal, 200, cancellationToken).ConfigureAwait(false);

            if (page.ResyncRequired)
            {
                // The cursor cannot be explained by the server: drop the cache and pull the baseline again.
                await _store.RebuildCacheAsync(cancellationToken).ConfigureAwait(false);
                await _store.SetSyncCursorAsync(businessId, locationId, 0, 0, cancellationToken).ConfigureAwait(false);

                page = await _api.GetChangesAsync(0, 0, 200, cancellationToken).ConfigureAwait(false);
            }

            var applied = await _store
                .ApplyChangesAsync(page.Changes, businessId, locationId, cancellationToken)
                .ConfigureAwait(false);

            pulled = applied.Applied;
            conflicts = applied.Conflicts;

            await _store.SetSyncCursorAsync(businessId, locationId, page.NextRevision, page.NextOrdinal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            return new SyncRunReport(0, pulled, conflicts, 0, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SyncRunReport(0, pulled, conflicts, 0, "The server is unreachable.");
        }

        return new SyncRunReport(0, pulled, conflicts, 0, null);
    }

    public async Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var summary = await _store.GetPendingSummaryAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = await _store.GetConflictsAsync(cancellationToken).ConfigureAwait(false);

        var revision = 0L;
        var ordinal = 0L;
        DateTimeOffset? lastSuccess = null;

        var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);

        if (scope is not null)
        {
            var (businessId, locationId) = scope.Value;
            (revision, ordinal) = await _store.GetSyncCursorAsync(businessId, locationId, cancellationToken).ConfigureAwait(false);
            lastSuccess = await _store.GetLastSyncSuccessAsync(businessId, locationId, cancellationToken).ConfigureAwait(false);
        }

        return new SyncStatus(
            summary.PendingOperations,
            conflicts.Count,
            revision,
            (int)ordinal,
            lastSuccess,
            _connectivity.IsOnline,
            null);
    }

    private async Task<(Guid BusinessId, Guid LocationId)?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var session = await _accounts.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session?.BusinessId is not { } businessId)
        {
            return null;
        }

        if (_settings.LocationId is not { } locationId || locationId == Guid.Empty)
        {
            return null;
        }

        return (businessId, locationId);
    }

    private SyncOperationEnvelope? BuildEnvelope(OutboxOperation operation, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(operation.CanonicalPayload);
            var payload = document.RootElement.Clone();

            var businessId = payload.TryGetProperty("businessId", out var business) && business.TryGetGuid(out var businessValue)
                ? businessValue
                : Guid.Empty;

            var locationId = payload.TryGetProperty("locationId", out var location) && location.TryGetGuid(out var locationValue)
                ? locationValue
                : Guid.Empty;

            var type = payload.TryGetProperty("type", out var kind) ? kind.GetString() ?? string.Empty : string.Empty;
            var version = payload.TryGetProperty("version", out var payloadVersion) && payloadVersion.TryGetInt32(out var versionValue)
                ? versionValue
                : 1;

            if (businessId == Guid.Empty || locationId == Guid.Empty || type.Length == 0)
            {
                error = "The stored payload has no business, location or type.";
                return null;
            }

            return new SyncOperationEnvelope(
                _identity.DeviceId,
                operation.OperationId,
                businessId,
                locationId,
                type,
                version,
                operation.CreatedAtUtc,
                operation.Sha256Fingerprint,
                payload);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static InboxReceipt ToReceipt(SyncOperationReceipt receipt) => new()
    {
        OperationId = receipt.OperationId,
        ServerResult = receipt.Outcome switch
        {
            SyncOutcome.Posted => "Posted",
            SyncOutcome.AlreadyPosted => "AlreadyPosted",
            SyncOutcome.TransientRetry => "TransientRetry",
            _ => "Rejected",
        },
        ErrorCode = receipt.ErrorCode,
        ServerSaleId = receipt.ServerSaleId,
        PostedNumber = receipt.PostedReference,
        ServerRevision = receipt.ServerRevision,
        AppliedAtUtc = receipt.ProcessedAtUtc.UtcDateTime,
    };
}
