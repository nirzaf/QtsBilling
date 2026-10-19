using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. It never needs an existing database: the SQLite
/// migrations are authored from the model in this assembly and are completely independent of the
/// PostgreSQL migrations in <c>QtsBilling.Persistence.Postgres</c>.
/// </summary>
public sealed class LocalStoreDbContextFactory : IDesignTimeDbContextFactory<LocalStoreDbContext>
{
    public LocalStoreDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("QTS_SQLITE_DESIGN_PATH")
            ?? Path.Combine(Path.GetTempPath(), "qtsbilling-sqlite-design", "local.db");

        var options = new DbContextOptionsBuilder<LocalStoreDbContext>()
            .UseSqlite(SqliteConnectionStrings.ForFile(path))
            .Options;

        return new LocalStoreDbContext(options);
    }
}
