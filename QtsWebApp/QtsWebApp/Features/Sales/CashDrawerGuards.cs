using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// The one arithmetic a drawer's payable cash is judged by (specification D07): the same derived figure task 19
/// shows on the register workbench - opening float plus confirmed sale cash plus paid-in, less paid-out and
/// refunds, every component read from the session's own append-only movements. It is applied before money is
/// promised, so neither a refund nor a paid-out can take cash the till never held and leave the difference to be
/// discovered at close.
/// </summary>
internal static class CashDrawerGuards
{
    /// <summary>Expected cash of one session, derived from its movements and never from a stored balance.</summary>
    internal static async Task<decimal> ExpectedCashAsync(
        BillingDbContext db,
        Guid businessId,
        CashRegisterSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var totals = await db.CashRegisterMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId && movement.CashRegisterSessionId == session.Id)
            .GroupBy(movement => movement.MovementType)
            .Select(group => new { MovementType = group.Key, Total = group.Sum(movement => movement.Amount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal Sum(CashMovementType kind)
            => totals.FirstOrDefault(row => row.MovementType == kind)?.Total ?? 0m;

        return LedgerWriter.Round(
            session.OpeningFloat
            + Sum(CashMovementType.SalePayment)
            + Sum(CashMovementType.CashIn)
            - Sum(CashMovementType.CashOut)
            - Sum(CashMovementType.Refund));
    }

    /// <summary>
    /// Refuses a payout the drawer cannot cover, before any row, movement or journal is written. A negative
    /// expected cash is not a detail to be corrected when the drawer is counted: it means money left a till that
    /// never held it, which is exactly the discrepancy D07 forbids hiding.
    /// </summary>
    internal static async Task EnsureCashCoversAsync(
        BillingDbContext db,
        Guid businessId,
        CashRegisterSession session,
        decimal amount,
        string whatIsBeingPaid,
        CancellationToken cancellationToken)
    {
        var expected = await ExpectedCashAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

        if (expected >= amount)
        {
            return;
        }

        var shortfall = LedgerWriter.Round(amount - expected);

        throw new DomainRuleException(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.InsufficientDrawerCash,
            $"This drawer cannot pay out {Format(amount)} {session.Currency}.",
            $"The session holds {Format(expected)} {session.Currency}, derived from its own movements, and {whatIsBeingPaid} would take {Format(amount)} - {Format(shortfall)} more than the drawer has. Record a paid-in against real cash, refund through the drawer that took the money, or reduce the amount; a counted shortage at close is never a substitute for this refusal.");
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
