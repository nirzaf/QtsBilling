using QtsModels.ValueObjects;

namespace TestQtsBilling.ValueObjects;

/// <summary>QB-02 value-object tests: exact decimal money semantics, no binary floating point.</summary>
public sealed class MoneyTests
{
    private static readonly CurrencyCode Qar = CurrencyCode.Parse("qar");

    [Theory]
    [InlineData("USD")]
    [InlineData("QAR")]
    [InlineData("jpy")]
    public void Parse_AcceptsThreeLetterAsciiCodes_CaseInsensitive(string code)
    {
        var currency = CurrencyCode.Parse(code);

        Assert.Equal(code.ToUpperInvariant(), currency.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("USDD")]
    public void TryParse_RejectsMalformedCodes(string code)
    {
        Assert.False(CurrencyCode.TryParse(code, out _));
    }

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("QAR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    public void MinorUnitDigits_FollowsCurrencyTable(string code, int expected)
    {
        Assert.Equal(expected, CurrencyCode.Parse(code).MinorUnitDigits);
    }

    [Fact]
    public void Of_RequiresExplicitCurrency()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Of(10m, default));
    }

    [Fact]
    public void Addition_IsExact()
    {
        var first = Money.Of(0.1m, Qar);
        var second = Money.Of(0.2m, Qar);

        Assert.Equal(0.3m, (first + second).Amount);
    }

    [Fact]
    public void Addition_RejectsMixedCurrencies()
    {
        var qar = Money.Of(1m, Qar);
        var usd = Money.Of(1m, CurrencyCode.Parse("USD"));

        Assert.Throws<InvalidOperationException>(() => qar + usd);
    }

    [Fact]
    public void Round_AppliesCommercialRoundingAtMinorUnits()
    {
        var amount = Money.Of(10.125m, Qar);

        Assert.Equal(10.13m, amount.Round().Amount);
    }

    [Fact]
    public void Round_UsesZeroDigitsForJpy()
    {
        var amount = Money.Of(10.5m, CurrencyCode.Parse("JPY"));

        Assert.Equal(11m, amount.Round().Amount);
    }
}
