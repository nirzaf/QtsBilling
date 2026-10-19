using System.Globalization;
using System.Net;
using System.Text;
using QtsBilling.Contracts.Sales;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// Printable receipt rendering (QB-10). The page is self-contained (no scripts, no external assets) so it
/// prints from any browser and every dynamic value is HTML-encoded. A non-posted receipt is visibly distinct:
/// the banner reads PROVISIONAL / PENDING SYNC and the page never claims to be final (spec §3.6; QB-19/QB-20
/// reuse this template for offline provisional receipts).
/// </summary>
public static class ReceiptRenderer
{
    public static string RenderHtml(SaleReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var provisional = !string.Equals(receipt.ReceiptState, "Posted", StringComparison.OrdinalIgnoreCase);
        var builder = new StringBuilder(6_000);
        var culture = CultureInfo.InvariantCulture;

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.Append("<title>Receipt ").Append(Encode(receipt.SaleNumber)).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("  :root { color-scheme: light; }");
        builder.AppendLine("  body { font-family: 'Consolas', 'Courier New', monospace; font-size: 13px; color: #111; margin: 0; background: #f5f5f5; }");
        builder.AppendLine("  .receipt { max-width: 340px; margin: 16px auto; background: #fff; padding: 16px; border: 1px solid #ddd; }");
        builder.AppendLine("  h1 { font-size: 15px; margin: 0 0 2px 0; text-align: center; }");
        builder.AppendLine("  .muted { color: #555; text-align: center; margin: 0 0 8px 0; }");
        builder.AppendLine("  .badge { text-align: center; font-weight: bold; padding: 4px 6px; margin: 8px 0; border: 1px solid #111; }");
        builder.AppendLine("  .badge.final { background: #e8f5e9; }");
        builder.AppendLine("  .badge.provisional { background: #ffebee; border-width: 2px; }");
        builder.AppendLine("  table { width: 100%; border-collapse: collapse; }");
        builder.AppendLine("  td, th { padding: 2px 0; vertical-align: top; text-align: left; }");
        builder.AppendLine("  td.num, th.num { text-align: right; }");
        builder.AppendLine("  hr { border: none; border-top: 1px dashed #999; margin: 8px 0; }");
        builder.AppendLine("  .totals td { font-weight: bold; }");
        builder.AppendLine("  .foot { margin-top: 10px; font-size: 11px; color: #555; text-align: center; }");
        builder.AppendLine("  @media print { body { background: #fff; } .receipt { border: none; margin: 0; max-width: none; } }");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<div class=\"receipt\">");

        builder.Append("<h1>").Append(Encode(receipt.BusinessName)).AppendLine("</h1>");

        if (!string.IsNullOrWhiteSpace(receipt.BusinessLegalName))
        {
            builder.Append("<p class=\"muted\">").Append(Encode(receipt.BusinessLegalName)).AppendLine("</p>");
        }

        if (!string.IsNullOrWhiteSpace(receipt.LocationName))
        {
            builder.Append("<p class=\"muted\">").Append(Encode(receipt.LocationName)).AppendLine("</p>");
        }

        if (!string.IsNullOrWhiteSpace(receipt.LocationAddress))
        {
            builder.Append("<p class=\"muted\">").Append(Encode(receipt.LocationAddress)).AppendLine("</p>");
        }

        if (provisional)
        {
            builder.AppendLine("<div class=\"badge provisional\">PROVISIONAL / PENDING SYNC &mdash; NOT A FINAL RECEIPT</div>");

            if (!string.IsNullOrWhiteSpace(receipt.ProvisionalReference))
            {
                builder.Append("<p class=\"muted\">Provisional reference: ").Append(Encode(receipt.ProvisionalReference)).AppendLine("</p>");
            }
        }
        else
        {
            builder.AppendLine("<div class=\"badge final\">FINAL RECEIPT &mdash; POSTED</div>");
        }

        builder.AppendLine("<table>");
        AppendMetaRow(builder, "Receipt", receipt.SaleNumber);
        AppendMetaRow(builder, "Date", receipt.PostedAtLocal);

        if (!string.IsNullOrWhiteSpace(receipt.CashierName))
        {
            AppendMetaRow(builder, "Cashier", receipt.CashierName);
        }

        if (!string.IsNullOrWhiteSpace(receipt.RegisterName))
        {
            AppendMetaRow(builder, "Register", receipt.RegisterName);
        }

