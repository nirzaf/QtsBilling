using Microsoft.Data.Sqlite;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Connection-string policy for the device-local store: one file, no shared cache, no connection pooling
/// (deterministic file lifecycle and PRAGMA application per connection). WAL mode is set once by
/// <see cref="SqliteLocalStore.InitializeAsync"/>.
/// </summary>
public static class SqliteConnectionStrings
{
    public static string ForFile(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }
}
