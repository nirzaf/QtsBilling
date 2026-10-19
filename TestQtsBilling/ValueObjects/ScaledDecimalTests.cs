using QtsModels.ValueObjects;

namespace TestQtsBilling.ValueObjects;

/// <summary>
/// QB-02/§3.1 overflow policy tests: scaled-integer conversion must reject excess precision and
/// overflow instead of truncating or saturating.
/// </summary>
public sealed class ScaledDecimalTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 10_000)]
    [InlineData(-1, -10_000)]
    [InlineData(123.4567, 1_234_567)]
    [InlineData(-0.0001, -1)]
    public void ToScaledMoney_ConvertsExactly(decimal amount, long expected)
    {
        Assert.Equal(expected, ScaledDecimal.ToScaledMoney(amount));
    }

    [Fact]
    public void ToScaledMoney_RejectsExcessPrecision()
    {
        Assert.Throws<InvalidOperationException>(() => ScaledDecimal.ToScaledMoney(0.00005m));
    }

    [Fact]
    public void ToScaledMoney_RejectsOverflow()
    {
        Assert.Throws<OverflowException>(() => ScaledDecimal.ToScaledMoney(1_000_000_000_000_000m));
    }

    [Fact]
    public void ToScaledQuantity_AcceptsSixDecimals()
    {
        Assert.Equal(1_234_567, ScaledDecimal.ToScaledQuantity(1.234567m));
    }

    [Fact]
    public void ToScaledQuantity_RejectsSevenDecimals()
    {
        Assert.Throws<InvalidOperationException>(() => ScaledDecimal.ToScaledQuantity(0.0000001m));
    }

    [Theory]
    [InlineData(123.4567)]
    [InlineData(-0.0001)]
    [InlineData(999_999_999.9999)]
    public void RoundTrips_AreLossless(decimal amount)
    {
        Assert.Equal(amount, ScaledDecimal.FromScaledMoney(ScaledDecimal.ToScaledMoney(amount)));
    }
}
