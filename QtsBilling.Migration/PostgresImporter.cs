using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Persistence.Postgres;

namespace QtsBilling.Migration;

/// <summary>What one import run did.</summary>
public sealed record ImportReport(
    IReadOnlyList<TableImportReport> Tables,
    int IdentifierMapRows,
    int Quarantined);

/// <summary>What one target table received.</summary>
public sealed record TableImportReport(string Table, int Rows, int Updated);

/// <summary>
/// The idempotent load (spec §6.1, step 6). Every target row is upserted against the stable derived id,
/// and every mapping is written to <c>legacy_identifier_map</c>, so a rerun updates the same rows instead
/// of creating a second copy. Nothing is invented: a row that cannot be mapped is quarantined upstream.
/// </summary>
public sealed class PostgresImporter
{
    private readonly DeterministicTransform _transform;

    public PostgresImporter(DeterministicTransform transform)
    {
        _transform = transform;
    }

    public async Task<ImportReport> ImportAsync(
        BillingDbContext db,
        MigrationMapping mapping,
        IReadOnlyList<TransformResult> tables,
        DateTimeOffset runTimestamp,
        CancellationToken cancellationToken = default)
    {
        var tableReports = new List<TableReport>();
        var mapRows = 0;
        var quarantined = 0;

        foreach (var table in tables)
        {
            quarantined += table.Quarantine.Count;

            var columns = await LoadColumnsAsync(db, table.TargetTable, cancellationToken).ConfigureAwait(false);
            var rows = 0;
            var updated = 0;

            foreach (var row in table.Rows)
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var businessId = Guid.Empty;

                foreach (var (column, value) in row)
                {
                    if (column.StartsWith("__", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!columns.Contains(column))
                    {
                        // The mapping targets a column the schema does not have: that is a mapping error.
                        throw new InvalidOperationException(
                            $"The mapping writes to '{column}', which the table '{table.TargetTable}' does not have.");
                    }

                    values[column] = value;
                }

                // The references are resolved through the same deterministic derivation.
                foreach (var field in mapping.Collections
                    .SelectMany(collection => collection.Fields)
                    .Where(field => field.Kind == FieldKind.Reference))
                {
                    if (!row.TryGetPropertyValue(field.Target, out var raw) || raw is null)
                    {
                        continue;
                    }

                    var referenced = raw.ToString()!;
                    var targetId = _transform.TargetId(field.References!, referenced);

                    values[field.Target] = targetId.ToString("D");

                    if (string.Equals(field.Target, "business_id", StringComparison.OrdinalIgnoreCase))
                    {
                        businessId = targetId;
                    }
                }

                values["id"] = row["id"]!.ToString();
                values["version"] = 1;
                values["created_at_utc"] = runTimestamp;
                values["updated_at_utc"] = runTimestamp;

                if (columns.Contains("is_active") && !values.ContainsKey("is_active"))
                {
                    values["is_active"] = true;
                }

                var (inserted, existed) = await UpsertAsync(db, table.TargetTable, values, cancellationToken).ConfigureAwait(false);

                rows++;
                if (existed)
                {
                    updated++;
                }

                var sourceCollection = row["__source_collection"]!.ToString()!;
                var sourceId = row["__source_id"]!.ToString()!;

                if (await WriteIdentifierMapAsync(db, mapping, sourceCollection, sourceId, Guid.Parse(values["id"]!.ToString()!), businessId, runTimestamp, cancellationToken).ConfigureAwait(false))
                {
                    mapRows++;
                }
            }

            tableReports.Add(new TableReport(table.TargetTable, rows, updated));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ImportReport(
            tableReports.Select(report => new TableImportReport(report.Table, report.Rows, report.Updated)).ToList(),
            mapRows,
            quarantined);
    }

    private static async Task<IReadOnlySet<string>> LoadColumnsAsync(
        BillingDbContext db,
        string table,
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
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table;
                """;
            command.Parameters.AddWithValue("table", table);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<(bool Inserted, bool Existed)> UpsertAsync(
        BillingDbContext db,
        string table,
        IReadOnlyDictionary<string, object?> values,
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
            var columns = values.Keys.ToArray();
            var parameters = new List<string>(columns.Length);
            var arguments = new List<object?>(columns.Length);

            for (var index = 0; index < columns.Length; index++)
            {
                parameters.Add($"@p{index}");
                arguments.Add(values[columns[index]]);
            }

            var updatable = columns
                .Where(column => !string.Equals(column, "id", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var sql = $"""
                INSERT INTO {table} ({string.Join(", ", columns)})
                VALUES ({string.Join(", ", parameters)})
                ON CONFLICT (id) DO UPDATE SET {string.Join(", ", updatable.Select(column => $"{column} = EXCLUDED.{column}"))}
                RETURNING (xmax = 0) AS inserted;
                """;

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (var index = 0; index < columns.Length; index++)
            {
                command.Parameters.AddWithValue($"p{index}", arguments[index] ?? DBNull.Value);
            }

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var inserted = result is bool flag && flag;

            return (inserted, !inserted);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> WriteIdentifierMapAsync(
        BillingDbContext db,
        MigrationMapping mapping,
        string sourceCollection,
        string sourceId,
        Guid targetId,
        Guid businessId,
        DateTimeOffset runTimestamp,
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
                INSERT INTO legacy_identifier_map
                    (id, business_id, source_collection, legacy_id, entity_type, target_id, migrated_at_utc, created_at_utc, updated_at_utc)
                VALUES
                    (@id, @business_id, @collection, @legacy_id, @entity_type, @target_id, @now, @now, @now)
                ON CONFLICT (id) DO UPDATE SET target_id = EXCLUDED.target_id, updated_at_utc = EXCLUDED.updated_at_utc;
                """;

            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("business_id", businessId == Guid.Empty ? DBNull.Value : businessId);
            command.Parameters.AddWithValue("collection", sourceCollection);
            command.Parameters.AddWithValue("legacy_id", sourceId);
            command.Parameters.AddWithValue("entity_type", sourceCollection);
            command.Parameters.AddWithValue("target_id", targetId);
            command.Parameters.AddWithValue("now", runTimestamp);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed record TableReport(string Table, int Rows, int Updated);
}
