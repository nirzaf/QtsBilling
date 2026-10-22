namespace QtsBilling.Ui.Pages.Catalog;

/// <summary>
/// One variant price read three ways, so the number an operator typed is never mistaken for the number the
/// customer pays: <see cref="Base"/> is what the line is worth before tax, <see cref="Tax"/> what the rate adds,
/// and <see cref="Gross"/> the till total.
/// </summary>
/// <param name="Base">The untaxed value of one unit.</param>
/// <param name="Tax">The tax one unit carries at the chosen rate.</param>
/// <param name="Gross">What one unit costs the customer.</param>
/// <param name="Typed">Which of the two the operator actually entered - the field holds that one.</param>
/// <param name="Sentence">The plain-language reading shown beside the fields.</param>
public sealed record PricePreview(
    decimal Base,
    decimal Tax,
    decimal Gross,
    decimal Typed,
    string Sentence);

/// <summary>
/// The few decisions the catalog workbench makes for itself (specification task 18, recipe steps 2 and 3). Every
/// rule that changes a record - unique keys, the version guard, which references exist, what a movement protects -
/// stays on the server; these are the sentences and the arithmetic a screen needs to explain the server's answer.
/// </summary>
public static class CatalogRules
{
    /// <summary>
    /// The scale a preview is stated at. The posted line is rounded by the business's own settlement scale when
    /// the document is saved (task 24 widens this screen to read that scale), so the preview says which scale it
    /// used instead of implying it is the invoice's arithmetic.
    /// </summary>
    public const int PreviewDecimals = 2;

    public const string TaxInclusive = "Inclusive";
    public const string TaxExclusive = "Exclusive";

    /// <summary>Splits a typed price into base, tax and gross exactly the way an exclusive or inclusive line reads.</summary>
    public static PricePreview Preview(decimal sellingPrice, decimal taxRate, string? taxType)
    {
        var price = sellingPrice < 0m ? 0m : sellingPrice;
        var rate = taxRate is < 0m or > 10m ? 0m : taxRate;
        var inclusive = string.Equals(taxType, TaxInclusive, StringComparison.OrdinalIgnoreCase);

        if (inclusive)
        {
            // The typed number is what the customer pays, so the tax has to be taken out of it.
            var baseAmount = Round(price / (1m + rate));

            return new PricePreview(
                baseAmount,
                Round(price - baseAmount),
                price,
                price,
                $"{Money(price)} includes {Money(price - baseAmount)} tax at {Percent(rate)}; the line is worth {Money(baseAmount)} before tax.");
        }

        var tax = Round(price * rate);

        return new PricePreview(
            price,
            tax,
            Round(price + tax),
            price,
            $"{Money(price)} plus {Percent(rate)} tax is {Money(tax)}, so one unit costs the customer {Money(price + tax)}.");
    }

    /// <summary>How the tax mode reads to somebody who has the paper in front of them.</summary>
    public static string TaxModeText(string? taxType)
        => string.Equals(taxType, TaxInclusive, StringComparison.OrdinalIgnoreCase)
            ? "Tax is already inside the price"
            : "Tax is added to the price";

    /// <summary>The two catalog kinds this product supports, in the operator's words (specification §1.3).</summary>
    public static string StockKindText(bool trackStock)
        => trackStock
            ? "Stock item - quantity on hand is tracked per branch"
            : "Service or non-stocked item - nothing is counted out of a shelf when it is sold";

    /// <summary>
    /// What costing method applies. The product holds a purchase price; the value of what is on the shelf is the
    /// business-wide moving average the inventory ledger maintains, so this is stated, never chosen here.
    /// </summary>
    public static string CostingText(bool trackStock)
        => trackStock
            ? "Costed at the moving average the inventory ledger carries; the purchase price below is the opening rate a receipt starts from."
            : "No stock cost: nothing is valued on the shelf.";

    /// <summary>Where a quantity is corrected, because the catalog screen must not become a second way to move stock.</summary>
    public const string CorrectionRoute =
        "Quantities are corrected through stock counts and branch transfers, never from this screen: a count is reviewed and a transfer names both branches.";

