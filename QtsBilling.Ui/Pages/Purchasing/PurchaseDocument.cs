using System.Net;
using System.Text;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// Local composition of a printable purchase-order or goods-received document (specification task 26, recipe
/// step 4). Every value comes from the snapshots the server already stored for the order or the receipt - names,
/// quantities, costs, tax and numbers are read, never recalculated - and each dynamic piece is HTML-encoded, so
/// a supplier name containing markup cannot smuggle anything onto the paper or into the page that prints it.
/// </summary>
/// <remarks>
/// The result is handed to <see cref="IDocumentOutput"/> as content, which is the only shape the print contract
/// accepts: nothing navigates, so no session token can ride in a URL, and no file is written by this code.
/// </remarks>
public static class PurchaseDocument
{
    /// <summary>Builds the order document, stamped as a copy when it is not the first print.</summary>
    /// <param name="branchName">The branch named the way the operator chose it, so the paper never prints a key.</param>
    public static DocumentOutputRequest ForOrder(PurchaseOrderDetailDto detail, string? branchName, bool reprint)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var order = detail.Order;
        var builder = Page(
            "Purchase order",
            order.OrderNumber,
            reprint,
            body =>
            {
                Row(body, "Supplier", order.SupplierName ?? "the supplier on the order");
                Row(body, "Branch", string.IsNullOrWhiteSpace(branchName) ? "the branch on the order" : branchName);
                Row(body, "Ordered", InvoiceRules.DateText(order.OrderDate));
                Row(body, "Expected", order.ExpectedDate is { } expected ? InvoiceRules.DateText(expected) : "-");
                Row(body, "Status", PurchasingRules.StatusText(order.Status));
                Row(body, "Currency", order.Currency);

                Lines(body, detail.Lines);

                Totals(body, order);

                if (!string.IsNullOrWhiteSpace(order.Note))
                {
                    body.Append("<p class=\"muted\">Note: ").Append(Encode(order.Note)).Append("</p>");
                }

                body.Append("<p class=\"foot\">").Append(Encode(PurchasingRules.CommitmentRule)).Append("</p>");
            });

