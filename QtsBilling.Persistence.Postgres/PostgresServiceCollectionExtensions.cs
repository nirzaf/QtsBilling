using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace QtsBilling.Persistence.Postgres;

/// <summary>Registration helpers for the authoritative PostgreSQL context.</summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>Environment variable holding the authoritative PostgreSQL 18 connection string.</summary>
    public const string ConnectionStringVariable = "QTS_POSTGRES_CONNECTION";

    /// <summary>Development default used only when the environment variable is absent.</summary>
    public const string DevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=qtsbilling;Username=qtsbilling;Password=qtsbilling";

    public static string ResolveConnectionString()
        => Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DevelopmentConnectionString;

    /// <summary>
    /// Registers <see cref="BillingDbContext"/> with transient-fault retry. Callers must supply the
    /// authoritative connection string; no PostgreSQL credentials ever flow to browser or MAUI clients.
    /// </summary>
    public static IServiceCollection AddQtsPostgresBilling(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<BillingDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3))
            .EnableDetailedErrors());

        return services;
    }
}
