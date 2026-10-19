using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using QtsBilling.Contracts.Clients;
using QtsBilling.Maui.Services;
using QtsBilling.Persistence.Sqlite;

namespace QtsBilling.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // The shared MudBlazor surface (QB-16) is used unchanged by this host.
        builder.Services.AddMudServices();

        // Device services (QB-18).
        builder.Services.AddSingleton<IDeviceIdentity, DeviceIdentity>();
        builder.Services.AddSingleton<IServerSettings, ServerSettings>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<IDeviceConnectivity, DeviceConnectivity>();
        builder.Services.AddSingleton<IStartupDiagnostics, StartupDiagnostics>();
        builder.Services.AddSingleton<IDeviceApiLog, DeviceApiLog>();
        builder.Services.AddTransient<SessionAuthHandler>();

        // Device-local store (QB-17): one WAL SQLite file in the private app data directory.
        var localStorePath = Path.Combine(FileSystem.AppDataDirectory, "qtsbilling", "local.db");
        builder.Services.AddQtsSqliteLocalStore(new SqliteLocalStoreOptions
        {
            DatabasePath = localStorePath,
        });

        // The browser never talks to a database; the desktop app uses the same typed API clients.
        builder.Services
            .AddHttpClient<ICustomerApiClient, DeviceCustomerApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<IAccountApiClient, DeviceAccountApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
