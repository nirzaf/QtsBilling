using System.Text.Json;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Device session storage. The session cookie and the last known session summary live in the platform
/// secure store (DPAPI-backed on Windows), never in plain preferences and never in the application
/// package. Signing out removes them.
/// </summary>
public interface ISessionStore
{
    Task SaveSessionAsync(SessionDto session, CancellationToken cancellationToken = default);

    Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default);

    Task SaveTokensAsync(StoredOidcTokens tokens, CancellationToken cancellationToken = default);

    Task<StoredOidcTokens?> GetTokensAsync(CancellationToken cancellationToken = default);

    Task SaveOfflineGrantAsync(StoredOfflineGrant grant, CancellationToken cancellationToken = default);

    Task<StoredOfflineGrant?> GetOfflineGrantAsync(CancellationToken cancellationToken = default);

    Task SavePinnedGrantKeyAsync(PinnedGrantKey key, CancellationToken cancellationToken = default);

    Task<PinnedGrantKey?> GetPinnedGrantKeyAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>The grant plus the high-water mark of the monotonic clock.</summary>
public sealed record StoredOfflineGrant(
    string Compact,
    string KeyId,
    DateTimeOffset LastSeenAtUtc);

/// <summary>Rotating native access/refresh tokens stored in the Windows secure store (DPAPI-backed).</summary>
public sealed record StoredOidcTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    Guid UserId,
    Guid BusinessId);

/// <summary>The verification key pinned from the server at enrolment; it survives sign-out.</summary>
public sealed record PinnedGrantKey(string KeyId, string PublicKey);

public sealed class SessionStore : ISessionStore
{
    private const string SessionKey = "session.summary";
    private const string TokensKey = "session.oidc_tokens";
    private const string OfflineGrantKey = "session.offline_grant";
    private const string PinnedGrantKey = "session.offline_grant_key";

    public async Task SaveSessionAsync(SessionDto session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var json = JsonSerializer.Serialize(session, ContractJson.Options);
        await SecureStorage.Default.SetAsync(SessionKey, json).ConfigureAwait(false);
    }

    public async Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(SessionKey).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionDto>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveTokensAsync(StoredOidcTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens.AccessToken);

        var json = JsonSerializer.Serialize(tokens, ContractJson.Options);
        await SecureStorage.Default.SetAsync(TokensKey, json).ConfigureAwait(false);
    }

    public async Task<StoredOidcTokens?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(TokensKey).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredOidcTokens>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveOfflineGrantAsync(StoredOfflineGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var json = JsonSerializer.Serialize(grant, ContractJson.Options);
        await SecureStorage.Default.SetAsync(OfflineGrantKey, json).ConfigureAwait(false);
    }

    public async Task<StoredOfflineGrant?> GetOfflineGrantAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(OfflineGrantKey).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredOfflineGrant>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<PinnedGrantKey?> GetPinnedGrantKeyAsync(CancellationToken cancellationToken = default)
    {
        var json = await SecureStorage.Default.GetAsync(PinnedGrantKey).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PinnedGrantKey>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SavePinnedGrantKeyAsync(PinnedGrantKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var json = JsonSerializer.Serialize(key, ContractJson.Options);
        await SecureStorage.Default.SetAsync(PinnedGrantKey, json).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        SecureStorage.Default.Remove(SessionKey);
        SecureStorage.Default.Remove(TokensKey);
        SecureStorage.Default.Remove(OfflineGrantKey);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
