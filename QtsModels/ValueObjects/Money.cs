namespace QtsModels.ValueObjects;

/// <summary>
/// A decimal monetary amount with an explicit currency. Never derived from <see cref="double"/>.
/// Rounding happens exactly once at a documented boundary via <see cref="Round"/>.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public decimal Amount { get; }

    public CurrencyCode Currency { get; }

    public bool IsZero => Amount == 0m;

    public int Sign => decimal.Sign(Amount);

    private Money(decimal amount, CurrencyCode currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>Creates a monetary value without rounding; call <see cref="Round"/> at the posting/display boundary.</summary>
    public static Money Of(decimal amount, CurrencyCode currency)
    {
        if (currency.IsEmpty)
        {
            throw new InvalidOperationException("Money requires an explicit currency.");
        }

        return new Money(amount, currency);
    }

    /// <summary>
    /// Rounds to the currency's minor unit. Default is commercial rounding
    /// (<see cref="MidpointRounding.AwayFromZero"/>), applied at posting boundaries only.
    /// </summary>
    public Money Round(MidpointRounding mode = MidpointRounding.AwayFromZero)
        => new(decimal.Round(Amount, Currency.MinorUnitDigits, mode), Currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    /// <summary>Multiplies and rounds in one documented step.</summary>
    public Money Multiply(decimal factor, MidpointRounding mode = MidpointRounding.AwayFromZero)
        => new(decimal.Round(Amount * factor, Currency.MinorUnitDigits, mode), Currency);

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => $"{Amount.ToString(global::System.Globalization.CultureInfo.InvariantCulture)} {Currency}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot combine amounts in different currencies: '{left.Currency}' and '{right.Currency}'.");
        }
    }
}
