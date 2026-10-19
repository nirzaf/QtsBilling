using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// Writes the master-data change feed (QB-21). Every change is appended in the **same transaction** as the
/// business write that caused it, and the per-business revision is allocated by an atomic, row-locked
/// increment, so the feed order follows the commit order and no committed update can be skipped.
/// </summary>
public static class SyncChangeWriter
{
    /// <summary>
    /// Appends one change. The caller must invoke it before its <c>SaveChangesAsync</c>, so the change row
    /// and the business row are committed together.
    /// </summary>
    public static async Task<long> AppendAsync(
        BillingDbContext db,
        Guid businessId,
        string entityType,
        Guid entityId,
        SyncChangeKind kind,
        string? payloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        var revision = await AllocateRevisionAsync(db, businessId, now, cancellationToken).ConfigureAwait(false);
        var ordinal = await NextOrdinalAsync(db, businessId, revision, cancellationToken).ConfigureAwait(false);

        db.SyncChanges.Add(new SyncChange
        {
            BusinessId = businessId,
            Revision = revision,
            Ordinal = ordinal,
            EntityType = entityType,
            EntityId = entityId,
            Kind = kind,
            PayloadJson = payloadJson,
            ChangedAtUtc = now,
        });

        return revision;
    }

    /// <summary>
    /// Allocates the next revision for the business. The update locks the row until the transaction ends, so
    /// two concurrent writers can never receive the same revision.
    /// </summary>
    private static async Task<long> AllocateRevisionAsync(
        BillingDbContext db,
        Guid businessId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;

        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_revisions (business_id, revision, updated_at_utc)
                VALUES (@business_id, 1, @now)
                ON CONFLICT (business_id) DO UPDATE
                    SET revision = sync_revisions.revision + 1, updated_at_utc = @now
                RETURNING revision;
                """;
            AddParameter(command, "business_id", businessId);
            AddParameter(command, "now", now.UtcDateTime);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The next ordinal inside the revision; the revision lock keeps the ordinals unique.</summary>
    private static async Task<int> NextOrdinalAsync(
        BillingDbContext db,
        Guid businessId,
        long revision,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;

        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(MAX(ordinal), -1) + 1
                FROM sync_changes
                WHERE business_id = @business_id AND revision = @revision;
                """;
            AddParameter(command, "business_id", businessId);
            AddParameter(command, "revision", revision);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
    /// <summary>Serializes a projection for the feed. Secrets are never part of a projection.</summary>
    public static string Projection<T>(T value) => JsonSerializer.Serialize(value, QtsBilling.Contracts.Serialization.ContractJson.Options);
}
