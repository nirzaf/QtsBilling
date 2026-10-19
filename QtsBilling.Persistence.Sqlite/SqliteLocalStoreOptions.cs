namespace QtsBilling.Persistence.Sqlite;

/// <summary>Configuration for the device-local store. Paths live under the app's private data directory.</summary>
public sealed record SqliteLocalStoreOptions
{
    /// <summary>Full path of the single local SQLite database (cache + durable pending tables).</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Run <c>PRAGMA quick_check</c> before touching the schema. Corruption blocks startup;
    /// the store never deletes or rebuilds the file automatically.</summary>
    public bool RunIntegrityCheckOnStartup { get; init; } = true;

    /// <summary>Create a <c>VACUUM INTO</c> snapshot before applying pending schema migrations.</summary>
    public bool CreatePreMigrationBackup { get; init; } = true;
}
