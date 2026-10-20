using QtsBilling.Contracts.Sales;
using QtsWebApp.Features.Sales;
using Xunit;

namespace TestQtsBilling.Contracts;

public sealed class ReceiptRendererTests
{
    [Fact]
    public void Posted_receipt_uses_an_80_millimetre_print_page()
    {
        var receipt = new SaleReceiptDto(
            Guid.CreateVersion7(),
            "S-1003",
            "Posted",
            null,
            "Demo Store",
            null,
            "Main location",
            null,
            "USD",
            DateTimeOffset.UtcNow,
            "2026-10-20 13:00",
            "Cashier",
            "Front till",
            Guid.CreateVersion7(),
            [new SaleReceiptLineDto(1, "Tea", "TEA-1", 1m, 100m, 0m, 0.15m, 15m, 115m)],
            100m,
            0m,
            15m,
            115m,
            [new SaleReceiptPaymentDto("Cash", "Confirmed", 115m, 120m, 5m, null)]);

        var html = ReceiptRenderer.RenderHtml(receipt);

        Assert.Contains("@page { size: 80mm auto; margin: 0; }", html, StringComparison.Ordinal);
        Assert.Contains("width: 72mm", html, StringComparison.Ordinal);
        Assert.Contains("FINAL RECEIPT", html, StringComparison.Ordinal);
    }
}
