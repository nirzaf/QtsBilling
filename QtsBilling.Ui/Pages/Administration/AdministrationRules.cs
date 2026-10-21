using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Sync;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// The decisions the administration screens make, kept out of the markup so they can be tested against the real
/// answers the API gives (specification task 14). None of this enforces anything - the server is the authority -
/// but every sentence here has to agree with what the server will say, otherwise the screen misleads the operator
/// about a money-bearing action before they take it.
/// </summary>
public static class AdministrationRules
{
    /// <summary>
    /// Currency and settlement scale are structural: the server refuses them once facts exist. A business that is
    /// ready to post is a business whose shape is now fixed, so the screen shows those two fields as read-only
    /// with the reason instead of inviting an attempt that will be refused.
    /// </summary>
    public const string StructuralChangeReason =
        "The base currency and the settlement scale belong to the business's shape. Once it can post, they are fixed: "
        + "changing them would re-price facts that are already booked. Record them correctly now, or open a new business.";

    /// <summary>The handover secret's own rule: one display, then it is gone.</summary>
    public const string HandoverOnceReason =
        "This code is shown once and cannot be shown again. Hand it over in person or print it, then close this panel. "
        + "Nothing here keeps it: the server stores only a verifier, and a new code supersedes this one.";

    /// <summary>Retirement does not erase a terminal, and the screen must not pretend otherwise.</summary>
    public const string NoWipeReason =
        "Retiring a terminal withdraws its authority and keeps its identity, receipts and queued operations for "
        + "reconciliation. Erasing the device itself is a separate, approved on-device step after export; there is "
        + "deliberately no wipe button here.";

    private const string OwnerRole = "Owner";
    private const string CashierRole = "Cashier";

    /// <summary>Whether the session may do this at all - the same permission the route asks for.</summary>
    public static bool Can(IReadOnlyList<string>? permissions, string permission)
        => permissions is not null && permissions.Contains(permission, StringComparer.Ordinal);

    /// <summary>Why an action is not on screen, in the operator's terms rather than the policy name.</summary>
    public static string MissingReason(string action, string permission)
        => $"{action} needs the '{permission}' authority. Ask an owner of this business to review your role.";

    /// <summary>
    /// A new cashier gets no discount allowance at all. The stored column default is not a safe one to offer, and
    /// a screen that pre-fills 100 % hands out the whole register (specification task 07).
    /// </summary>
    public static decimal DefaultDiscountRate(string role)
        => string.Equals(role, CashierRole, StringComparison.OrdinalIgnoreCase) ? 0m : 1m;

    /// <summary>What a role means in day-to-day words, so the picker is a decision rather than a label.</summary>
    public static string RoleSummary(string role) => role switch
    {
        OwnerRole => "Runs the business: setup, accounts, staff, terminals and every posting.",
        "Manager" => "Trades the counter, the catalogue and purchasing, and may open an enrolment code.",
        "Accountant" => "Posts and closes the books, maintains invoices and expenses, reads reports. Not staff or terminals.",
        CashierRole => "Takes payment at the branches listed here and may add a customer, with no discount allowance unless you give one.",
        "Viewer" => "Reads customers, the catalogue and reports. Changes nothing.",
        _ => "The server decides what this role may do; you will see its answer when you try.",
    };

