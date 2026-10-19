namespace QtsModels.ValueObjects;

/// <summary>
/// How the tax is composed into the price of a commercial line (specification D03).
/// </summary>
public enum CommercialTaxMode
{
    /// <summary>No tax applies to the line.</summary>
    Exempt = 0,

    /// <summary>The tax is charged on top of the line value.</summary>
    Exclusive = 1,

    /// <summary>The line value already contains the tax.</summary>
    Inclusive = 2,
}

/// <summary>
/// The inputs of one commercial line. Every value is a snapshot taken at capture time; the server
/// re-validates the commercial policy and never trusts a submitted amount blindly.
/// </summary>
public sealed record CommercialLineInput
{
    /// <summary>The quantity, at most six decimals (the <c>numeric(18,6)</c> scale).</summary>
    public decimal Quantity { get; init; }

    /// <summary>The unit price, at most four decimals (the <c>numeric(18,4)</c> scale).</summary>
    public decimal UnitPrice { get; init; }

    /// <summary>How the tax is composed into the price.</summary>
    public CommercialTaxMode TaxMode { get; init; } = CommercialTaxMode.Exclusive;

    /// <summary>The tax rate as a fraction (0.15 = 15%).</summary>
    public decimal TaxRate { get; init; }

    /// <summary>The authorized discount on the line, at most the line gross amount.</summary>
    public decimal DiscountAmount { get; init; }

    /// <summary>The pricing policy version the line was captured against.</summary>
    public string? PricingPolicyVersion { get; init; }
}

/// <summary>
/// The exact amounts of one commercial line.
/// </summary>
/// <remarks>
/// The identity <c>RevenueBase + Tax = Payable</c> holds in every tax mode: for an exclusive line
/// the tax is added on top of the revenue base, for an inclusive line the revenue base is the
/// payable minus the tax that is already contained in it. A document that mixes modes therefore
/// still balances, and the journal credits the revenue base - never the tax-inclusive amount.
/// </remarks>
public sealed record CommercialLineAmounts(
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal RevenueBaseAmount,
    decimal TaxAmount,
    decimal PayableAmount,
    decimal TaxRate,
    CommercialTaxMode TaxMode,
    string? PricingPolicyVersion)
{
    /// <summary>The invariant every line satisfies: revenue base plus tax equals the payable amount.</summary>
    public bool Balances => RevenueBaseAmount + TaxAmount == PayableAmount;
}

/// <summary>The exact amounts of a whole document (a sale, an invoice or a quotation).</summary>
public sealed record CommercialDocumentAmounts(
    IReadOnlyList<CommercialLineAmounts> Lines,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal RevenueBaseAmount,
    decimal TaxAmount,
    decimal PayableAmount,
    decimal SettlementPayableAmount,
    decimal RoundingAmount)
{
    /// <summary>The invariant every document satisfies: revenue base plus tax equals the payable amount.</summary>
    public bool Balances => RevenueBaseAmount + TaxAmount == PayableAmount;
}

/// <summary>
/// Raised when a commercial amount cannot be computed exactly. The caller maps it to a validation
/// error; nothing is ever rounded silently to make an invalid input fit.
/// </summary>
public sealed class CommercialAmountException : Exception
{
    public CommercialAmountException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    /// <summary>The field the failure belongs to (for example <c>lines[2].quantity</c>).</summary>
    public string Field { get; }
}

/// <summary>
/// The one shared commercial calculation: pure, deterministic and identical on the server, in the
/// browser and on the Windows device (specification D03, R05).
/// </summary>
public static class CommercialAmounts
{
    /// <summary>The money scale of the ledger: four decimals.</summary>
    public const int MoneyScale = 4;

    /// <summary>The quantity scale of the ledger: six decimals.</summary>
    public const int QuantityScale = 6;

    /// <summary>The largest amount that fits the <c>numeric(18,4)</c> money column.</summary>
    public const decimal MaxMoneyAmount = 99_999_999_999_999.9999m;

    /// <summary>The largest quantity that fits the <c>numeric(18,6)</c> quantity column.</summary>
    public const decimal MaxQuantity = 999_999_999_999.999999m;

    /// <summary>Rounds money half away from zero (never the banker's rounding of the framework default).</summary>
    public static decimal RoundMoney(decimal amount) => decimal.Round(amount, MoneyScale, MidpointRounding.AwayFromZero);

