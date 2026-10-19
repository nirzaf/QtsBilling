namespace QtsBilling.Persistence.Sqlite;

/// <summary>Result of opening and migrating the local store. Sync must not start until this succeeds.</summary>
public sealed record SqliteStartupResult(
    string DatabasePath,
    int SchemaVersion,
    string JournalMode,
    bool IntegrityCheckPassed,
    IReadOnlyList<string> AppliedMigrations,
    string? PreMigrationBackupPath,
    int RecoveredSendingOperations);

/// <summary>Exception raised when the local database schema is newer than this application understands.
/// The caller must not delete the file; startup is blocked and the data, including pending money rows, stays intact.</summary>
public sealed class SqliteSchemaCompatibilityException : InvalidOperationException
{
    public SqliteSchemaCompatibilityException(string message)
        : base(message)
    {
    }

    public SqliteSchemaCompatibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The database failed SQLite's integrity check. No automatic rebuild occurs (QB-17).</summary>
public sealed class SqliteCorruptionException : InvalidOperationException
{
    public SqliteCorruptionException(string message)
        : base(message)
    {
    }

    public SqliteCorruptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The same local operation id was replayed with a different canonical payload.</summary>
public sealed class SqliteSameKeyDifferentPayloadException : InvalidOperationException
{
    public SqliteSameKeyDifferentPayloadException(Guid operationId)
        : base($"Operation '{operationId}' already exists with a different canonical payload/fingerprint.")
    {
    }
}

/// <summary>A persisted receipt conflicts with the outcome already recorded for this operation.</summary>
public sealed class SqliteReceiptConflictException : InvalidOperationException
{
    public SqliteReceiptConflictException(Guid operationId, string existingResult, string incomingResult)
        : base($"Operation '{operationId}' already has receipt '{existingResult}' but '{incomingResult}' arrived.")
    {
    }
}