        builder.AppendLine("</table>");
        builder.AppendLine("<hr />");

        builder.AppendLine("<table>");
        builder.AppendLine("  <tr><th>Item</th><th class=\"num\">Qty</th><th class=\"num\">Price</th><th class=\"num\">Total</th></tr>");

        foreach (var line in receipt.Lines)
        {
            builder.Append("  <tr><td>").Append(Encode(line.ProductName));

            if (!string.IsNullOrWhiteSpace(line.Sku))
            {
                builder.Append("<br /><span class=\"muted\">").Append(Encode(line.Sku)).Append("</span>");
            }

            builder.Append("</td><td class=\"num\">").Append(FormatQuantity(line.Quantity));
            builder.Append("</td><td class=\"num\">").Append(line.UnitPrice.ToString("0.0000", culture));
            builder.Append("</td><td class=\"num\">").Append(line.LineTotal.ToString("0.0000", culture)).AppendLine("</td></tr>");

            if (line.DiscountAmount > 0 || line.TaxAmount > 0)
            {
                builder.Append("  <tr><td colspan=\"2\" class=\"muted\">");

                if (line.DiscountAmount > 0)
                {
                    builder.Append("discount ").Append(line.DiscountAmount.ToString("0.0000", culture));
                }

                if (line.DiscountAmount > 0 && line.TaxAmount > 0)
                {
                    builder.Append(" / ");
                }

                if (line.TaxAmount > 0)
                {
                    builder.Append("tax ").Append(line.TaxAmount.ToString("0.0000", culture))
                        .Append(" (").Append((line.TaxRate * 100).ToString("0.####", culture)).Append("%)");
                }

                builder.AppendLine("</td><td class=\"num\"></td><td class=\"num\"></td></tr>");
            }
        }

        builder.AppendLine("</table>");
        builder.AppendLine("<hr />");

        builder.AppendLine("<table>");
        AppendAmountRow(builder, "Subtotal", receipt.SubtotalAmount, culture);

        if (receipt.DiscountAmount > 0)
        {
            AppendAmountRow(builder, "Discount", -receipt.DiscountAmount, culture);
        }

        if (receipt.TaxAmount > 0)
        {
            AppendAmountRow(builder, "Tax", receipt.TaxAmount, culture);
        }

        builder.Append("  <tr class=\"totals\"><td>Total ").Append(Encode(receipt.Currency)).Append("</td><td class=\"num\">")
            .Append(receipt.TotalAmount.ToString("0.0000", culture)).AppendLine("</td></tr>");
        builder.AppendLine("</table>");
        builder.AppendLine("<hr />");

        builder.AppendLine("<table>");

        foreach (var payment in receipt.Payments)
        {
            builder.Append("  <tr><td>").Append(Encode(payment.Method)).Append(" (").Append(Encode(payment.Status)).Append(")</td><td class=\"num\">")
                .Append(payment.Amount.ToString("0.0000", culture)).AppendLine("</td></tr>");

            if (payment.CashReceived is { } received && payment.Change is { } change)
            {
                builder.Append("  <tr><td class=\"muted\">received ").Append(received.ToString("0.0000", culture))
                    .Append(" / change ").Append(change.ToString("0.0000", culture)).AppendLine("</td><td class=\"num\"></td></tr>");
            }

            if (!string.IsNullOrWhiteSpace(payment.Reference))
            {
                builder.Append("  <tr><td class=\"muted\">ref ").Append(Encode(payment.Reference)).AppendLine("</td><td class=\"num\"></td></tr>");
            }
        }

        builder.AppendLine("</table>");
        builder.AppendLine("<p class=\"foot\">Values are the immutable snapshots stored when the sale was posted.</p>");
        builder.AppendLine("</div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static void AppendMetaRow(StringBuilder builder, string label, string value)
        => builder.Append("  <tr><td>").Append(Encode(label)).Append("</td><td class=\"num\">").Append(Encode(value)).AppendLine("</td></tr>");

    private static void AppendAmountRow(StringBuilder builder, string label, decimal amount, CultureInfo culture)
        => builder.Append("  <tr><td>").Append(Encode(label)).Append("</td><td class=\"num\">")
            .Append(amount.ToString("0.0000", culture)).AppendLine("</td></tr>");

    private static string FormatQuantity(decimal quantity)
        => quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString(CultureInfo.InvariantCulture)
            : quantity.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);
}