    /// <summary>Rounds a quantity half away from zero to the six-decimal ledger scale.</summary>
    public static decimal RoundQuantity(decimal quantity) => decimal.Round(quantity, QuantityScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Computes one line exactly. The gross is <c>Round4(quantity × unit price)</c>; the discount is
    /// capped by the gross; the tax follows the mode; the payable is the amount the customer owes.
    /// </summary>
    /// <exception cref="CommercialAmountException">The input cannot be computed exactly.</exception>
    public static CommercialLineAmounts ComputeLine(CommercialLineInput input, string field = "line")
    {
        if (!Enum.IsDefined(input.TaxMode))
        {
            throw new CommercialAmountException(
                $"{field}.taxMode",
                $"The tax mode '{(int)input.TaxMode}' is not a supported value.");
        }

        if (input.Quantity <= 0m)
        {
            throw new CommercialAmountException($"{field}.quantity", "The quantity must be greater than zero.");
        }

        if (input.Quantity > MaxQuantity)
        {
            throw new CommercialAmountException($"{field}.quantity", "The quantity exceeds the supported range.");
        }

        if (HasMoreDecimals(input.Quantity, ScaledDecimal.QuantityScale))
        {
            throw new CommercialAmountException(
                $"{field}.quantity",
                $"The quantity carries more than {QuantityScale} decimal places; the value is rejected instead of being rounded.");
        }

        if (input.UnitPrice < 0m)
        {
            throw new CommercialAmountException($"{field}.unitPrice", "The unit price cannot be negative.");
        }

        if (input.UnitPrice > MaxMoneyAmount)
        {
            throw new CommercialAmountException($"{field}.unitPrice", "The unit price exceeds the supported range.");
        }

        if (HasMoreDecimals(input.UnitPrice, ScaledDecimal.MoneyScale))
        {
            throw new CommercialAmountException(
                $"{field}.unitPrice",
                $"The unit price carries more than {MoneyScale} decimal places; the value is rejected instead of being rounded.");
        }

        if (input.TaxRate < 0m || input.TaxRate >= 1m)
        {
            throw new CommercialAmountException(
                $"{field}.taxRate",
                "The tax rate must be at least 0 and below 1 (100%); a rate of 1 or more is not a supported business rule.");
        }

        if (HasMoreDecimals(input.TaxRate, ScaledDecimal.QuantityScale))
        {
            throw new CommercialAmountException(
                $"{field}.taxRate",
                $"The tax rate carries more than {QuantityScale} decimal places.");
        }

        decimal gross;

        try
        {
            gross = RoundMoney(checked(input.Quantity * input.UnitPrice));
        }
        catch (OverflowException)
        {
            throw new CommercialAmountException($"{field}.quantity", "The line amount exceeds the supported range.");
        }

        if (input.DiscountAmount < 0m)
        {
            throw new CommercialAmountException($"{field}.discountAmount", "The discount cannot be negative.");
        }

        if (HasMoreDecimals(input.DiscountAmount, ScaledDecimal.MoneyScale))
        {
            throw new CommercialAmountException(
                $"{field}.discountAmount",
                $"The discount carries more than {MoneyScale} decimal places; the value is rejected instead of being rounded.");
        }

        if (input.DiscountAmount > gross)
        {
            throw new CommercialAmountException(
                $"{field}.discountAmount",
                "The discount cannot exceed the line gross amount.");
        }

        var discount = RoundMoney(input.DiscountAmount);
        var rate = input.TaxRate;

        decimal revenueBase;
        decimal tax;
        decimal payable;

        switch (input.TaxMode)
        {
            case CommercialTaxMode.Exempt:
                revenueBase = gross - discount;
                tax = 0m;
                payable = revenueBase;
                break;

            case CommercialTaxMode.Exclusive:
                revenueBase = gross - discount;
                tax = RoundMoney(revenueBase * rate);
                payable = revenueBase + tax;
                break;

            case CommercialTaxMode.Inclusive:
                payable = gross - discount;
                tax = RoundMoney(payable * rate / (1m + rate));
                revenueBase = payable - tax;
                break;

            default:
                throw new CommercialAmountException($"{field}.taxMode", "The tax mode is not supported.");
        }

        if (payable > MaxMoneyAmount || revenueBase > MaxMoneyAmount)
        {
            throw new CommercialAmountException($"{field}.quantity", "The line amount exceeds the supported range.");
        }

        return new CommercialLineAmounts(
            gross,
            discount,
            revenueBase,
            tax,
            payable,
            rate,
            input.TaxMode,
            input.PricingPolicyVersion);
    }

    /// <summary>
    /// Computes a whole document: the header totals are the sums of the already-rounded lines, and the
    /// payable is rounded once to the configured settlement scale. The difference between the sum of the
    /// line payables and the settlement payable is the explicit rounding amount - it is never hidden in
    /// the tax or in the inventory.
    /// </summary>
    /// <exception cref="CommercialAmountException">The input cannot be computed exactly.</exception>
    public static CommercialDocumentAmounts ComputeDocument(
        IReadOnlyList<CommercialLineInput> lines,
        int settlementScale = MoneyScale,
        string fieldPrefix = "lines")
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (settlementScale is < 0 or > MoneyScale)
        {
            throw new CommercialAmountException(
                $"{fieldPrefix}.settlementScale",
                $"The settlement scale must be between 0 and {MoneyScale} decimal places.");
        }

        var computed = new List<CommercialLineAmounts>(lines.Count);

        for (var index = 0; index < lines.Count; index++)
        {
            computed.Add(ComputeLine(lines[index], $"{fieldPrefix}[{index}]"));
        }

        var gross = Sum(computed.Select(line => line.GrossAmount));
        var discount = Sum(computed.Select(line => line.DiscountAmount));
        var revenueBase = Sum(computed.Select(line => line.RevenueBaseAmount));
        var tax = Sum(computed.Select(line => line.TaxAmount));
        var payable = Sum(computed.Select(line => line.PayableAmount));

        var settlementPayable = decimal.Round(payable, settlementScale, MidpointRounding.AwayFromZero);
        var rounding = settlementPayable - payable;

        return new CommercialDocumentAmounts(
            computed,
            gross,
            discount,
            revenueBase,
            tax,
            payable,
            settlementPayable,
            rounding);
    }