    /// <summary>
    /// The last active owner cannot be demoted or disabled: without a live owner nobody can issue the recovery
    /// ticket that would restore access, so the server refuses and the screen warns first.
    /// </summary>
    /// <param name="intendedRole">The role the form is about to save, or null when the change is a deactivation.</param>
    public static string? LastOwnerBlock(
        IReadOnlyList<AdministrationUserDto> users,
        AdministrationUserDto target,
        string? intendedRole = null)
    {
        if (!string.Equals(target.Role, OwnerRole, StringComparison.OrdinalIgnoreCase) || !target.IsActive)
        {
            return null;
        }

        // Keeping the Owner role (a branch or discount change, for instance) leaves somebody able to recover the
        // business, so it is not what this rule is about.
        if (intendedRole is not null && string.Equals(intendedRole, OwnerRole, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var activeOwners = users.Count(user => string.Equals(user.Role, OwnerRole, StringComparison.OrdinalIgnoreCase) && user.IsActive);

        return activeOwners > 1
            ? null
            : "This is the only active owner of the business. Appoint a second owner first - ownership cannot be removed while it would leave the business unowned.";
    }

    /// <summary>The sentence the handover panel shows: what to do with the code and when it stops working.</summary>
    public static string HandoverSentence(SetupTicketDto ticket, DateTimeOffset now)
    {
        var remaining = ticket.ExpiresAtUtc - now;
        var lifetime = remaining <= TimeSpan.Zero
            ? "has already expired - issue a new one"
            : $"expires in {(int)Math.Ceiling(remaining.TotalMinutes)} minute(s)";

        var purpose = string.Equals(ticket.Purpose, "Recovery", StringComparison.OrdinalIgnoreCase)
            ? "recovery of an existing account"
            : "a first sign-in for a new account";

        return $"Hand this code to the person for {purpose}. It works once, {lifetime}, and only for that account.";
    }

    // ------------------------------------------------------------------ terminals

    /// <summary>True when the terminal still owes money or has operations the server could not settle.</summary>
    public static bool HoldsUnresolvedMoney(DeviceInfoDto device)
        => device.OperationsNeedingReview > 0 || device.DeviceHeldProvisionalCash > 0m;

    /// <summary>The count a clearance must acknowledge at least. Understating it is refused, so the screen says so.</summary>
    public static int MinimumAcknowledgedOperations(DeviceInfoDto device) => device.OperationsNeedingReview;

    /// <summary>Why moving this terminal needs a custody resolution first, or null when it can move as it stands.</summary>
    public static string? ReassignmentBlock(DeviceInfoDto device)
    {
        if (!string.Equals(device.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            return $"Only an active terminal can be reassigned; this one is {device.Status.ToLowerInvariant()}.";
        }

        if (!HoldsUnresolvedMoney(device))
        {
            return null;
        }

        return $"This terminal has {device.OperationsNeedingReview} operation(s) awaiting review"
            + (device.DeviceHeldProvisionalCash > 0m
                ? $" and holds {device.DeviceHeldProvisionalCash:N2} of provisional cash"
                : " and no cash acknowledged on record")
            + ". Record a reassignment clearance acknowledging at least that before it moves.";
    }

    /// <summary>Why retirement waits. Unlike reassignment, no acknowledgement buys the way through.</summary>
    public static string? RetirementBlock(DeviceInfoDto device)
        => device.OperationsNeedingReview > 0
            ? $"{device.OperationsNeedingReview} operation(s) on this terminal still await review. Retirement waits for reconciliation; a terminal is never retired to make outstanding money disappear."
            : null;

    /// <summary>Whether any lifecycle command is still offered for this row.</summary>
    public static bool IsClosed(DeviceInfoDto device)
        => string.Equals(device.Status, "Retired", StringComparison.OrdinalIgnoreCase) || device.RetiredAtUtc is not null;

    /// <summary>What the operator must state on the clearance, in the same terms the server will check.</summary>
    public static string ClearanceSentence(DeviceInfoDto device)
        => $"State what was reconciled, and acknowledge at least {MinimumAcknowledgedOperations(device)} outstanding operation(s)"
            + (device.DeviceHeldProvisionalCash > 0m
                ? $" and {device.DeviceHeldProvisionalCash:N2} of cash the terminal holds"
                : string.Empty)
            + ". A clearance that understates the server's own count is refused.";

    /// <summary>The one-sentence custody position of a terminal, shown wherever the row is.</summary>
    public static string CustodySentence(DeviceInfoDto device)
        => HoldsUnresolvedMoney(device)
            ? $"Unreconciled: {device.OperationsNeedingReview} operation(s) awaiting review, {device.DeviceHeldProvisionalCash:N2} cash on record."
            : $"Nothing outstanding: {device.ReceivedOperations} operation(s) received, none awaiting review.";
}
