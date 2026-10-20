using Microsoft.Data.Sqlite;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Connection-string policy for the device-local store: one file, no shared cache, no connection pooling
/// (deterministic file lifecycle and PRAGMA application per connection). WAL mode is set once by
/// <see cref="SqliteLocalStore.InitializeAsync"/>.
/// </summary>
public static class SqliteConnectionStrings
{
    static SqliteConnectionStrings() => SQLitePCL.Batteries_V2.Init();

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

    public static string ForEncryptedFile(string databasePath, string hexKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hexKey);
        if (hexKey.Length != 64 || hexKey.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The SQLite encryption key must be 32 bytes encoded as 64 hexadecimal characters.", nameof(hexKey));
        }

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
            Password = $"x'{hexKey}'",
        }.ToString();
    }
}
