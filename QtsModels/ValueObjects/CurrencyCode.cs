namespace QtsModels.ValueObjects;

/// <summary>
/// ISO 4217-style currency code (three ASCII letters, stored upper-case).
/// Rates and money must always be denominated in an explicit currency (specification §3.1).
/// </summary>
public readonly record struct CurrencyCode
{
    public const int CodeLength = 3;

    /// <summary>Upper-case ISO code, or <c>null</c> for <c>default(CurrencyCode)</c>.</summary>
    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    private CurrencyCode(string value) => Value = value;

    public static CurrencyCode Parse(string code)
    {
        if (!TryParse(code, out var result))
        {
            throw new FormatException($"'{code}' is not a valid three-letter currency code.");
        }

        return result;
    }

    public static bool TryParse(string? code, out CurrencyCode result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();
        if (trimmed.Length != CodeLength)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetter(character))
            {
                return false;
            }
        }

        result = new CurrencyCode(trimmed.ToUpperInvariant());
        return true;
    }

    /// <summary>Number of minor-unit digits used for rounding boundaries of this currency.</summary>
    public int MinorUnitDigits => GetMinorUnitDigits(Value);

    /// <summary>
    /// Minor-unit digits by currency. The default is 2; the table covers currencies with
    /// 0 or 3 digits that are materially different. Unknown codes fall back to 2 and must be
    /// validated against the business configuration before posting.
    /// </summary>
    public static int GetMinorUnitDigits(string isoCode) => isoCode switch
    {
        "JPY" or "KRW" or "VND" or "CLP" or "ISK" or "XOF" or "XAF" or "XPF" or "GNF" or "KMF" or "RWF" or "UGX" or "VUV" or "PYG" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "OMR" or "TND" or "LYD" => 3,
        _ => 2,
    };

    public override string ToString() => Value ?? string.Empty;
}
