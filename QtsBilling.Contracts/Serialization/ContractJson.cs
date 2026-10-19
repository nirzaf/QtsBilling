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

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
