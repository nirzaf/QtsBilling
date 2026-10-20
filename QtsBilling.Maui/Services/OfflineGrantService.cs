using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Maui.Services;

/// <summary>
/// The device half of the offline grant (specification task 23): it obtains a grant only from the real
/// server, pins the verification key at first issue and validates the grant before every capture. The
/// cached session summary is never permission to capture anything.
/// </summary>
public sealed class OfflineGrantService
{
    private static readonly ConcurrentDictionary<string, RSA> VerificationKeys = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly ISessionStore _store;
    private readonly IDeviceIdentity _identity;

    public OfflineGrantService(HttpClient http, ISessionStore store, IDeviceIdentity identity)
    {
        _http = http;
        _store = store;
        _identity = identity;
    }

    public async Task<OfflineGrant.OfflineGrantValidation> AuthorizeCaptureAsync(
        SessionDto session,
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var now = DateTimeOffset.UtcNow;
        var stored = await _store.GetOfflineGrantAsync(cancellationToken).ConfigureAwait(false);
        var pinned = await _store.GetPinnedGrantKeyAsync(cancellationToken).ConfigureAwait(false);

        if (stored is not null && pinned is not null && string.Equals(stored.KeyId, pinned.KeyId, StringComparison.Ordinal))
        {
            var current = Validate(stored, pinned, _identity.DeviceId, session.UserId, businessId, locationId, now);

            if (current.IsAllowed)
            {
                await RememberAsync(stored, now, cancellationToken).ConfigureAwait(false);
                return current;
            }

            // A rolled-back clock is never repaired by asking the server: the user must fix the clock.
            if (current.Reason == "clock_rollback")
            {
                return current;
            }
        }

        var issued = await TryIssueAsync(locationId, cancellationToken).ConfigureAwait(false);

        if (issued is null)
        {
            return OfflineGrant.OfflineGrantValidation.Refused(stored is null ? "no_grant" : "grant_unavailable");
        }

        if (pinned is not null && !string.Equals(pinned.KeyId, issued.KeyId, StringComparison.Ordinal))
        {
            // The server presents a key the device never pinned: possible tampering or an unreviewed
            // rotation. The device refuses and stays offline until it is re-enrolled deliberately.
            return OfflineGrant.OfflineGrantValidation.Refused("key_changed");
        }

        if (pinned is null)
        {
            await _store.SavePinnedGrantKeyAsync(new PinnedGrantKey(issued.KeyId, issued.PublicKey), cancellationToken)
                .ConfigureAwait(false);
            pinned = new PinnedGrantKey(issued.KeyId, issued.PublicKey);
        }

        var fresh = new StoredOfflineGrant(issued.Grant, issued.KeyId, now);
        var validation = Validate(fresh, pinned, _identity.DeviceId, session.UserId, businessId, locationId, now);

        if (validation.IsAllowed)
        {
            await _store.SaveOfflineGrantAsync(fresh, cancellationToken).ConfigureAwait(false);
        }

        return validation;
    }

    private async Task<OfflineGrant.OfflineGrantIssueResponse?> TryIssueAsync(
        Guid locationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/devices/{_identity.DeviceId}/offline-grant",
                new OfflineGrant.OfflineGrantIssueRequest(locationId, RegisterSessionId: null),
                ContractJson.Options,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<OfflineGrant.OfflineGrantIssueResponse>(ContractJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline: the stored grant remains the only authority, if it is still valid.
            return null;
        }
    }

    private static OfflineGrant.OfflineGrantValidation Validate(
        StoredOfflineGrant stored,
        PinnedGrantKey pinned,
        Guid deviceId,
        Guid userId,
        Guid businessId,
        Guid locationId,
        DateTimeOffset now)
    {
        var key = VerificationKeys.GetOrAdd(pinned.KeyId, _ => ImportPublicKey(pinned.PublicKey));

        // The device id comes from the recorded installation, not from the grant, so a grant copied
        // from another machine fails the binding check.
        var expectation = new OfflineGrant.OfflineGrantExpectation(
            DeviceId: deviceId,
            BusinessId: businessId,
            LocationId: locationId,
            RegisterSessionId: null,
            UserId: userId,
            Currency: null,
            RequiredPermission: QtsBilling.Contracts.Identity.Permissions.SalesPost);

        return OfflineGrant.Validate(
            stored.Compact,
            key,
            pinned.KeyId,
            expectation,
            now,
            stored.LastSeenAtUtc == default ? null : stored.LastSeenAtUtc);
    }

    private static RSA ImportPublicKey(string publicKey)
    {
        var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key;
    }

    private Task RememberAsync(StoredOfflineGrant stored, DateTimeOffset now, CancellationToken cancellationToken)
        => _store.SaveOfflineGrantAsync(stored with { LastSeenAtUtc = now }, cancellationToken);
}
