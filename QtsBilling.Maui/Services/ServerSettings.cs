namespace QtsBilling.Maui.Services;

/// <summary>
/// Non-secret device settings (server address, last known business/location, app version). Stored in the
/// application preferences so the app can start offline with the last known configuration.
/// </summary>
public interface IServerSettings
{
    Uri ServerBaseUrl { get; }

    void SetServerBaseUrl(Uri baseUrl);

    Guid? LastBusinessId { get; set; }

    Guid? LastLocationId { get; set; }

    string AppVersion { get; }
}

public sealed class ServerSettings : IServerSettings
{
    private const string ServerUrlKey = "server.baseUrl";
    private const string BusinessIdKey = "server.lastBusinessId";
    private const string LocationIdKey = "server.lastLocationId";

    /// <summary>Default API address of the self-hosted server; overridable in the app.</summary>
    public const string DefaultServerBaseUrl = "http://localhost:5188";

    public ServerSettings()
    {
        var stored = Preferences.Default.Get(ServerUrlKey, string.Empty);
        ServerBaseUrl = Uri.TryCreate(stored, UriKind.Absolute, out var parsed) ? parsed : new Uri(DefaultServerBaseUrl);
    }

    public Uri ServerBaseUrl { get; private set; }

    public string AppVersion => AppInfo.Current.VersionString;

    public Guid? LastBusinessId
    {
        get => Parse(Preferences.Default.Get(BusinessIdKey, string.Empty));
        set => Preferences.Default.Set(BusinessIdKey, value?.ToString("D") ?? string.Empty);
    }

    public Guid? LastLocationId
    {
        get => Parse(Preferences.Default.Get(LocationIdKey, string.Empty));
        set => Preferences.Default.Set(LocationIdKey, value?.ToString("D") ?? string.Empty);
    }

    public void SetServerBaseUrl(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ServerBaseUrl = baseUrl;
        Preferences.Default.Set(ServerUrlKey, baseUrl.ToString());
    }

    private static Guid? Parse(string value)
        => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}
