using QtsModels.ValueObjects;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class ScaledDecimalConverterTests
{
    [Fact]
    public void Money_and_quantity_round_trip_exactly()
    {
        Assert.Equal(0m, ScaledDecimal.FromScaledMoney(ScaledDecimal.ToScaledMoney(0m)));
        Assert.Equal(9.99m, ScaledDecimal.FromScaledMoney(ScaledDecimal.ToScaledMoney(9.99m)));
        Assert.Equal(123456.7891m, ScaledDecimal.FromScaledMoney(ScaledDecimal.ToScaledMoney(123456.7891m)));
        Assert.Equal(-1234.5678m, ScaledDecimal.FromScaledMoney(ScaledDecimal.ToScaledMoney(-1234.5678m)));

        Assert.Equal(1_000_000L, ScaledDecimal.ToScaledQuantity(1m));
        Assert.Equal(2.5m, ScaledDecimal.FromScaledQuantity(ScaledDecimal.ToScaledQuantity(2.5m)));
        Assert.Equal(0.000001m, ScaledDecimal.FromScaledQuantity(ScaledDecimal.ToScaledQuantity(0.000001m)));
    }

    [Fact]
    public void More_precision_than_the_scale_is_rejected_not_truncated()
    {
        Assert.Throws<InvalidOperationException>(() => ScaledDecimal.ToScaledMoney(0.00001m));
        Assert.Throws<InvalidOperationException>(() => ScaledDecimal.ToScaledQuantity(0.0000001m));
    }

    [Fact]
    public void Values_outside_the_64_bit_scaled_range_are_rejected()
    {
        Assert.Throws<OverflowException>(() => ScaledDecimal.ToScaledMoney(decimal.MaxValue));
        Assert.Throws<OverflowException>(() => ScaledDecimal.ToScaledMoney(decimal.MinValue));
        Assert.Throws<OverflowException>(() => ScaledDecimal.ToScaledQuantity(long.MaxValue));
    }
}
