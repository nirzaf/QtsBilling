using System.Globalization;
using System.Net;
using System.Text;
using QtsBilling.Contracts.Invoices;

namespace QtsWebApp.Features.Invoices;

/// <summary>
/// Printable invoice rendering (QB-11). Self-contained page (no scripts, no external assets) so it prints
/// from any browser; every dynamic value is HTML-encoded and all numbers come from the stored snapshots.
/// A draft is visibly marked DRAFT &mdash; NOT AN ISSUED INVOICE and a voided document is stamped VOID, so a
/// printout can never pass for a live receivable (spec §3.7).
/// </summary>
public static class InvoicePrintRenderer
{
    public static string RenderHtml(InvoiceDetailDto detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var invoice = detail.Invoice;
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(8_000);

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.Append("<title>Invoice ").Append(Encode(invoice.InvoiceNumber ?? "draft")).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("  :root { color-scheme: light; }");
        builder.AppendLine("  body { font-family: 'Segoe UI', 'Helvetica Neue', sans-serif; font-size: 13px; color: #111; margin: 0; background: #f5f5f5; }");
        builder.AppendLine("  .sheet { max-width: 720px; margin: 16px auto; background: #fff; padding: 28px 32px; border: 1px solid #ddd; }");
        builder.AppendLine("  .head { display: flex; justify-content: space-between; gap: 24px; }");
        builder.AppendLine("  h1 { font-size: 20px; margin: 0 0 2px 0; }");
        builder.AppendLine("  h2 { font-size: 14px; margin: 0; text-transform: uppercase; letter-spacing: 1px; color: #555; }");
        builder.AppendLine("  .muted { color: #555; }");
        builder.AppendLine("  .party { margin-top: 14px; }");
        builder.AppendLine("  .badge { display: inline-block; font-weight: bold; padding: 4px 10px; margin: 10px 0 0 0; border: 1px solid #111; }");
        builder.AppendLine("  .badge.draft { background: #fff8e1; }");
        builder.AppendLine("  .badge.issued, .badge.partially { background: #e3f2fd; }");
        builder.AppendLine("  .badge.paid { background: #e8f5e9; }");
        builder.AppendLine("  .badge.void { background: #ffebee; border-width: 2px; }");
        builder.AppendLine("  table { width: 100%; border-collapse: collapse; margin-top: 12px; }");
        builder.AppendLine("  th, td { padding: 5px 6px; text-align: left; border-bottom: 1px solid #eee; vertical-align: top; }");
        builder.AppendLine("  th { border-bottom: 2px solid #111; }");
        builder.AppendLine("  td.num, th.num { text-align: right; }");
        builder.AppendLine("  tr.totals td { font-weight: bold; border-bottom: none; }");
        builder.AppendLine("  .foot { margin-top: 18px; font-size: 11px; color: #555; }");
        builder.AppendLine("  @media print { body { background: #fff; } .sheet { border: none; margin: 0; max-width: none; } }");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<div class=\"sheet\">");

        builder.AppendLine("<div class=\"head\">");
        builder.AppendLine("<div>");
        builder.Append("<h1>").Append(Encode(detail.PartySnapshot?.BusinessName ?? "Historical business name unavailable")).AppendLine("</h1>");

        if (!string.IsNullOrWhiteSpace(detail.PartySnapshot?.BusinessLegalName))
        {
            builder.Append("<p class=\"muted\">").Append(Encode(detail.PartySnapshot.BusinessLegalName)).AppendLine("</p>");
        }

        builder.AppendLine("</div>");
        builder.AppendLine("<div style=\"text-align:right;\">");
        builder.AppendLine("<h2>Invoice</h2>");
        builder.Append("<p class=\"muted\">").Append(Encode(string.IsNullOrEmpty(invoice.InvoiceNumber) ? "(draft - no number yet)" : invoice.InvoiceNumber)).AppendLine("</p>");
        builder.AppendLine("</div>");
        builder.AppendLine("</div>");

        builder.Append("<div class=\"badge ").Append(BadgeClass(invoice.Status)).Append("\">").Append(BadgeText(invoice.Status)).AppendLine("</div>");

        builder.AppendLine("<div class=\"party\">");
        builder.AppendLine("<h2>Bill to</h2>");
        builder.Append("<p><strong>").Append(Encode(detail.PartySnapshot?.CustomerName ?? "Historical customer name unavailable")).AppendLine("</strong>");

        if (!string.IsNullOrWhiteSpace(detail.PartySnapshot?.CustomerAddress))
        {
            builder.Append("<br />").Append(Encode(detail.PartySnapshot.CustomerAddress));
        }

        if (!string.IsNullOrWhiteSpace(detail.PartySnapshot?.CustomerPhone))
        {
            builder.Append("<br />").Append(Encode(detail.PartySnapshot.CustomerPhone));
        }

        if (!string.IsNullOrWhiteSpace(detail.PartySnapshot?.CustomerEmail))
        {
            builder.Append("<br />").Append(Encode(detail.PartySnapshot.CustomerEmail));
        }

        builder.AppendLine("</p>");
        builder.AppendLine("</div>");

