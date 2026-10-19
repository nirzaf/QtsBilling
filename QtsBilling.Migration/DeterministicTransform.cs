using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QtsBilling.Migration;

/// <summary>A row that could not be transformed, with the exact reason and the source id.</summary>
public sealed record QuarantineRecord(
    string Collection,
    string SourceId,
    string Reason,
    string Detail);

/// <summary>The result of transforming one collection.</summary>
public sealed record TransformResult(
    string TargetTable,
    IReadOnlyList<JsonObject> Rows,
    IReadOnlyList<QuarantineRecord> Quarantine);

/// <summary>
/// The deterministic transform (spec §6.1, step 5). The same source document always produces the same
/// target row: the target id is derived from the source identity, the numbers are parsed with the
/// invariant culture, the money keeps its documented precision and anything ambiguous is **quarantined**
/// with its source id instead of being repaired.
/// </summary>
public sealed class DeterministicTransform
{
    private const int MoneyScale = 4;
    private const int QuantityScale = 6;

    private readonly MigrationMapping _mapping;

    public DeterministicTransform(MigrationMapping mapping)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
    }

    /// <summary>Derives the stable target id of a source record (never a cast of a Mongo ObjectId).</summary>
    public Guid TargetId(string collection, string sourceId)
    {
        var seed = Encoding.UTF8.GetBytes(
            $"{_mapping.SourceEnvironment}|{collection}|{sourceId}|{_mapping.TransformationVersion}");

        var hash = SHA256.HashData(seed);
        var bytes = hash[..16];

        // RFC 4122 version 8 (custom) so the derivation is visible in the identifier.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }

    /// <summary>
    /// Transforms one collection. <paramref name="documents"/> are the raw source documents (as JSON
    /// objects). The result is ordered by the source id so two runs produce byte-identical output.
    /// </summary>
    public TransformResult Transform(string collection, IReadOnlyList<JsonObject> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var mapping = _mapping.Collections
            .FirstOrDefault(candidate => string.Equals(candidate.Collection, collection, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The mapping has no entry for the collection '{collection}'.");

        var rows = new List<JsonObject>();
        var quarantine = new List<QuarantineRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents.OrderBy(document => SourceId(document, mapping), StringComparer.Ordinal))
        {
            var sourceId = SourceId(document, mapping);

            if (string.IsNullOrWhiteSpace(sourceId))
            {
                quarantine.Add(new QuarantineRecord(collection, "<none>", "missing_source_id", "The document has no identity field."));
                continue;
            }

            if (!seen.Add(sourceId))
            {
                // A duplicate source id is a source problem: it is never merged or overwritten silently.
                quarantine.Add(new QuarantineRecord(collection, sourceId, "duplicate_source_id", "The same source id appears more than once."));
                continue;
            }

            var row = new JsonObject
            {
                ["id"] = TargetId(collection, sourceId).ToString("D"),
                ["__source_collection"] = collection,
                ["__source_id"] = sourceId,
            };

            var failed = false;

            foreach (var field in mapping.Fields)
            {
                if (string.Equals(field.Target, "__source_collection", StringComparison.Ordinal) ||
                    string.Equals(field.Target, "__source_id", StringComparison.Ordinal))
                {
                    continue;
                }

                var (value, error) = Convert(field, document, collection, sourceId);

                if (error is not null)
                {
                    quarantine.Add(new QuarantineRecord(collection, sourceId, error.Reason, error.Detail));
                    failed = true;
                    break;
                }

                row[field.Target] = value;
            }

            if (!failed)
            {
                rows.Add(row);
            }
        }

        return new TransformResult(mapping.TargetTable, rows, quarantine);
    }

    private static string SourceId(JsonObject document, CollectionMapping mapping)
        => document[mapping.SourceKey]?.ToString() ?? string.Empty;

    private static (JsonNode? Value, (string Reason, string Detail)? Error) Convert(
        FieldMapping field,
        JsonObject document,
        string collection,
        string sourceId)
    {
        var raw = Read(document, field.Source);

        if (raw is null)
        {
            if (field.Default is not null)
            {
                return (JsonValue.Create(field.Default), null);
            }

            if (field.Kind is FieldKind.Reference)
            {
                return (null, ("missing_reference", $"The source field '{field.Source}' is missing."));
            }

            return (null, ("missing_value", $"The source field '{field.Source}' is missing and the field has no default."));
        }

        switch (field.Kind)
        {
            case FieldKind.String:
                return (JsonValue.Create(raw.ToString()), null);

            case FieldKind.Int:
                return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                    ? (JsonValue.Create(intValue), null)
                    : (null, ("invalid_integer", $"'{field.Source}' is not an integer."));

            case FieldKind.Long:
                return long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                    ? (JsonValue.Create(longValue), null)
                    : (null, ("invalid_long", $"'{field.Source}' is not an integer."));

            case FieldKind.Decimal:
                return ToDecimal(raw, field.Source);

            case FieldKind.Bool:
                return raw.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.True => (JsonValue.Create(true), null),
                    System.Text.Json.JsonValueKind.False => (JsonValue.Create(false), null),
                    _ => (null, ("invalid_boolean", $"'{field.Source}' is not a boolean.")),
                };

            case FieldKind.DateTime:
                return ToDateTime(raw, field.Source);

            case FieldKind.Guid:
                return Guid.TryParse(raw.ToString(), out var guid)
                    ? (JsonValue.Create(guid.ToString("D")), null)
                    : (null, ("invalid_guid", $"'{field.Source}' is not a GUID."));

            case FieldKind.SourceId:
                return (JsonValue.Create(raw.ToString()), null);

            case FieldKind.Reference:
                var referenceId = raw.ToString();

                if (string.IsNullOrWhiteSpace(referenceId))
                {
                    return (null, ("missing_reference", $"'{field.Source}' is empty."));
                }

                if (field.References is null || field.ReferenceTarget is null)
                {
                    return (null, ("invalid_mapping", $"The field '{field.Target}' has no reference target."));
                }

                // The reference is resolved by the caller (the transform needs the referenced ids); until
                // then the raw source id is carried and the importer resolves it.
                return (JsonValue.Create(referenceId), null);

            default:
                return (null, ("invalid_mapping", $"The field '{field.Target}' has an unknown kind."));
        }
    }

    private static (JsonNode? Value, (string Reason, string Detail)? Error) ToDecimal(JsonNode raw, string source)
    {
        if (raw.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            if (!raw.TryGetDecimal(out var number))
            {
                return (null, ("invalid_decimal", $"'{source}' is not a decimal."));
            }

            return (JsonValue.Create(number), null);
        }

        var text = raw.ToString();

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return (null, ("invalid_decimal", $"'{source}' is not a decimal."));
        }

        return (JsonValue.Create(parsed), null);
    }

    private static (JsonNode? Value, (string Reason, string Detail)? Error) ToDateTime(JsonNode raw, string source)
    {
        if (raw.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var text = raw.GetValue<string>();

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return (JsonValue.Create(parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), null);
            }

            return (null, ("invalid_date", $"'{source}' is not a date."));
        }

        return (null, ("invalid_date", $"'{source}' is not a date."));
    }

    private static JsonNode? Read(JsonObject document, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonNode? current = document;

        foreach (var segment in segments)
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            if (!obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }
}
