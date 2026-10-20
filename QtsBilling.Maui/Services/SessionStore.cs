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
    Task SaveCookieAsync(string name, string value, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetCookiesAsync(CancellationToken cancellationToken = default);

    Task SaveSessionAsync(SessionDto session, CancellationToken cancellationToken = default);

    Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default);

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

/// <summary>The verification key pinned from the server at enrolment; it survives sign-out.</summary>
public sealed record PinnedGrantKey(string KeyId, string PublicKey);

public sealed class SessionStore : ISessionStore
{
    private const string CookiePrefix = "session.cookie.";
    private const string SessionKey = "session.summary";
    private const string OfflineGrantKey = "session.offline_grant";
    private const string PinnedGrantKey = "session.offline_grant_key";
    private const string KnownCookies = "session.cookie.names";

    public async Task SaveCookieAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await SecureStorage.Default.SetAsync(CookiePrefix + name, value).ConfigureAwait(false);

        var names = (Preferences.Default.Get(KnownCookies, string.Empty))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!names.Contains(name, StringComparer.Ordinal))
        {
            names.Add(name);
            Preferences.Default.Set(KnownCookies, string.Join(';', names));
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCookiesAsync(CancellationToken cancellationToken = default)
    {
        var names = (Preferences.Default.Get(KnownCookies, string.Empty))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var value = await SecureStorage.Default.GetAsync(CookiePrefix + name).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(value))
            {
                cookies[name] = value;
            }
        }

        return cookies;
    }

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
        var names = (Preferences.Default.Get(KnownCookies, string.Empty))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in names)
        {
            SecureStorage.Default.Remove(CookiePrefix + name);
        }

        Preferences.Default.Set(KnownCookies, string.Empty);
        SecureStorage.Default.Remove(SessionKey);
        SecureStorage.Default.Remove(OfflineGrantKey);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
