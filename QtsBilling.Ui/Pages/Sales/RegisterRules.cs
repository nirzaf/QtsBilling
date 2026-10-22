using System.Globalization;

namespace QtsBilling.Ui.Pages.Sales;

/// <summary>
/// The decisions the register workbench makes for itself (specification task 19, recipe steps 1 to 4). The drawer
/// arithmetic, the custody rules and the over/short posting belong to the server; these are the sentences and the
/// pre-checks a screen needs so an operator is not surprised by a refusal they cannot see coming. Nothing here
/// decides whether money may be closed over - the server answers that, and the screen shows its answer.
/// </summary>
public static class RegisterRules
{
    /// <summary>
    /// Money as a drawer reads it: two decimals for anything that is a whole number of fils, and the full stored
    /// figure when the drawer actually holds a sub-cent difference, because hiding it would be the same lie as
    /// netting it out (specification D07, task 19).
    /// </summary>
    public static string Money(decimal value)
    {
        var cents = Math.Round(value, 2, MidpointRounding.AwayFromZero);

        return cents == value
            ? cents.ToString("0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    public static string MoneyText(decimal value, string currency) => $"{Money(value)} {currency}";

    /// <summary>
    /// An item whose amount the server cannot see (a terminal that has not pushed, or an operation it never posted)
    /// still needs a declared figure, because the person receiving custody must answer for a number.
    /// </summary>
    public static bool NeedsDeclaredAmount(QtsBilling.Contracts.Sales.UnresolvedCashItemDto item)
        => item.Kind != QtsBilling.Contracts.Sales.CashCustodySourceState.ProvisionalPayment;

    /// <summary>The amount the server can already see for this item, which no carried figure may go under.</summary>
    public static decimal MinimumCarried(QtsBilling.Contracts.Sales.UnresolvedCashItemDto item) => item.Amount;

    /// <summary>What the screen says when the server has refused a close for unresolved money.</summary>
    public static string BlockSentence(QtsBilling.Contracts.Sales.DrawerCustodyDto custody)
        => custody.BlockReason is { Length: > 0 } reason
            ? reason
            : "This drawer still holds unresolved cash.";

    /// <summary>How the close panel describes the counted difference before it is submitted.</summary>
    public static string VarianceSentence(decimal counted, decimal expected, string currency)
    {
        var difference = Math.Round(counted - expected, 4, MidpointRounding.AwayFromZero);

        return difference == 0m
            ? $"The count equals the {MoneyText(expected, currency)} the movements add to, so no journal is produced."
            : difference > 0m
                ? $"Over by {MoneyText(difference, currency)}: closing posts a reviewed cash-over entry to the business's cash-over/short account."
                : $"Short by {MoneyText(-difference, currency)}: closing posts a reviewed cash-short entry to the business's cash-over/short account. Rejected offline sales are never netted out of this difference.";
    }

    /// <summary>What the screen says about a terminal's contact evidence.</summary>
    public static string DeviceSentence(QtsBilling.Contracts.Sales.DeviceCustodyEvidenceDto device)
        => device.ContactedSinceSessionOpened
            ? $"{device.Name} ({device.Status}): reported since this drawer opened; {device.OperationsAwaitingReview} operation(s) await review."
            : $"{device.Name} ({device.Status}): {device.Explanation}";

    /// <summary>
    /// The command key for one intention. It is generated once per intention and reused for every retry of that
    /// same command, which is what makes a retry safe (specification D06).
    /// </summary>
    public static string NewCommandKey(string what) => $"{what}-{Guid.NewGuid():N}";

    /// <summary>The sentence that explains why a repeated click is not a repeated movement.</summary>
    public const string KeyHeldNote =
        "This command keeps one key while it stands, so retrying after a lost answer cannot move the drawer twice.";

    /// <summary>Whether the close panel can be offered at all.</summary>
    public static bool CanAttemptClose(QtsBilling.Contracts.Sales.DrawerCustodyDto? custody)
        => custody is { Closable: true };
}
