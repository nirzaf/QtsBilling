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

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionStore : ISessionStore
{
    private const string CookiePrefix = "session.cookie.";
    private const string SessionKey = "session.summary";
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
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
