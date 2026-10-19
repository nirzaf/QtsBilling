using QtsModels.ValueObjects;

namespace TestQtsBilling.ValueObjects;

public sealed class QuantityTests
{
    [Fact]
    public void Of_NormalizesToSixDecimals()
    {
        var quantity = Quantity.Of(1.123456789m);

        Assert.Equal(1.123457m, quantity.Amount);
    }

    [Fact]
    public void Multiply_KeepsPrecisionWithinScale()
    {
        var quantity = Quantity.Of(2.5m);

        Assert.Equal(6.25m, quantity.Multiply(2.5m).Amount);
    }

    [Fact]
    public void Subtraction_CanProduceNegativeQuantity()
    {
        var result = Quantity.Of(3m) - Quantity.Of(5m);

        Assert.True(result.IsNegative);
        Assert.Equal(-2m, result.Amount);
    }
}
