using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Sqlite;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Startup report for support and smoke checks. It records what the device actually loaded (shared
/// components, local-store schema, connectivity) without any secret: no password, cookie or token value is
/// written here.
/// </summary>
public interface IStartupDiagnostics
{
    string ReportPath { get; }

    Task WriteAsync(
        SqliteStartupResult? localStore = null,
        Exception? failure = null,
        CancellationToken cancellationToken = default);
}

public sealed class StartupDiagnostics : IStartupDiagnostics
{
    private readonly IDeviceIdentity _deviceIdentity;
    private readonly IServerSettings _settings;
    private readonly IDeviceConnectivity _connectivity;
    private readonly IServiceProvider _services;

    public StartupDiagnostics(
        IDeviceIdentity deviceIdentity,
        IServerSettings settings,
        IDeviceConnectivity connectivity,
        IServiceProvider services)
    {
        _deviceIdentity = deviceIdentity;
        _settings = settings;
        _connectivity = connectivity;
        _services = services;
    }

    public string ReportPath => Path.Combine(FileSystem.AppDataDirectory, "startup-diagnostics.txt");

    public async Task WriteAsync(
        SqliteStartupResult? localStore = null,
        Exception? failure = null,
        CancellationToken cancellationToken = default)
    {
        var report = new StringBuilder();

        report.AppendLine($"writtenAtUtc={DateTimeOffset.UtcNow:O}");
        report.AppendLine("app=QTS Billing (Windows desktop)");
        report.AppendLine($"appVersion={_settings.AppVersion}");
        report.AppendLine($"os={Environment.OSVersion}");
        report.AppendLine($"deviceId={_deviceIdentity.DeviceId:D}");
        report.AppendLine($"deviceName={_deviceIdentity.DeviceName}");
        report.AppendLine($"serverBaseUrl={_settings.ServerBaseUrl}");
        report.AppendLine($"connectivity={_connectivity.Description}");
        report.AppendLine($"businessId={_settings.LastBusinessId?.ToString("D") ?? "<none>"}");
        report.AppendLine($"locationId={_settings.LastLocationId?.ToString("D") ?? "<none>"}");

        if (localStore is not null)
        {
            report.AppendLine($"localStorePath={localStore.DatabasePath}");
            report.AppendLine($"localStoreSchemaVersion={localStore.SchemaVersion}");
            report.AppendLine($"localStoreJournalMode={localStore.JournalMode}");
            report.AppendLine($"localStoreIntegrityCheck={(localStore.IntegrityCheckPassed ? "ok" : "skipped")}");
            report.AppendLine($"localStoreAppliedMigrations={string.Join(",", localStore.AppliedMigrations)}");
            report.AppendLine($"localStorePreMigrationBackup={localStore.PreMigrationBackupPath ?? "<none>"}");
            report.AppendLine($"localStoreRecoveredSendingOperations={localStore.RecoveredSendingOperations}");
        }
        else
        {
            report.AppendLine("localStore=<not initialized>");
        }

        foreach (var route in SharedRoutes())
        {
            report.AppendLine($"sharedRoute={route}");
        }

        report.AppendLine($"startup={(failure is null ? "ok" : $"failed: {failure.GetType().Name}: {failure.Message}")}");
        report.AppendLine($"syncCoordinatorRegistered={_services.GetService<ISyncCoordinator>() is not null}");

        var directory = Path.GetDirectoryName(ReportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(ReportPath, report.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> SharedRoutes()
    {
        var assembly = typeof(QtsBilling.Ui.Pages.Customers).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            var route = type.GetCustomAttribute<RouteAttribute>();
            if (route is not null)
            {
                yield return route.Template;
            }
        }
    }
}
