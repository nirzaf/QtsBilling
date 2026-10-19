using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QtsModels.ValueObjects;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Checked decimal ↔ scaled-integer converters used by the SQLite model. Money is never stored as
/// <c>REAL</c>: a decimal with more precision than the documented scale (or a value outside 64-bit
/// range) throws instead of truncating/saturating (specification 3.1/QB-17).
/// </summary>
public static class LocalValueConverters
{
    public static readonly ValueConverter<decimal, long> Money = new(
        value => ScaledDecimal.ToScaledMoney(value),
        scaled => ScaledDecimal.FromScaledMoney(scaled));

    public static readonly ValueConverter<decimal, long> Quantity = new(
        value => ScaledDecimal.ToScaledQuantity(value),
        scaled => ScaledDecimal.FromScaledQuantity(scaled));
}
