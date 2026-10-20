using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pos;
using QtsBilling.Persistence.Sqlite.Pos;

namespace QtsBilling.Maui.Services;

/// <summary>
/// The device point of sale. It enforces the offline permission (the signed-in account must hold
/// <c>sales.post</c> and the device must be enrolled to a location and register) and then delegates to
/// the local store, which commits the money rows atomically. A capture that is not durably stored is
/// never reported as accepted.
/// </summary>
public sealed class DeviceLocalPosService : ILocalPosService
{
    private readonly LocalPosStore _pos;
    private readonly DeviceCatalogSync _catalog;
    private readonly IAccountApiClient _accounts;
    private readonly IDeviceIdentity _identity;
    private readonly IServerSettings _settings;
    private readonly OfflineGrantService _grants;

    public DeviceLocalPosService(
        LocalPosStore pos,
        DeviceCatalogSync catalog,
        IAccountApiClient accounts,
        IDeviceIdentity identity,
        IServerSettings settings,
        OfflineGrantService grants)
    {
        _pos = pos;
        _catalog = catalog;
        _accounts = accounts;
        _identity = identity;
        _settings = settings;
        _grants = grants;
    }

    public bool IsOfflineCaptureSupported => true;

    public Task<IReadOnlyList<LocalCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
        => _pos.GetCatalogAsync(cancellationToken);

    public Task<IReadOnlyList<LocalPendingSale>> GetPendingSalesAsync(CancellationToken cancellationToken = default)
        => _pos.GetPendingSalesAsync(cancellationToken);

    public Task<LocalQueueSummary> GetQueueSummaryAsync(CancellationToken cancellationToken = default)
        => _pos.GetQueueSummaryAsync(cancellationToken);

    public async Task<LocalSaleCaptureResult> CaptureCashSaleAsync(
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var session = await _accounts.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return Rejected(draft, "This device is not signed in.");
        }

        if (session.BusinessId is not { } businessId)
        {
            return Rejected(draft, "This account is not assigned to a business.");
        }

        if (_settings.LocationId is not { } locationId || locationId == Guid.Empty)
        {
            return Rejected(draft, "This device is not enrolled to a location yet. Download the catalog while online.");
        }

        if (_settings.RegisterId is not { } registerId || registerId == Guid.Empty)
        {
            return Rejected(draft, "This device is not enrolled to a cash register yet. Download the catalog while online.");
        }

        _settings.LastBusinessId = businessId;
        _settings.LastLocationId = locationId;

        // The cached session summary is not authority. Only a server-signed, unexpired grant that names
        // this user, device, business and location permits the capture (task 23, finding F10).
        var grant = await _grants
            .AuthorizeCaptureAsync(session, businessId, locationId, cancellationToken)
            .ConfigureAwait(false);

        if (!grant.IsAllowed)
        {
            return Rejected(
                draft,
                grant.Reason == "no_grant" || grant.Reason == "grant_unavailable"
                    ? "This device has no valid offline capture grant. Sign in while online to renew it."
                    : "The offline capture grant for this device is not valid. Sign in while online to renew it.");
        }

        if (grant.Claims?.RegisterSessionId is not { } registerSessionId || registerSessionId == Guid.Empty)
        {
            return Rejected(draft, "This device has no offline grant bound to an active cash drawer session.");
        }

        var context = new LocalDeviceContext(
            _identity.DeviceId,
            businessId,
            locationId,
            session.UserId,
            registerId,
            registerSessionId);

        return await _pos.CaptureCashSaleAsync(context, draft, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var session = await _accounts.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session?.BusinessId is not { } businessId)
        {
            throw new ApiException(
                403,
                ApiErrorCodes.Forbidden,
                "The catalog can only be refreshed for an enrolled account.");
        }

        var count = await _catalog.RefreshAsync(businessId, cancellationToken).ConfigureAwait(false);
        _settings.LastBusinessId = businessId;
        return count;
    }

    private static LocalSaleCaptureResult Rejected(LocalCashSaleDraft draft, string message)
        => new(false, draft.OperationId, null, 0m, LocalCaptureCodes.Rejected, message);
}
