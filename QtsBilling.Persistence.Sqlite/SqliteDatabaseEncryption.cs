using System.Text;
using Microsoft.Data.Sqlite;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>Converts an existing plaintext local store to SQLCipher without replacing it until the export checks clean.</summary>
internal static class SqliteDatabaseEncryption
{
    private static readonly byte[] PlaintextHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static async Task PrepareAsync(SqliteLocalStoreOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.EncryptionKeyHex))
        {
            return;
        }

        var databasePath = Path.GetFullPath(options.DatabasePath);
        _ = SqliteConnectionStrings.ForEncryptedFile(databasePath, options.EncryptionKeyHex);
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
        {
            return;
        }

        var header = new byte[PlaintextHeader.Length];
        await using (var file = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read, PlaintextHeader.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var read = await file.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            if (read != header.Length || !header.AsSpan().SequenceEqual(PlaintextHeader))
            {
                return;
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var encryptedPath = databasePath + ".sqlcipher-" + id + ".tmp";
        var backupPath = databasePath + ".pre-encryption-" + id + ".bak";
        var replaced = false;
        try
        {
            await using (var source = new SqliteConnection(SqliteConnectionStrings.ForFile(databasePath)))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var backup = source.CreateCommand())
                {
                    backup.CommandText = "VACUUM INTO $path;";
                    backup.Parameters.AddWithValue("$path", backupPath);
                    await backup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var attach = source.CreateCommand())
                {
                    // The key is restricted to hex characters above, so it is safe to use as a SQLCipher key literal.
                    attach.CommandText = $"ATTACH DATABASE $path AS encrypted KEY \"x'{options.EncryptionKeyHex}'\";";
                    attach.Parameters.AddWithValue("$path", encryptedPath);
                    await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var export = source.CreateCommand())
                {
                    export.CommandText = "SELECT sqlcipher_export('encrypted');";
                    await export.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var detach = source.CreateCommand())
                {
                    detach.CommandText = "DETACH DATABASE encrypted;";
                    await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await VerifyEncryptedFileAsync(encryptedPath, options.EncryptionKeyHex, cancellationToken).ConfigureAwait(false);
            File.Replace(encryptedPath, databasePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            replaced = true;

            // The temporary backup is plaintext because it was made before export. Remove it only after
            // the encrypted replacement has passed quick_check; on failure the source remains untouched.
            foreach (var staleBackup in Directory.EnumerateFiles(
                         Path.GetDirectoryName(databasePath)!,
                         Path.GetFileName(databasePath) + ".pre-encryption-*.bak",
                         SearchOption.TopDirectoryOnly))
            {
                File.Delete(staleBackup);
            }
        }
        finally
        {
            if (File.Exists(encryptedPath))
            {
                File.Delete(encryptedPath);
            }

            if (!replaced && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
    }

    private static async Task VerifyEncryptedFileAsync(string path, string key, CancellationToken cancellationToken)
    {
        var connectionString = SqliteConnectionStrings.ForEncryptedFile(path, key);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The encrypted local database export failed its integrity check.");
        }
    }
}
