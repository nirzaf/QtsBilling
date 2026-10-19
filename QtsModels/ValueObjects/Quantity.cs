namespace QtsModels.ValueObjects;

/// <summary>
/// A stock/service quantity with a fixed six-decimal precision matching
/// <c>numeric(18,6)</c> server-side and scaled integers locally (specification §3.1).
/// Values are normalized to the scale at construction; binary floating point is never used.
/// </summary>
public readonly record struct Quantity : IComparable<Quantity>
{
    public const int Scale = 6;

    public static readonly Quantity Zero = new(0m);

    public decimal Amount { get; }

    private Quantity(decimal amount) => Amount = amount;

    /// <summary>Creates a quantity, rounding to six decimals at the documented construction boundary.</summary>
    public static Quantity Of(decimal amount) => new(decimal.Round(amount, Scale, MidpointRounding.AwayFromZero));

    public int Sign => decimal.Sign(Amount);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Amount + right.Amount);

    public static Quantity operator -(Quantity left, Quantity right) => new(left.Amount - right.Amount);

    public Quantity Negate() => new(-Amount);

    public Quantity Multiply(decimal factor) => new(decimal.Round(Amount * factor, Scale, MidpointRounding.AwayFromZero));

    public int CompareTo(Quantity other) => Amount.CompareTo(other.Amount);

    public override string ToString() => Amount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
}
