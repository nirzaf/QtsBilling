using System.Text.Json;
using System.Text.Json.Serialization;

namespace QtsBilling.Contracts.Serialization;

/// <summary>
/// Shared JSON settings for every QtsBilling wire contract (server, browser client, MAUI).
/// UTC ISO 8601 timestamps, camel-case member names, no null noise, enums as camel-case strings
/// and decimal numbers serialized without a floating-point intermediary.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Options for offline payload fingerprints (canonical form is applied separately).</summary>
    public static JsonSerializerOptions FingerprintOptions { get; } = CreateOptions();

    /// <summary>
    /// Applies the shared wire contract to an existing options instance (for example the server's
    /// HTTP JSON options) so every producer and consumer of a contract agrees on the encoding.
    /// </summary>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.WriteIndented = false;
        options.NumberHandling = JsonNumberHandling.Strict;

        if (!options.Converters.Any(converter => converter is JsonStringEnumConverter))
        {
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }
}
