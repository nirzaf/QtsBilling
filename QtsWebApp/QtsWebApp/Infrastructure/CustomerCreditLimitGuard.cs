using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Infrastructure;

/// <summary>Serializes new receivable exposure against a customer's optional credit limit.</summary>
public static class CustomerCreditLimitGuard
{
    public static async Task EnsureWithinLimitAsync(
        BillingDbContext db,
        Guid businessId,
        Guid customerId,
        string currency,
        decimal proposedExposure,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .FromSql($"SELECT * FROM customers WHERE business_id = {businessId} AND id = {customerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (customer is null)
        {
            throw new DomainRuleException(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "The customer does not exist in this business.",
                "The customer must remain available while the receivable is posted.");
        }

        if (customer.CreditLimit is not { } creditLimit)
        {
            return;
        }

        var invoiceBalance = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.BusinessId == businessId
                && invoice.CustomerId == customerId
                && invoice.Currency == currency
                && invoice.BalanceDue > 0m
                && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.PartiallyPaid))
            .SumAsync(invoice => (decimal?)invoice.BalanceDue, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        var saleBalance = await db.Sales.AsNoTracking()
            .Where(sale => sale.BusinessId == businessId
                && sale.CustomerId == customerId
                && sale.Currency == currency
                && sale.Status == SaleStatus.Posted
                && (sale.PaymentStatus == PaymentStatus.Pending || sale.PaymentStatus == PaymentStatus.Bounced)
                && !db.Invoices.Any(invoice => invoice.BusinessId == businessId
                    && invoice.SourceSaleId == sale.Id
                    && (invoice.Status == InvoiceStatus.Issued
                        || invoice.Status == InvoiceStatus.PartiallyPaid
                        || invoice.Status == InvoiceStatus.Paid)))
            .SumAsync(sale => (decimal?)sale.TotalAmount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        var openingRaised = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId
                && item.CustomerId == customerId
                && item.Kind == OpenItemKind.Receivable
                && item.Currency == currency)
            .SumAsync(item => (decimal?)item.OriginalAmount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        // An opening that a deposit has already settled is no longer exposure. Counting the imported amount again
        // would refuse a sale the customer has already paid for (specification task 25, recipe step 2).
        var openingApplied = await db.PaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.BusinessId == businessId && allocation.OpenItemId != null)
            .Join(db.OpenItems.AsNoTracking(),
                allocation => allocation.OpenItemId!.Value,
                item => item.Id,
                (allocation, item) => new { allocation, item })
            .Where(row => row.item.BusinessId == businessId
                && row.item.CustomerId == customerId
                && row.item.Kind == OpenItemKind.Receivable
                && row.item.Currency == currency)
            .SumAsync(row => (decimal?)row.allocation.AllocatedAmount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        var openingBalance = Math.Max(0m, decimal.Round(openingRaised - openingApplied, 4, MidpointRounding.AwayFromZero));

        var currentExposure = decimal.Round(invoiceBalance + saleBalance + openingBalance, 4, MidpointRounding.AwayFromZero);
        var projectedExposure = decimal.Round(currentExposure + proposedExposure, 4, MidpointRounding.AwayFromZero);
        if (projectedExposure > creditLimit)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.CreditLimitExceeded,
                "The customer credit limit would be exceeded.",
                $"Open receivables are {currentExposure} {currency}; this posting would raise them to {projectedExposure} {currency} against a limit of {creditLimit} {currency}.");
        }
    }
}