        return new DocumentOutputRequest(builder.ToString(), $"Purchase order {order.OrderNumber}{(reprint ? " (copy)" : string.Empty)}");
    }

    /// <summary>Builds one goods received note or return document from its stored lines.</summary>
    /// <param name="branchTimeZoneId">The receiving branch's zone, so the recorded instant is read on that branch's day.</param>
    public static DocumentOutputRequest ForReceipt(
        PurchaseReceiptDto receipt,
        string? branchName,
        bool reprint,
        string? branchTimeZoneId = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var isReturn = PurchasingRules.IsReturn(receipt.Kind);
        var presentation = PurchasingRules.PresentationOf(branchTimeZoneId);
        var builder = Page(
            isReturn ? "Purchase return" : "Goods received note",
            receipt.ReceiptNumber,
            reprint,
            body =>
            {
                Row(body, "Order", receipt.OrderNumber);
                Row(body, "Branch", string.IsNullOrWhiteSpace(branchName) ? "the branch on the order" : branchName);
                Row(body, "Recorded", InvoiceRules.InstantText(receipt.ReceivedAtUtc, presentation));
                Row(body, "Document", receipt.ReceiptNumber);

                body.Append("<table><tr><th>Item</th><th class=\"num\">Quantity</th><th class=\"num\">Unit cost</th><th class=\"num\">Value</th></tr>");
                var value = 0m;

                foreach (var line in receipt.Lines)
                {
                    body.Append("<tr><td>").Append(Encode(line.ProductName));

                    if (!string.IsNullOrWhiteSpace(line.Sku))
                    {
                        body.Append("<br /><span class=\"muted\">").Append(Encode(line.Sku)).Append("</span>");
                    }

                    body.Append("</td><td class=\"num\">").Append(PurchasingRules.QuantityText(line.Quantity));
                    body.Append("</td><td class=\"num\">").Append(PurchasingRules.Money(line.UnitCost));
                    body.Append("</td><td class=\"num\">").Append(PurchasingRules.Money(line.Quantity * line.UnitCost));
                    body.Append("</tr>");

                    value += line.Quantity * line.UnitCost;
                }

                body.Append("</table>");
                body.Append("<table><tr class=\"totals\"><td>Value of this document</td><td class=\"num\">")
                    .Append(PurchasingRules.Money(value)).Append("</td></tr></table>");

                if (!string.IsNullOrWhiteSpace(receipt.Note))
                {
                    body.Append("<p class=\"muted\">Note: ").Append(Encode(receipt.Note)).Append("</p>");
                }

                // The paper states which ledger entry the document produced and which accounts it moved, so a
                // delivery note on a desk is traceable into the books without asking a developer (recipe step 3).
                Row(body, "Ledger entry", PurchasingRules.LedgerText(receipt));

                body.Append("<p class=\"foot\">").Append(Encode(isReturn ? PurchasingRules.ReturnRule : PurchasingRules.ReceiptRule)).Append("</p>");
            });

        return new DocumentOutputRequest(builder.ToString(), $"{(isReturn ? "Return" : "Receipt")} {receipt.ReceiptNumber}{(reprint ? " (copy)" : string.Empty)}");
    }

    private static StringBuilder Page(string heading, string number, bool reprint, Action<StringBuilder> compose)
    {
        var builder = new StringBuilder(4_000);

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.Append("<title>").Append(Encode($"{heading} {number}")).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("  body { font-family: 'Segoe UI', 'Helvetica Neue', sans-serif; font-size: 13px; color: #111; margin: 0; background: #f5f5f5; }");
        builder.AppendLine("  .sheet { max-width: 720px; margin: 16px auto; background: #fff; padding: 28px 32px; border: 1px solid #ddd; }");
        builder.AppendLine("  h1 { font-size: 20px; margin: 0 0 2px 0; }");
        builder.AppendLine("  .muted { color: #555; }");
        builder.AppendLine("  .badge { display: inline-block; font-weight: bold; padding: 4px 10px; margin: 8px 0; border: 1px solid #111; }");
        builder.AppendLine("  .badge.copy { background: #fff8e1; }");
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
        builder.Append("<h1>").Append(Encode(heading)).Append(" ").Append(Encode(number)).AppendLine("</h1>");

        if (reprint)
        {
            builder.AppendLine("<div class=\"badge copy\">COPY - composed from the stored snapshot; nothing was re-priced or re-numbered</div>");
        }

        compose(builder);

        builder.AppendLine("</div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder;
    }

    private static void Lines(StringBuilder body, IReadOnlyList<PurchaseOrderLineDto> lines)
    {
        body.Append("<table><tr><th>Item</th><th class=\"num\">Ordered</th><th class=\"num\">Received</th><th class=\"num\">Outstanding</th>");
        body.Append("<th class=\"num\">Unit cost</th><th class=\"num\">Tax</th><th class=\"num\">Line total</th></tr>");

        foreach (var line in lines)
        {
            body.Append("<tr><td>").Append(Encode(line.ProductName));

            if (!string.IsNullOrWhiteSpace(line.Sku))
            {
                body.Append("<br /><span class=\"muted\">").Append(Encode(line.Sku)).Append("</span>");
            }

            body.Append("</td><td class=\"num\">").Append(PurchasingRules.QuantityText(line.QuantityOrdered));
            body.Append("</td><td class=\"num\">").Append(PurchasingRules.QuantityText(line.QuantityReceived));
            body.Append("</td><td class=\"num\">").Append(PurchasingRules.QuantityText(PurchasingRules.Outstanding(line)));
            body.Append("</td><td class=\"num\">").Append(PurchasingRules.Money(line.UnitCost));
            body.Append("</td><td class=\"num\">").Append(PurchasingRules.Money(line.TaxAmount));
            body.Append("</td><td class=\"num\">").Append(PurchasingRules.Money(line.LineTotal));
            body.Append("</tr>");
        }

        body.Append("</table>");
    }

    private static void Totals(StringBuilder body, PurchaseOrderSummaryDto order)
    {
        body.Append("<table>");
        AmountRow(body, "Subtotal", order.SubtotalAmount);
        AmountRow(body, "Discount", -order.DiscountAmount);
        AmountRow(body, "Tax", order.TaxAmount);
        body.Append("<tr class=\"totals\"><td>Total ").Append(Encode(order.Currency)).Append("</td><td class=\"num\">")
            .Append(PurchasingRules.Money(order.TotalAmount)).Append("</td></tr>");
        body.Append("</table>");
    }

    private static void AmountRow(StringBuilder body, string label, decimal amount)
        => body.Append("<tr><td>").Append(Encode(label)).Append("</td><td class=\"num\">")
            .Append(PurchasingRules.Money(amount)).Append("</td></tr>");

    private static void Row(StringBuilder body, string label, string value)
        => body.Append("<p><strong>").Append(Encode(label)).Append(":</strong> ").Append(Encode(value)).Append("</p>");

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
