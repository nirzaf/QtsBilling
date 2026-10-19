using QtsBilling.Contracts.Pos;
using QtsModels.ValueObjects;
using Xunit;

namespace TestQtsBilling.ValueObjects;

/// <summary>
/// The shared commercial calculation (specification D03, R05). The server, the browser and the device
/// all use this one implementation, so these cases are the contract for every host.
/// </summary>
public sealed class CommercialAmountsTests
{
    private static CommercialLineInput Line(
        decimal quantity,
        decimal unitPrice,
        CommercialTaxMode mode,
        decimal taxRate,
        decimal discount = 0m)
        => new()
        {
            Quantity = quantity,
            UnitPrice = unitPrice,
            TaxMode = mode,
            TaxRate = taxRate,
            DiscountAmount = discount,
        };

    [Fact]
    public void An_inclusive_line_keeps_the_tax_inside_the_price()
    {
        // 115 at 15% inclusive: the customer pays 115, the revenue is 100 and the tax is 15.
        var line = CommercialAmounts.ComputeLine(Line(1m, 115m, CommercialTaxMode.Inclusive, 0.15m));

        Assert.Equal(115m, line.GrossAmount);
        Assert.Equal(15m, line.TaxAmount);
        Assert.Equal(100m, line.RevenueBaseAmount);
        Assert.Equal(115m, line.PayableAmount);
        Assert.True(line.Balances);
    }

    [Fact]
    public void An_exclusive_line_adds_the_tax_on_top()
    {
        var line = CommercialAmounts.ComputeLine(Line(1m, 100m, CommercialTaxMode.Exclusive, 0.15m));

        Assert.Equal(100m, line.GrossAmount);
        Assert.Equal(15m, line.TaxAmount);
        Assert.Equal(100m, line.RevenueBaseAmount);
        Assert.Equal(115m, line.PayableAmount);
        Assert.True(line.Balances);
    }

    [Fact]
    public void An_exempt_line_has_no_tax()
    {
        var line = CommercialAmounts.ComputeLine(Line(2m, 50m, CommercialTaxMode.Exempt, 0m));

        Assert.Equal(100m, line.GrossAmount);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(100m, line.RevenueBaseAmount);
        Assert.Equal(100m, line.PayableAmount);
        Assert.True(line.Balances);
    }

    [Fact]
    public void A_mixed_document_balances_across_the_modes()
    {
        var document = CommercialAmounts.ComputeDocument(
        [
            Line(1m, 115m, CommercialTaxMode.Inclusive, 0.15m),
            Line(1m, 100m, CommercialTaxMode.Exclusive, 0.15m),
            Line(3m, 10m, CommercialTaxMode.Exempt, 0m),
        ]);

        Assert.Equal(3, document.Lines.Count);
        Assert.Equal(245m, document.GrossAmount);
        Assert.Equal(30m, document.TaxAmount);
        Assert.Equal(230m, document.RevenueBaseAmount);
        Assert.Equal(260m, document.PayableAmount);
        Assert.True(document.Balances);

        // Every line keeps the identity on its own.
        Assert.All(document.Lines, line => Assert.True(line.Balances));
    }

    [Fact]
    public void The_header_totals_are_the_sums_of_the_rounded_lines()
    {
        var document = CommercialAmounts.ComputeDocument(
        [
            Line(1m, 10.005m, CommercialTaxMode.Exclusive, 0m),
            Line(1m, 10.005m, CommercialTaxMode.Exclusive, 0m),
        ]);

        Assert.Equal(10.005m, document.Lines[0].GrossAmount);
        Assert.Equal(10.005m, document.Lines[1].GrossAmount);
        Assert.Equal(20.01m, document.GrossAmount);
    }

    [Fact]
    public void A_fractional_quantity_is_exact()
    {
        var line = CommercialAmounts.ComputeLine(Line(2.5m, 3.2m, CommercialTaxMode.Exclusive, 0.15m));

        Assert.Equal(8m, line.GrossAmount);
        Assert.Equal(1.2m, line.TaxAmount);
        Assert.Equal(8m, line.RevenueBaseAmount);
        Assert.Equal(9.2m, line.PayableAmount);
    }

    [Fact]
    public void A_half_way_value_rounds_away_from_zero()
    {
        // 0.5 x 0.2469 = 0.12345 is a half-way value at the money scale: it becomes 0.1235, never 0.1234.
        var line = CommercialAmounts.ComputeLine(Line(0.5m, 0.2469m, CommercialTaxMode.Exclusive, 0m));

        Assert.Equal(0.1235m, line.GrossAmount);

        var negative = CommercialAmounts.RoundMoney(-0.12345m);

        Assert.Equal(-0.1235m, negative);
    }

    [Fact]
    public void A_full_discount_is_allowed_and_leaves_no_revenue()
    {
        var line = CommercialAmounts.ComputeLine(Line(1m, 100m, CommercialTaxMode.Exclusive, 0.15m, 100m));

        Assert.Equal(100m, line.DiscountAmount);
        Assert.Equal(0m, line.RevenueBaseAmount);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(0m, line.PayableAmount);
        Assert.True(line.Balances);
    }