        builder.AppendLine("<table>");
        builder.AppendLine("  <tr><td class=\"muted\">Issue date</td><td>").Append(Encode(invoice.IssueDate.ToString("yyyy-MM-dd", culture))).AppendLine("</td>");
        builder.AppendLine("      <td class=\"muted\">Due date</td><td>").Append(Encode(invoice.DueDate?.ToString("yyyy-MM-dd", culture) ?? "-")).AppendLine("</td></tr>");
        builder.AppendLine("  <tr><td class=\"muted\">Currency</td><td>").Append(Encode(invoice.Currency)).AppendLine("</td>");
        builder.AppendLine("      <td class=\"muted\">Status</td><td>").Append(Encode(invoice.Status.ToString())).AppendLine("</td></tr>");
        builder.AppendLine("</table>");

        builder.AppendLine("<table>");
        builder.AppendLine("  <tr><th>Description</th><th class=\"num\">Qty</th><th class=\"num\">Unit price</th><th class=\"num\">Discount</th><th class=\"num\">Tax</th><th class=\"num\">Line total</th></tr>");

        foreach (var line in detail.Lines)
        {
            builder.Append("  <tr><td>").Append(Encode(line.ProductName));

            if (!string.IsNullOrWhiteSpace(line.Sku))
            {
                builder.Append("<br /><span class=\"muted\">").Append(Encode(line.Sku)).Append("</span>");
            }

            builder.Append("</td><td class=\"num\">").Append(FormatQuantity(line.Quantity));
            builder.Append("</td><td class=\"num\">").Append(line.UnitPrice.ToString("0.0000", culture));
            builder.Append("</td><td class=\"num\">").Append(line.DiscountAmount.ToString("0.0000", culture));
            builder.Append("</td><td class=\"num\">").Append(line.TaxAmount.ToString("0.0000", culture));
            builder.Append("</td><td class=\"num\">").Append(line.LineTotal.ToString("0.0000", culture)).AppendLine("</td></tr>");
        }

        builder.AppendLine("</table>");

        builder.AppendLine("<table>");
        AppendAmountRow(builder, "Subtotal", invoice.SubtotalAmount, culture);

        if (invoice.DiscountAmount > 0)
        {
            AppendAmountRow(builder, "Discount", -invoice.DiscountAmount, culture);
        }

        if (invoice.TaxAmount > 0)
        {
            AppendAmountRow(builder, "Tax", invoice.TaxAmount, culture);
        }

        builder.Append("  <tr class=\"totals\"><td>Total ").Append(Encode(invoice.Currency)).Append("</td><td class=\"num\">")
            .Append(invoice.TotalAmount.ToString("0.0000", culture)).AppendLine("</td></tr>");

        if (invoice.PaidAmount > 0)
        {
            AppendAmountRow(builder, "Paid", invoice.PaidAmount, culture);

            var balance = invoice.Status == InvoiceState.Void ? 0m : invoice.TotalAmount - invoice.PaidAmount;
            AppendAmountRow(builder, "Balance due", balance, culture);
        }

        builder.AppendLine("</table>");

        if (detail.Payments.Count > 0)
        {
            builder.AppendLine("<table>");
            builder.AppendLine("  <tr><th>Payments</th><th>Reference</th><th class=\"num\">Amount</th></tr>");

            foreach (var payment in detail.Payments)
            {
                builder.Append("  <tr><td>").Append(Encode(payment.Method)).Append(" (").Append(Encode(payment.Status)).Append(")")
                    .Append("</td><td class=\"muted\">").Append(Encode(payment.Reference ?? "-"))
                    .Append("</td><td class=\"num\">").Append(payment.Amount.ToString("0.0000", culture)).AppendLine("</td></tr>");
            }

            builder.AppendLine("</table>");
        }

        if (detail.CreditNote is { } credit)
        {
            builder.AppendLine("<table>");
            builder.AppendLine("  <tr><th>Credit note</th><th>Reason</th><th class=\"num\">Amount</th></tr>");
            builder.Append("  <tr><td>").Append(Encode(credit.CreditNumber)).Append("<br /><span class=\"muted\">")
                .Append(Encode(credit.IssuedAtUtc.ToString("yyyy-MM-dd", culture))).AppendLine("</span></td>")
                .Append("<td class=\"muted\">").Append(Encode(credit.Reason)).AppendLine("</td>")
                .Append("<td class=\"num\">").Append(credit.Amount.ToString("0.0000", culture)).AppendLine("</td></tr>");
            builder.AppendLine("</table>");
            builder.AppendLine("<p class=\"muted\">This invoice is void; the credit note reverses the original journal entry.</p>");
        }

        if (!string.IsNullOrWhiteSpace(invoice.Note))
        {
            builder.Append("<p class=\"muted\">Note: ").Append(Encode(invoice.Note)).AppendLine("</p>");
        }

        builder.AppendLine("<p class=\"foot\">Values are the immutable snapshots stored when the invoice was issued; later catalog or customer edits never change this document.</p>");
        builder.AppendLine("</div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static string BadgeText(InvoiceState status)
        => status switch
        {
            InvoiceState.Draft => "DRAFT &mdash; NOT AN ISSUED INVOICE",
            InvoiceState.PartiallyPaid => "ISSUED &mdash; PARTIALLY PAID",
            InvoiceState.Paid => "ISSUED &mdash; PAID IN FULL",
            InvoiceState.Void => "VOID &mdash; NOT PAYABLE",
            _ => "ISSUED",
        };

    private static string BadgeClass(InvoiceState status)
        => status switch
        {
            InvoiceState.Draft => "draft",
            InvoiceState.PartiallyPaid => "partially",
            InvoiceState.Paid => "paid",
            InvoiceState.Void => "void",
            _ => "issued",
        };

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
