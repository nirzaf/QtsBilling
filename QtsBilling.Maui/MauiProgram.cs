using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Invoices;
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

        builder.Services
            .AddHttpClient<IOidcTokenClient, OidcTokenClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        builder.Services
            .AddHttpClient<IPkceLoopbackAuthenticator, PkceLoopbackAuthenticator>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromMinutes(5);
            });

        // Device-local store (QB-17): one WAL SQLite file in the private app data directory.
        var localStorePath = Path.Combine(FileSystem.AppDataDirectory, "qtsbilling", "local.db");
        var localStoreEncryptionKey = LocalDatabaseKeyStore.LoadOrCreateHexKey();
        builder.Services.AddQtsSqliteLocalStore(new SqliteLocalStoreOptions
        {
            DatabasePath = localStorePath,
            EncryptionKeyHex = localStoreEncryptionKey,
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

        // The sale history and refund workbench (specification task 21) is one shared page in both hosts. A
        // terminal reads the server's posted sales and refunds through the same contract the browser uses; the
        // drawer it pays out of is the server's own register session, never a locally invented balance.
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Sales.ISaleApiClient, QtsBilling.Contracts.Sales.SaleApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Orders.IQuoteApiClient, QtsBilling.Contracts.Orders.QuoteApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Orders.ISalesOrderApiClient, QtsBilling.Contracts.Orders.SalesOrderApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Orders.IFulfillmentApiClient, QtsBilling.Contracts.Orders.FulfillmentApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<IInvoiceApiClient, InvoiceApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        // Local document output on the device (specification task 24, T24.02). The invoice snapshot arrives over
        // the bearer client above, and only its content is handed to the print facility: a device never navigates
        // a WebView at a protected route, because that is the one shape of printing that forces a token into a
        // URL. Registering it here is the same cross-host gate F25 asks for - without it the terminal's operator
        // would get a print button that throws.
        builder.Services.AddScoped<QtsBilling.Contracts.Abstractions.IDocumentOutput, NativeDocumentOutput>();
        builder.Services.AddScoped<QtsBilling.Contracts.Abstractions.ILocalFileOutput, NativeFileOutput>();

        // The administration screens live in the shared library, and this host routes them (Routes.razor adds the
        // whole QtsBilling.Ui assembly). Registering their clients here is the cross-host gate specification finding
        // F25 asks for: a terminal signed in as an owner gets the same owner screens as the browser, and a host that
        // forgot one would hand the operator a dead form.
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Administration.IBusinessSetupApiClient, QtsBilling.Contracts.Administration.BusinessSetupApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Administration.IAdministrationUserApiClient, QtsBilling.Contracts.Administration.AdministrationUserApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Administration.IDeviceAdministrationApiClient, QtsBilling.Contracts.Administration.DeviceAdministrationApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Accounting.IAccountingApiClient, QtsBilling.Contracts.Accounting.AccountingApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        // The manager's reports on the terminal: the same typed contract, the same posted facts, and a CSV the store
        // writes locally rather than sending to a BI service (task 33).
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Reports.IReportApiClient, QtsBilling.Contracts.Reports.ReportApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Purchasing.ISupplierApiClient, QtsBilling.Contracts.Purchasing.SupplierApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        // The procurement screens are shared pages this host routes too (specification task 26): a store receiving
        // a delivery on a terminal needs the same purchase-order, receipt and return contract as the browser.
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Purchasing.IPurchasingApiClient, QtsBilling.Contracts.Purchasing.PurchasingApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Catalog.IMasterDataApiClient, QtsBilling.Contracts.Catalog.MasterDataApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Pricing.IPricingApiClient, QtsBilling.Contracts.Pricing.PricingApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        builder.Services
            .AddHttpClient<IPaymentApiClient, PaymentApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        // Expense workbench (specification task 28): the shared expense screens render on the terminal too.
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Expenses.IExpenseApiClient, QtsBilling.Contracts.Expenses.ExpenseApiClient>((services, client) =>
            {
                client.BaseAddress = services.GetRequiredService<IServerSettings>().ServerBaseUrl;
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<SessionAuthHandler>();

        // Bank reconciliation (specification task 31): the review screen is a shared page this terminal routes too,
        // so an accountant reconciling a statement on a counter talks to the same contract as the browser.
        builder.Services
            .AddHttpClient<QtsBilling.Contracts.Banking.IBankingApiClient, QtsBilling.Contracts.Banking.BankingApiClient>((services, client) =>
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
