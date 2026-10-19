namespace QtsModels.ValueObjects;

/// <summary>
/// Checked, exact conversions between <see cref="decimal"/> and the signed 64-bit scaled integers
/// used by the local SQLite store (specification §3.1: money <c>INTEGER</c> at scale 10,000;
/// quantity at scale 1,000,000 — reject rather than truncate or saturate).
/// </summary>
public static class ScaledDecimal
{
    /// <summary>Scale for <c>numeric(18,4)</c> money: four decimal places.</summary>
    public const int MoneyScale = 10_000;

    /// <summary>Scale for <c>numeric(18,6)</c> quantities: six decimal places.</summary>
    public const int QuantityScale = 1_000_000;

    public static long ToScaledMoney(decimal amount) => ToScaled(amount, MoneyScale);

    public static long ToScaledQuantity(decimal quantity) => ToScaled(quantity, QuantityScale);

    public static decimal FromScaledMoney(long scaled) => FromScaled(scaled, MoneyScale);

    public static decimal FromScaledQuantity(long scaled) => FromScaled(scaled, QuantityScale);

    /// <summary>
    /// Converts <paramref name="value"/> to an exact scaled integer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value carries more precision than the scale.</exception>
    /// <exception cref="OverflowException">The scaled value does not fit in <see cref="long"/>.</exception>
    public static long ToScaled(decimal value, int scale)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive.");
        }

        decimal scaled;
        try
        {
            scaled = value * scale;
        }
        catch (OverflowException)
        {
            throw new OverflowException(
                $"Value {value} cannot be represented at scale 1/{scale} (scaled magnitude exceeds 64-bit range).");
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw new InvalidOperationException(
                $"Value {value} has more precision than 1/{scale}; round at a documented boundary before persisting.");
        }

        if (scaled > long.MaxValue || scaled < long.MinValue)
        {
            throw new OverflowException(
                $"Value {value} exceeds the signed 64-bit scaled range at 1/{scale}.");
        }

        return (long)scaled;
    }

    /// <summary>Converts a scaled integer back to <see cref="decimal"/> (always exact for these scales).</summary>
    public static decimal FromScaled(long scaled, int scale)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive.");
        }

        return (decimal)scaled / scale;
    }
}
