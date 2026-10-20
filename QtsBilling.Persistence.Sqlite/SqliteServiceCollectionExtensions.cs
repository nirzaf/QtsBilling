using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Registers the scoped local-storage services for a MAUI/Blazor host. The host calls
/// <see cref="SqliteLocalStore.InitializeAsync"/> once at startup, before sync starts, and supplies the
/// device's private data-directory path (<c>FileSystem.AppDataDirectory</c> on MAUI).
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    public static IServiceCollection AddQtsSqliteLocalStore(
        this IServiceCollection services,
        SqliteLocalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("A local SQLite database path is required.", nameof(options));
        }

        // Fail fast if the data directory cannot be created/accessed.
        var connectionString = options.EncryptionKeyHex is { Length: > 0 } encryptionKey
            ? SqliteConnectionStrings.ForEncryptedFile(options.DatabasePath, encryptionKey)
            : SqliteConnectionStrings.ForFile(options.DatabasePath);

        services.AddSingleton(options);
        services.AddSingleton<SqliteWriteGate>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<LocalStoreDbContext>(builder => builder
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddScoped<SqliteLocalStore>();
        return services;
    }
}
