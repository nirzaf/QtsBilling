using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Sqlite;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class SqliteEncryptionTests
{
    [Fact]
    public async Task Existing_plaintext_store_is_exported_to_sqlcipher_with_pending_money_preserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qtsbilling-sqlcipher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local.db");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (var plaintextProvider = CreateProvider(databasePath, encryptionKeyHex: null))
            await using (var scope = plaintextProvider.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<SqliteLocalStore>();
                await store.InitializeAsync();
                await store.CaptureProvisionalCashSaleAsync(TestData.Sale(amount: 25.50m));
            }

            Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(databasePath).AsSpan(0, 16)));

            await using (var encryptedProvider = CreateProvider(databasePath, key))
            await using (var scope = encryptedProvider.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<SqliteLocalStore>();
                await store.InitializeAsync();

                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LocalStoreDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                Assert.Equal(1, await db.LocalSaleDrafts.CountAsync());
                Assert.Equal(2, await db.LocalSaleLines.CountAsync());
                Assert.Equal(1, await db.LocalCashMovements.CountAsync());
                Assert.Equal(1, await db.OutboxOperations.CountAsync());
            }

            var header = new byte[16];
            await using (var file = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                _ = await file.ReadAsync(header);
            }
            Assert.NotEqual("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));

            using var unkeyed = new SqliteConnection(SqliteConnectionStrings.ForFile(databasePath));
            unkeyed.Open();
            using var command = unkeyed.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            Assert.Throws<SqliteException>(() => command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateProvider(string databasePath, string? encryptionKeyHex)
    {
        var services = new ServiceCollection();
        services.AddQtsSqliteLocalStore(new SqliteLocalStoreOptions
        {
            DatabasePath = databasePath,
            EncryptionKeyHex = encryptionKeyHex,
        });
        return services.BuildServiceProvider();
    }
}