    /// <summary>
    /// Allocates a header discount across the eligible lines proportionally to their pre-discount
    /// amount, in whole money units, and distributes the remainder deterministically by line ordinal.
    /// </summary>
    /// <exception cref="CommercialAmountException">The allocation cannot be computed exactly.</exception>
    public static IReadOnlyList<decimal> AllocateHeaderDiscount(
        IReadOnlyList<decimal> grossAmounts,
        decimal headerDiscount,
        string fieldPrefix = "lines")
    {
        ArgumentNullException.ThrowIfNull(grossAmounts);

        if (grossAmounts.Count == 0)
        {
            return [];
        }

        if (headerDiscount < 0m)
        {
            throw new CommercialAmountException($"{fieldPrefix}.discountAmount", "The discount cannot be negative.");
        }

        if (HasMoreDecimals(headerDiscount, ScaledDecimal.MoneyScale))
        {
            throw new CommercialAmountException(
                $"{fieldPrefix}.discountAmount",
                $"The discount carries more than {MoneyScale} decimal places.");
        }

        var total = Sum(grossAmounts);

        if (headerDiscount > total)
        {
            throw new CommercialAmountException(
                $"{fieldPrefix}.discountAmount",
                "The header discount cannot exceed the sum of the line gross amounts.");
        }

        var target = (long)Math.Round(headerDiscount * ScaledDecimal.MoneyScale, MidpointRounding.AwayFromZero);
        var scaled = grossAmounts
            .Select(amount => (long)Math.Round(amount * ScaledDecimal.MoneyScale, MidpointRounding.AwayFromZero))
            .ToArray();
        var scaledTotal = scaled.Sum();

        if (scaledTotal == 0)
        {
            return grossAmounts.Select(_ => 0m).ToArray();
        }

        var allocated = new long[grossAmounts.Count];
        var remainders = new long[grossAmounts.Count];

        for (var index = 0; index < scaled.Length; index++)
        {
            var exact = target * scaled[index];
            allocated[index] = exact / scaledTotal;
            remainders[index] = exact % scaledTotal;
        }

        var outstanding = target - allocated.Sum();

        // The remainder goes to the lines with the largest fractional part; equal parts are broken by
        // the line ordinal, so the allocation is reproducible.
        var order = Enumerable.Range(0, scaled.Length)
            .OrderByDescending(index => remainders[index])
            .ThenBy(index => index)
            .ToArray();

        for (var position = 0; outstanding > 0; position++, outstanding--)
        {
            allocated[order[position % order.Length]]++;
        }

        return allocated.Select(value => ScaledDecimal.FromScaledMoney(value)).ToArray();
    }

    /// <summary>True when the value carries more decimals than the scale factor allows (10,000 = four decimals).</summary>
    public static bool HasMoreDecimals(decimal value, int scaleFactor)
    {
        var scaled = value * scaleFactor;

        return scaled != decimal.Truncate(scaled);
    }

    private static decimal Sum(IEnumerable<decimal> amounts)
    {
        decimal total = 0m;

        foreach (var amount in amounts)
        {
            total += amount;
        }

        return RoundMoney(total);
    }
}
