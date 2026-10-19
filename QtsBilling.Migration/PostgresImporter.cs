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
    int Quarantined,
    IReadOnlyList<QuarantineRecord> Quarantine);

/// <summary>What one target table received.</summary>
public sealed record TableImportReport(string Table, int Rows, int Updated);

/// <summary>
/// The idempotent load (spec Â§6.1, step 6). Every target row is upserted against the stable derived id,
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
        var quarantine = new List<QuarantineRecord>();
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

                    if (!columns.ContainsKey(column))
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

                    values[field.Target] = JsonValue.Create(targetId.ToString("D"));

                    if (string.Equals(field.Target, "business_id", StringComparison.OrdinalIgnoreCase))
                    {
                        businessId = targetId;
                    }
                }

                values["id"] = row["id"]!;
                values["version"] = JsonValue.Create(1);
                values["created_at_utc"] = JsonValue.Create(runTimestamp.UtcDateTime.ToString("O"));
                values["updated_at_utc"] = JsonValue.Create(runTimestamp.UtcDateTime.ToString("O"));

                if (columns.ContainsKey("is_active") && !values.ContainsKey("is_active"))
                {
                    values["is_active"] = JsonValue.Create(true);
                }

                bool inserted;
                bool existed;

                try
                {
                    (inserted, existed) = await UpsertAsync(db, table.TargetTable, values, columns, cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
                {
                    // The row collides with an existing record: it is quarantined with its source id and the
                    // import continues. It is never merged or overwritten silently.
                    quarantine.Add(new QuarantineRecord(
                        table.TargetTable,
                        row["__source_id"]!.ToString()!,
                        exception.SqlState == PostgresErrorCodes.UniqueViolation ? "unique_violation" : "foreign_key_violation",
                        exception.MessageText));

                    continue;
                }

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
            quarantined,
            quarantine);
    }

    /// <summary>Converts a transformed value into the CLR type the target column expects.</summary>
    private static object? ToParameterValue(string dataType, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var text = node.ToString();

        return dataType switch
        {
            "uuid" => Guid.Parse(text),
            "numeric" or "decimal" => decimal.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
            "boolean" => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            "integer" => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "bigint" => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "timestamp with time zone" => DateTime.Parse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            _ => text,
        };
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
    private static async Task<IReadOnlyDictionary<string, string>> LoadColumnsAsync(
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
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table;
                """;
            AddParameter(command, "table", table);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns[reader.GetString(0)] = reader.GetString(1);
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
        IReadOnlyDictionary<string, string> columns,
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
            var columnNames = values.Keys.ToArray();
            var parameters = new List<string>(columnNames.Length);
            var arguments = new List<object?>(columnNames.Length);

            for (var index = 0; index < columnNames.Length; index++)
            {
                parameters.Add($"@p{index}");
                arguments.Add(values[columnNames[index]]);
            }

            var updatable = columnNames
                .Where(column => !string.Equals(column, "id", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var sql = $"""
                INSERT INTO {table} ({string.Join(", ", columnNames)})
                VALUES ({string.Join(", ", parameters)})
                ON CONFLICT (id) DO UPDATE SET {string.Join(", ", updatable.Select(column => $"{column} = EXCLUDED.{column}"))}
                RETURNING (xmax = 0) AS inserted;
                """;

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (var index = 0; index < columnNames.Length; index++)
            {
                var dataType = columns.TryGetValue(columnNames[index], out var type) ? type : "text";
                AddParameter(command, $"p{index}", ToParameterValue(dataType, arguments[index] as JsonNode) ?? arguments[index] ?? DBNull.Value);
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

    private static async Task<Guid?> ReadIdentifierMapAsync(
        BillingDbContext db,
        string sourceCollection,
        string sourceId,
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
                SELECT target_id
                FROM legacy_identifier_map
                WHERE source_collection = @collection AND legacy_id = @legacy_id;
                """;

            AddParameter(command, "collection", sourceCollection);
            AddParameter(command, "legacy_id", sourceId);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is Guid guid ? guid : null;
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

                """;

            AddParameter(command, "id", Guid.CreateVersion7());
            AddParameter(command, "business_id", businessId == Guid.Empty ? DBNull.Value : businessId);
            AddParameter(command, "collection", sourceCollection);
            AddParameter(command, "legacy_id", sourceId);
            AddParameter(command, "entity_type", sourceCollection);
            AddParameter(command, "target_id", targetId);
            AddParameter(command, "now", runTimestamp);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // A concurrent run mapped the same source id first: the mapping is the same by construction.
                return false;
            }
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
