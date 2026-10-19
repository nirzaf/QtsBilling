using QtsModels.ValueObjects;

namespace QtsBilling.Contracts.Pos;

/// <summary>
/// The typed facade over the shared commercial calculation (specification D03). The server, the browser
/// and the Windows device all call this one implementation, so the same commercial inputs always
/// produce the same amounts. It contains no arithmetic of its own.
/// </summary>
public static class CommercialAmountCalculator
{
    /// <summary>Computes one line exactly, or throws a commercial amount error.</summary>
    public static CommercialLineAmounts ComputeLine(
        decimal quantity,
        decimal unitPrice,
        CommercialTaxMode taxMode,
        decimal taxRate,
        decimal discountAmount,
        string? pricingPolicyVersion = null,
        string field = "line")
        => CommercialAmounts.ComputeLine(
            new CommercialLineInput
            {
                Quantity = quantity,
                UnitPrice = unitPrice,
                TaxMode = taxMode,
                TaxRate = taxRate,
                DiscountAmount = discountAmount,
                PricingPolicyVersion = pricingPolicyVersion,
            },
            field);

    /// <summary>Computes a whole document: the header totals are the sums of the already-rounded lines.</summary>
    public static CommercialDocumentAmounts ComputeDocument(
        IReadOnlyList<CommercialLineInput> lines,
        int settlementScale = CommercialAmounts.MoneyScale,
        string fieldPrefix = "lines")
        => CommercialAmounts.ComputeDocument(lines, settlementScale, fieldPrefix);

    /// <summary>
    /// Reads the tax mode of a catalog item or a captured line. An unknown value is rejected instead of
    /// being coerced into a default: the tax composition changes the money, so it is never guessed.
    /// </summary>
    public static CommercialTaxMode ParseTaxMode(string? value, string field = "taxMode")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CommercialTaxMode.Exclusive;
        }

        if (!Enum.TryParse<CommercialTaxMode>(value.Trim(), ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new CommercialAmountException(field, $"The tax mode '{value}' is not supported.");
        }

        return mode;
    }

    /// <summary>The wire name of a tax mode (used by the device cache and the wire contracts).</summary>
    public static string TaxModeName(CommercialTaxMode mode) => mode.ToString();
}
