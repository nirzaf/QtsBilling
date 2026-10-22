using QtsBilling.Contracts.Orders;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// One quote or order line as the screen holds it while it is being written (specification task 22, recipe step 1).
/// It is deliberately a different thing from the server's <see cref="QuoteLineRequest"/>: the operator's buffer can
/// hold an unfinished line, while the request is only ever built from a line that is complete, and the priced
/// document the server returns is a third thing again - the snapshot no screen may overwrite.
/// </summary>
public sealed class QuoteLineForm
{
    public Guid? VariantId { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; } = 1m;

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public Guid? TaxRateId { get; set; }

    /// <summary>Stored and sent as the server's own text value, so no second vocabulary appears.</summary>
    public string TaxMode { get; set; } = "Exclusive";

    /// <summary>True when a catalog variant was chosen; a written line is allowed but has to carry its own price.</summary>
    public bool HasVariant => VariantId is not null;

    public bool IsComplete
        => Quantity > 0m
            && (HasVariant || (!string.IsNullOrWhiteSpace(Description) && UnitPrice >= 0m));

    /// <summary>
    /// The server insists a catalog line carries the catalog price, rate and mode (a submitted override is refused
    /// with INVALID_PRICE), so a variant line sends none of them; a written line carries its own price and may
    /// choose an active rate (specification task 22, recipe step 1).
    /// </summary>
    public QuoteLineRequest ToRequest() => new(
        VariantId,
        string.IsNullOrWhiteSpace(Description) ? (VariantName ?? string.Empty) : Description.Trim(),
        Quantity,
        HasVariant ? null : UnitPrice,
        DiscountAmount,
        HasVariant ? null : TaxRateId,
        HasVariant ? null : TaxMode);

    /// <summary>The variant's name, kept only so a chosen line can be described back to the operator.</summary>
    public string? VariantName { get; set; }

    public static QuoteLineForm FromOrderLine(OrderLineDto line, decimal quantity) => new()
    {
        VariantId = line.VariantId,
        Description = line.Description,
        VariantName = line.Description,
        Quantity = quantity,
        UnitPrice = line.UnitPrice,
        DiscountAmount = line.DiscountAmount,
        TaxMode = line.TaxMode,
    };
}

/// <summary>
/// A quote being drafted. Dates are chosen as business-zone days by the screen and sent as the plain dates the
/// contract carries; the server decides the document date when one is left to it (task 22, recipe step 1).
/// </summary>
public sealed class QuoteDraftForm
{
    public Guid? LocationId { get; set; }

    public Guid? CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public DateOnly? QuoteDate { get; set; }

    public DateOnly ExpiryDate { get; set; }

    public DateOnly? PromisedDeliveryDate { get; set; }

    public string? Note { get; set; }

    public List<QuoteLineForm> Lines { get; } = [];

    public bool IsComplete
        => LocationId is not null
            && ExpiryDate.DayNumber > 0
            && (PromisedDeliveryDate is null || PromisedDeliveryDate >= QuoteDate)
            && (QuoteDate is null || ExpiryDate >= QuoteDate)
            && Lines.Any(line => line.IsComplete)
            && Lines.All(line => !line.IsComplete || line.Quantity > 0m);

    public CreateQuoteRequest ToCreateRequest() => new(
        LocationId!.Value,
        CustomerId,
        QuoteDate,
        ExpiryDate,
        PromisedDeliveryDate,
        Trim(Note),
        CompleteLines.Select(line => line.ToRequest()).ToArray());

    public UpdateQuoteRequest ToUpdateRequest(long expectedVersion) => new(
        expectedVersion,
        CustomerId,
        ExpiryDate,
        PromisedDeliveryDate,
        Trim(Note),
        CompleteLines.Select(line => line.ToRequest()).ToArray());

    public QuoteAmendRequest ToAmendRequest(long expectedVersion, string reason) => new(
        expectedVersion,
        ExpiryDate,
        PromisedDeliveryDate,
        reason.Trim(),
        CompleteLines.Select(line => line.ToRequest()).ToArray());

    /// <summary>Only whole, complete lines are sent; an unfinished row stays on the screen instead of becoming a 400.</summary>
    public IEnumerable<QuoteLineForm> CompleteLines => Lines.Where(line => line.IsComplete);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The three tax handlings a line can carry, named the way the server stores them.</summary>
public static class TaxModes
{
    public static readonly IReadOnlyList<string> All = ["Exclusive", "Inclusive", "Exempt"];

    /// <summary>What each one means for the money, stated once so no screen invents its own wording.</summary>
    public static string Sentence(string mode) => mode switch
    {
        "Inclusive" => "The price already includes the tax; the tax is the part of it the business owes.",
        "Exempt" => "No tax is charged on this line.",
        _ => "The tax is added on top of the price.",
    };
}
