using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Sqlite.Entities;

namespace QtsBilling.Persistence.Sqlite.Tests;

/// <summary>
/// Creates a real temp-file SQLite store (never in-memory) and can close/reopen the service provider against
/// the same file to exercise crash/restart and migration recovery.
/// </summary>
internal sealed class LocalStoreHarness : IAsyncDisposable
{
    private ServiceProvider? _provider;
    private IServiceScope? _scope;

    private LocalStoreHarness(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "local.db");
        Open();
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public SqliteLocalStore Store => _scope is not null
        ? _scope.ServiceProvider.GetRequiredService<SqliteLocalStore>()
        : throw new InvalidOperationException("The store is closed.");

    public static LocalStoreHarness Create()
        => new(Path.Combine(Path.GetTempPath(), "qtsbilling-sqlite-tests", Guid.NewGuid().ToString("N")));

    public void Open()
    {
        var services = new ServiceCollection();
        services.AddQtsSqliteLocalStore(new SqliteLocalStoreOptions { DatabasePath = DatabasePath });
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Close()
    {
        _scope?.Dispose();
        _scope = null;
        _provider?.Dispose();
        _provider = null;
        SqliteConnection.ClearAllPools();
    }

    public void Reopen()
    {
        Close();
        Open();
    }

    public long ScalarLong(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long ScalarLongIn(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public string? ScalarString(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    public void Execute(string sql, bool foreignKeys = false)
    {
        using var connection = OpenRaw();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA foreign_keys = {(foreignKeys ? "ON" : "OFF")};";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public LocalStoreDbContext CreateContext()
        => _scope is not null
            ? _scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<LocalStoreDbContext>>().CreateDbContext()
            : throw new InvalidOperationException("The store is closed.");

    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await Task.Yield();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }
}

internal static class TestData
{
    public static ProvisionalCashSaleInput Sale(
        Guid? operationId = null,
        Guid? businessId = null,
        Guid? locationId = null,
        Guid? deviceId = null,
        Guid? userId = null,
        Guid? registerId = null,
        decimal amount = 12.34m,
        decimal quantity = 2.5m,
        string payload = "{\"type\":\"pos.sale.capture.v1\",\"sale\":1}")
    {
        var business = businessId ?? Guid.CreateVersion7();
        var location = locationId ?? Guid.CreateVersion7();
        var device = deviceId ?? Guid.CreateVersion7();
        var user = userId ?? Guid.CreateVersion7();
        var register = registerId ?? Guid.CreateVersion7();

        return new ProvisionalCashSaleInput
        {
            OperationId = operationId ?? Guid.CreateVersion7(),
            DeviceId = device,
            BusinessId = business,
            LocationId = location,
            UserId = user,
            RegisterId = register,
            RegisterSessionId = Guid.CreateVersion7(),
            Amount = amount,
            CanonicalPayload = payload,
            Lines =
            [
                new ProvisionalSaleLineInput(
                    Guid.CreateVersion7(),
                    quantity,
                    UnitPrice: 4.00m,
                    Tax: 1.00m,
                    Discount: 0.50m,
                    ExpectedCatalogRevision: 7),
                new ProvisionalSaleLineInput(
                    Guid.CreateVersion7(),
                    Quantity: 1m,
                    UnitPrice: 5.34m,
                    Tax: 0m,
                    Discount: 0m,
                    ExpectedCatalogRevision: 7),
            ],
        };
    }

    public static OutboxOperation Operation(
        Guid? operationId = null,
        Guid? businessId = null,
        Guid? locationId = null,
        Guid? deviceId = null,
        string payload = "{\"type\":\"cash.session.open.v1\"}")
        => new()
        {
            OperationId = operationId ?? Guid.CreateVersion7(),
            DeviceId = deviceId ?? Guid.CreateVersion7(),
            BusinessId = businessId ?? Guid.CreateVersion7(),
            LocationId = locationId ?? Guid.CreateVersion7(),
            OperationType = "cash.session.open.v1",
            PayloadVersion = 1,
            CanonicalPayload = payload,
            Sha256Fingerprint = SqlitePayloadHash.Compute(payload),
            State = OutboxState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow,
        };
}