    /// <summary>What the barcode field expects, stated so a keyboard wedge is not guessed at.</summary>
    public const string BarcodeHelp =
        "Type or scan with a keyboard wedge. A scanner ends the code with Enter, which saves this variant; nothing here opens a camera or a network service.";

    /// <summary>True when retiring this variant would leave the product with no default the counter can sell.</summary>
    public static bool NeedsReplacementDefault(bool isDefault, bool anotherActiveVariantExists)
        => isDefault && anotherActiveVariantExists;

    /// <summary>The sentence shown when a replacement default must be chosen before the default can be retired.</summary>
    public static string ReplacementDefaultReason(bool isDefault)
        => isDefault
            ? "This is the default variant, so choose which active variant becomes the default before retiring it."
            : "Retiring this variant leaves the product's default untouched.";

    /// <summary>The refusal a screen shows when the version it holds is no longer the stored one.</summary>
    public const string StaleNote =
        "Somebody else saved this record first. What you typed is still here - reload it, then apply the change again against the version you can now see.";

    /// <summary>The refusal a duplicate key produces, naming what is already taken.</summary>
    public static string DuplicateKeyNote(string message)
        => $"The server refused this key because another record already holds it. {message} Change it, or reload the list to find the record that took it.";

    /// <summary>
    /// The sentence a catalog screen shows for a refusal, keeping each different 409 a different answer: a key that
    /// is already taken says change the key, a version that has moved says reload, and a protected semantic says
    /// the server's own reason (specification task 13's rule that a refusal is never dressed as a success, and task
    /// 18's T18.02, where the operator must be able to recover without losing what was typed).
    /// </summary>
    public static string RefusalNote(QtsBilling.Contracts.Abstractions.ApiException exception)
    {
        var details = exception.Errors.Count == 0
            ? exception.Message
            : string.Join(" ", [exception.Message, .. exception.Errors.Select(pair => $"{pair.Key}: {string.Join(" ", pair.Value)}")]);

        if (!exception.IsConflict)
        {
            return details;
        }

        return exception.Code switch
        {
            QtsBilling.Contracts.Abstractions.ApiErrorCodes.OptimisticConcurrencyFailure => StaleNote + " " + details,
            QtsBilling.Contracts.Abstractions.ApiErrorCodes.DuplicateSku
                or QtsBilling.Contracts.Abstractions.ApiErrorCodes.DuplicateBarcode
                or QtsBilling.Contracts.Abstractions.ApiErrorCodes.DuplicateName => DuplicateKeyNote(details),
            _ => details,
        };
    }

    /// <summary>
    /// What an archive discloses before it is confirmed (task 18, recipe step 3). Only the quantity the screen has
    /// actually read is stated; the rest is named as staying on the shelf rather than being summed from figures
    /// this screen never fetched.
    /// </summary>
    public static string ArchiveSentence(string name, int activeVariants, string? selectedVariant, decimal selectedOnHand, bool trackStock)
    {
        var stock = !trackStock
            ? string.Empty
            : selectedVariant is { } variant
                ? $", and {Money(selectedOnHand)} unit(s) of {variant} are still held in the branches listed above"
                : ", and whatever is on the shelf stays counted there";

        return $"Taking \"{name}\" out of service stops it being chosen for new work. "
            + $"{activeVariants} active variant(s) go with it{stock}. "
            + "Nothing is removed from the shelf, no posted line is re-priced, and no record is erased.";
    }

    /// <summary>What a restore re-enables, stated narrowly.</summary>
    public static string RestoreSentence(int activeVariants)
        => activeVariants > 0
            ? "This product is already offering at least one variant, so restoring it changes only the product itself."
            : "Restoring brings back the default variant only, and nothing else: any other retired variant stays retired until it is restored deliberately.";

    /// <summary>The version line a successful save leaves visible (task 18, recipe step 4).</summary>
    public static string CommittedVersion(long version, string what)
        => $"{what} saved as version {version}.";

    public static string Percent(decimal rate) => $"{Round(rate * 100m, 4):0.####}%";

    public static string Money(decimal value) => Round(value).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static decimal Round(decimal value) => Round(value, PreviewDecimals);

    public static decimal Round(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
}
