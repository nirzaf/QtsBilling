using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using QtsBilling.Persistence.Postgres;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// Runs a master-data write and its change-feed row in one database transaction. The transaction is opened
/// through the configured execution strategy, so a transient database failure retries the whole unit
/// (business write + feed row) instead of leaving the feed behind the business change (QB-21).
/// </summary>
public static class SyncTransaction
{
    public static async Task<T> RunAsync<T>(
        BillingDbContext db,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var result = await action().ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }).ConfigureAwait(false);
    }
}
