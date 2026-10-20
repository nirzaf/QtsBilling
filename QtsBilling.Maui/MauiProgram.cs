using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sync;
using QtsBilling.Maui.Services;
using QtsBilling.Persistence.Sqlite;
using QtsBilling.Persistence.Sqlite.Pos;

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

        // Device point of sale (QB-19): the local store owns the money, the host owns the permissions
        // and the catalog pull.
        builder.Services.AddScoped<LocalPosStore>();
        builder.Services.AddScoped<DeviceCatalogSync>();
        builder.Services.AddScoped<ILocalPosService, DeviceLocalPosService>();
        builder.Services.AddSingleton<QtsBilling.Ui.Layout.INavContributor, MauiNavContributor>();

        // Device synchronization (QB-22): the push/pull loop, the conflict queue and the background timer.
        builder.Services.AddScoped<ISyncCoordinator, DeviceSyncCoordinator>();
        builder.Services.AddScoped<ISyncConflictService, DeviceSyncConflictService>();
        builder.Services.AddSingleton<DeviceSyncTimer>();

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

        builder.Services
            .AddHttpClient<OfflineGrantService>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Catalog.IProductApiClient, QtsBilling.Contracts.Catalog.ProductApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Inventory.IInventoryApiClient, QtsBilling.Contracts.Inventory.InventoryApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Sales.ICashRegisterApiClient, QtsBilling.Contracts.Sales.CashRegisterApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Sync.ISyncApiClient, QtsBilling.Contracts.Sync.SyncApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // The device-local store is opened and migrated before the window exists, so no screen can query
        // a database whose schema is not ready yet (QB-17/QB-22).
        InitializeLocalStore(app);

        return app;
    }

    private static void InitializeLocalStore(MauiApp app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<SqliteLocalStore>();
            store.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QtsBilling.Maui could not open the local store: {exception}");
        }
    }
}