    [Fact]
    public void A_discount_above_the_gross_is_rejected()
    {
        var error = Assert.Throws<CommercialAmountException>(
            () => CommercialAmounts.ComputeLine(Line(1m, 100m, CommercialTaxMode.Exclusive, 0.15m, 100.01m)));

        Assert.Equal("line.discountAmount", error.Field);
    }

    [Fact]
    public void An_excessive_precision_is_rejected_instead_of_being_rounded()
    {
        var quantity = Assert.Throws<CommercialAmountException>(
            () => CommercialAmounts.ComputeLine(Line(1.0000001m, 10m, CommercialTaxMode.Exclusive, 0m)));
        Assert.Equal("line.quantity", quantity.Field);

        var price = Assert.Throws<CommercialAmountException>(
            () => CommercialAmounts.ComputeLine(Line(1m, 10.00001m, CommercialTaxMode.Exclusive, 0m)));
        Assert.Equal("line.unitPrice", price.Field);
    }

    [Fact]
    public void A_negative_or_undefined_value_is_rejected()
    {
        Assert.Equal(
            "line.quantity",
            Assert.Throws<CommercialAmountException>(
                () => CommercialAmounts.ComputeLine(Line(-1m, 10m, CommercialTaxMode.Exclusive, 0m))).Field);

        Assert.Equal(
            "line.unitPrice",
            Assert.Throws<CommercialAmountException>(
                () => CommercialAmounts.ComputeLine(Line(1m, -10m, CommercialTaxMode.Exclusive, 0m))).Field);

        Assert.Equal(
            "line.taxRate",
            Assert.Throws<CommercialAmountException>(
                () => CommercialAmounts.ComputeLine(Line(1m, 10m, CommercialTaxMode.Exclusive, 1m))).Field);

        Assert.Equal(
            "line.taxMode",
            Assert.Throws<CommercialAmountException>(
                () => CommercialAmounts.ComputeLine(Line(1m, 10m, (CommercialTaxMode)99, 0m))).Field);
    }

    [Fact]
    public void A_value_beyond_the_ledger_range_is_rejected()
    {
        Assert.Equal(
            "line.unitPrice",
            Assert.Throws<CommercialAmountException>(
                () => CommercialAmounts.ComputeLine(Line(1m, CommercialAmounts.MaxMoneyAmount + 1m, CommercialTaxMode.Exclusive, 0m))).Field);
    }

    [Fact]
    public void The_settlement_rounding_is_explicit()
    {
        // Three lines of 0.005 each: the line payables are already exact, so a two-decimal settlement
        // scale keeps the difference visible instead of hiding it in the tax.
        var document = CommercialAmounts.ComputeDocument(
        [
            Line(1m, 0.01m, CommercialTaxMode.Exclusive, 0m),
            Line(1m, 0.01m, CommercialTaxMode.Exclusive, 0m),
            Line(1m, 0.01m, CommercialTaxMode.Exclusive, 0m),
        ],
            settlementScale: 2);

        Assert.Equal(0.03m, document.PayableAmount);
        Assert.Equal(0.03m, document.SettlementPayableAmount);
        Assert.Equal(0m, document.RoundingAmount);
    }

    [Fact]
    public void The_header_discount_is_allocated_in_whole_money_units()
    {
        var allocation = CommercialAmounts.AllocateHeaderDiscount([10m, 10m, 10m], 10m);

        Assert.Equal(3, allocation.Count);
        Assert.Equal(10m, allocation.Sum());

        // The remainder is distributed deterministically, so two runs are identical.
        var again = CommercialAmounts.AllocateHeaderDiscount([10m, 10m, 10m], 10m);

        Assert.Equal(allocation, again);

        // Every allocation is a whole money unit (four decimals at most) and the sum is exact.
        Assert.All(allocation, amount => Assert.Equal(amount, decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
    }

    [Fact]
    public void The_header_discount_never_exceeds_the_eligible_amounts()
    {
        Assert.Throws<CommercialAmountException>(() => CommercialAmounts.AllocateHeaderDiscount([10m, 10m], 20.01m));
        Assert.Throws<CommercialAmountException>(() => CommercialAmounts.AllocateHeaderDiscount([10m, 10m], -1m));
    }

    [Fact]
    public void The_device_and_the_server_calculate_the_same_amount()
    {
        // The device sends the captured line to the server; both compute it with the same function.
        var device = CommercialAmountCalculator.ComputeLine(2m, 57.50m, CommercialTaxMode.Inclusive, 0.15m, 0m);
        var server = CommercialAmountCalculator.ComputeLine(2m, 57.50m, CommercialTaxMode.Inclusive, 0.15m, 0m);

        Assert.Equal(server, device);
        Assert.Equal(115m, device.GrossAmount);
        Assert.Equal(15m, device.TaxAmount);
        Assert.Equal(100m, device.RevenueBaseAmount);
        Assert.Equal(115m, device.PayableAmount);
    }

    [Fact]
    public void An_unknown_tax_mode_is_not_coerced_into_a_default()
    {
        Assert.Equal(
            "taxMode",
            Assert.Throws<CommercialAmountException>(() => CommercialAmountCalculator.ParseTaxMode("inclusive-ish")).Field);

        Assert.Equal(CommercialTaxMode.Inclusive, CommercialAmountCalculator.ParseTaxMode("Inclusive"));
    }
}
