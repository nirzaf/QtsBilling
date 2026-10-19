using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QtsBilling.Contracts.Serialization;

/// <summary>
/// Deterministic JSON canonicalization used for sync-operation idempotency fingerprints
/// (specification §4.4: "Compare canonical request fingerprint; reject same key/different body").
/// </summary>
/// <remarks>
/// Canonical rules (v1, frozen — changing these rules invalidates stored fingerprints):
/// <list type="bullet">
/// <item>Objects: properties are re-emitted in ordinal name order; no insignificant whitespace.</item>
/// <item>Arrays: original order preserved.</item>
/// <item>Numbers: emitted from their decimal value with trailing zeros normalized, so
/// <c>1.0</c>, <c>1.00</c> and <c>1</c> produce identical output; integral values are emitted as integers.</item>
/// <item>Strings: standard JSON escaping via <see cref="Utf8JsonWriter"/>.</item>
/// <item>Nulls are preserved (omitting a null member is a different payload and is rejected as a
/// <c>IDEMPOTENCY_KEY_REUSED</c> style conflict rather than silently accepted).</item>
/// <item>Undefined values are rejected.</item>
/// </list>
/// </remarks>
public static class CanonicalJson
{
    private const decimal TrailingZeroNormalizer = 1.000000000000000000000000000000000m;

    /// <summary>Returns the canonical UTF-8 text of <paramref name="element"/>.</summary>
    public static string ToCanonicalString(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Returns the canonical UTF-8 bytes of <paramref name="element"/>.</summary>
    public static byte[] ToCanonicalUtf8(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Computes the lowercase SHA-256 hex fingerprint of the canonical form.</summary>
    public static string ComputeFingerprint(JsonElement element)
    {
        var bytes = ToCanonicalUtf8(element);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Computes the lowercase SHA-256 hex fingerprint of a canonical JSON string.</summary>
    public static string ComputeFingerprint(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Writes the canonical form of <paramref name="element"/>.</summary>
    public static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException(
                    $"Cannot canonicalize a JSON value of kind '{element.ValueKind}'.",
                    nameof(element));
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetDecimal(out var value))
        {
            var normalized = NormalizeDecimal(value);
            if (normalized >= long.MinValue
                && normalized <= long.MaxValue
                && normalized == decimal.Truncate(normalized))
            {
                writer.WriteNumberValue((long)normalized);
                return;
            }

            writer.WriteNumberValue(normalized);
            return;
        }

        // Exotic numeric values outside the decimal domain (e.g. 1e40): preserve the raw token.
        writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
    }

    private static decimal NormalizeDecimal(decimal value)
    {
        try
        {
            return value / TrailingZeroNormalizer;
        }
        catch (OverflowException)
        {
            return value;
        }
    }

    /// <summary>Culture-invariant formatting helper used by tests and diagnostics.</summary>
    public static string FormatDecimal(decimal value) => NormalizeDecimal(value).ToString(CultureInfo.InvariantCulture);
}
