using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Applies the per-connection SQLite pragmas that EF's connection string cannot express. WAL is a
/// database property set once at startup; foreign-key enforcement and synchronous durability are
/// per connection (QB-17).
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }
}
