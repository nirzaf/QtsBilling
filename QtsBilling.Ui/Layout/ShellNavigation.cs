using MudBlazor;
using QtsBilling.Contracts.Identity;

namespace QtsBilling.Ui.Layout;

/// <summary>How far a module has shipped: usable now, usable only on the device terminal, or not yet built.</summary>
public enum ShellAvailability
{
    /// <summary>The route exists in the shared library and both hosts can render it.</summary>
    Available,

    /// <summary>The route exists only on the Windows terminal, which owns the offline store and the drawer.</summary>
    DeviceOnly,

    /// <summary>Permission and API exist; the screen is scheduled but not built, so no link is offered.</summary>
    Planned,
}

/// <summary>
/// One navigation entry as the shell presents it. <paramref name="RequiredPermission"/> is the server's own
/// permission for the underlying reads, so the menu never advertises an action the API would refuse;
/// <paramref name="ArrivalNote"/> names the specification task that ships a planned screen.
/// </summary>
public sealed record ShellRoute(
    string Title,
    string Href,
    string Icon,
    string Group,
    string? RequiredPermission,
    ShellAvailability Availability,
    string? ArrivalNote = null);

/// <summary>
/// The single catalogue of what this product offers (specification task 13). Navigation and the landing page read
/// the same list, so a module can never be discoverable in one place and hidden in the other, and a planned
/// screen is labelled as planned instead of being shipped as a dead link.
/// </summary>
public static class ShellNavigation
{
    public const string GroupOverview = "Overview";
    public const string GroupSales = "Sales";
    public const string GroupPurchasing = "Purchasing";
    public const string GroupInventory = "Inventory";
    public const string GroupFinance = "Finance";
    public const string GroupAdministration = "Administration";

    /// <summary>The order the groups appear in. Every group has at least one entry, so none is ever empty.</summary>
    public static readonly IReadOnlyList<string> GroupOrder =
    [
        GroupOverview, GroupSales, GroupPurchasing, GroupInventory, GroupFinance, GroupAdministration,
    ];

    public static readonly IReadOnlyList<ShellRoute> Routes =
    [
        new("Home", "/", Icons.Material.Filled.Dashboard, GroupOverview, RequiredPermission: null, ShellAvailability.Available),

        // Sales: taking money and the commitments around it.
        new("Point of sale", "pos", Icons.Material.Filled.PointOfSale, GroupSales, Permissions.SalesPost, ShellAvailability.DeviceOnly,
            "The browser posts through the same contract but does not hold a drawer; trade at a registered terminal."),
        new("Registers and drawers", "sales/registers", Icons.Material.Filled.AccountBalanceWallet, GroupSales, Permissions.RegistersManage, ShellAvailability.Available),
        new("Sales history and refunds", "sales", Icons.Material.Filled.History, GroupSales, Permissions.SalesPost, ShellAvailability.Available),
        new("Quotes", "orders/quotes", Icons.Material.Filled.RequestQuote, GroupSales, Permissions.OrdersManage, ShellAvailability.Available),
        new("Sales orders", "orders/sales-orders", Icons.Material.Filled.Description, GroupSales, Permissions.OrdersManage, ShellAvailability.Available),
        new("Delivery and fulfillment", "orders/fulfillment", Icons.Material.Filled.LocalShipping, GroupSales, Permissions.OrdersManage, ShellAvailability.Available),

        // Purchasing: suppliers, what is owed to them, and what arrived.
        new("Suppliers", "suppliers", Icons.Material.Filled.LocalShipping, GroupPurchasing, Permissions.PurchasingManage, ShellAvailability.Available),
        // Procurement is one journey in three screens: the order, the delivery that makes it stock, and the
        // supplier's bill that makes it a payable. Each row reaches a route that exists.
        new("Purchase orders", "purchasing/orders", Icons.Material.Filled.Description, GroupPurchasing, Permissions.PurchasingManage, ShellAvailability.Available),
        new("Receiving", "purchasing/receiving", Icons.Material.Filled.Inventory, GroupPurchasing, Permissions.PurchasingManage, ShellAvailability.Available),
        new("Payables and supplier credits", "purchasing/payables", Icons.Material.Filled.Payment, GroupPurchasing, Permissions.PurchasingManage, ShellAvailability.Available),

        // Inventory: what is held, where, and what it is worth.
        new("Products and variants", "catalog/products", Icons.Material.Filled.Category, GroupInventory, Permissions.ProductsWrite, ShellAvailability.Available),
        new("Stock and transfers", "inventory/stock", Icons.Material.Filled.Place, GroupInventory, Permissions.ProductsRead, ShellAvailability.Available),
        new("Inter-location transfers", "inventory/transfers", Icons.Material.Filled.SwapHoriz, GroupInventory, Permissions.ProductsRead, ShellAvailability.Available),
        new("Stock counts", "inventory/counts", Icons.Material.Filled.FactCheck, GroupInventory, Permissions.InventoryAdjust, ShellAvailability.Planned,
            "Arrives with task 30."),
        new("Reference lists", "catalog/master-data", Icons.Material.Filled.FormatListBulleted, GroupInventory, Permissions.ProductsRead, ShellAvailability.Available),
        new("Taxes", "catalog/taxes", Icons.Material.Filled.Percent, GroupInventory, Permissions.TaxesManage, ShellAvailability.Available),
        new("Discount policy", "catalog/discounts", Icons.Material.Filled.Sell, GroupInventory, Permissions.DiscountsManage, ShellAvailability.Available),

        // Finance: the customer ledger, money received, and the books.
        new("Customers", "customers", Icons.Material.Filled.People, GroupFinance, Permissions.CustomersRead, ShellAvailability.Available),
        new("Invoices", "invoices", Icons.Material.Filled.ReceiptLong, GroupFinance, Permissions.InvoicesIssue, ShellAvailability.Available),
        // Nav visibility matches what the routes actually authorise: the statement and open-item reads answer to the
        // issuing authority, and only the posting authority can apply a deposit or hand one back (task 25).
        new("Receivables and receipts", "finance/receivables", Icons.Material.Filled.Paid, GroupFinance, Permissions.InvoicesIssue, ShellAvailability.Available),
        new("Bank feed and reconciliation", "finance/banking", Icons.Material.Filled.AccountBalanceWallet, GroupFinance, Permissions.AccountingPost, ShellAvailability.Available),
        new("Expenses", "expenses", Icons.Material.Filled.Receipt, GroupFinance, Permissions.ExpensesManage, ShellAvailability.Available),
        // Accounting: the books behind the money. Each row reaches a page that exists, at the authority that page's
        // routes actually ask for - reading the journal is a reports right, posting is a posting right, and opening,
        // closing or transferring a period belongs to the person who may close a period (specification task 32).
        new("Chart of accounts", "accounting/accounts", Icons.Material.Filled.AccountTree, GroupFinance, Permissions.AccountingPost, ShellAvailability.Available),
        new("Journals and trial balance", "accounting/journals", Icons.Material.Filled.Book, GroupFinance, Permissions.ReportsView, ShellAvailability.Available),
        new("Periods and year close", "accounting/periods", Icons.Material.Filled.DateRange, GroupFinance, Permissions.AccountingClose, ShellAvailability.Available),
        new("Reports", "reports", Icons.Material.Filled.QueryStats, GroupFinance, Permissions.ReportsView, ShellAvailability.Available),

        // Administration: the instance itself.
        new("Business and branches", "administration/business", Icons.Material.Filled.Storefront, GroupAdministration, Permissions.BusinessSettingsManage, ShellAvailability.Available),
        new("Users and handover tickets", "administration/users", Icons.Material.Filled.ManageAccounts, GroupAdministration, Permissions.UsersManage, ShellAvailability.Available),
        new("Terminals", "administration/devices", Icons.Material.Filled.PointOfSale, GroupAdministration, Permissions.DevicesManage, ShellAvailability.Available),
        new("Audit and diagnostics", "administration/audit", Icons.Material.Filled.FactCheck, GroupAdministration, Permissions.AuditView, ShellAvailability.Planned,
            "Arrives with task 34."),
    ];

    /// <summary>The group's entries for one session: permitted only, and never a silently broken link.</summary>
    public static IReadOnlyList<ShellRoute> ForSession(SessionDto? session, string group)
        => Visible(session, group).ToArray();

    /// <summary>True when the session may see the group at all (an empty group is never rendered).</summary>
    public static bool ShowsGroup(SessionDto? session, string group)
        => Visible(session, group).Any();

    /// <summary>
    /// Entries the caller is allowed to see. A planned screen stays listed (with its reason) when the permission
    /// exists, because hiding a gap is how a self-hosted operator discovers it late; an entry the caller has no
    /// permission for is not shown at all, which is the same answer the API gives.
    /// </summary>
    public static IEnumerable<ShellRoute> Visible(SessionDto? session, string? group = null)
    {
        var permissions = session?.Permissions ?? [];

        return Routes.Where(route => (group is null || route.Group == group)
            && (route.RequiredPermission is null || permissions.Contains(route.RequiredPermission, StringComparer.Ordinal)));
    }

    /// <summary>Whether an entry is clickable. Planned and device-only entries are shown disabled.</summary>
    public static bool IsNavigable(ShellRoute route) => route.Availability switch
    {
        ShellAvailability.Available => true,
        _ => false,
    };

    /// <summary>The explanation a disabled entry carries, so a disabled link is never mysterious.</summary>
    public static string? DisabledReason(ShellRoute route) => route.Availability switch
    {
        ShellAvailability.Available => null,
        ShellAvailability.DeviceOnly => route.ArrivalNote ?? "Available on the registered terminal only.",
        _ => route.ArrivalNote ?? "Not built yet.",
    };

    /// <summary>Entries the operator can use right now, in catalogue order.</summary>
    public static IReadOnlyList<ShellRoute> Usable(SessionDto? session)
        => Visible(session).Where(IsNavigable).ToArray();

    /// <summary>
    /// True when <paramref name="href"/> resolves to a route the shell knows. Used by the tests to prove no
    /// navigation entry points at a page that does not exist.
    /// </summary>
    public static bool Knows(string href)
        => Routes.Any(route => string.Equals(route.Href, href, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The states a shared screen can be in. Each one is a different sentence because each one asks the operator for
/// a different action: an empty list, an expired session, a refused permission, a lost race with another editor,
/// a disconnected terminal, and an outcome the server never confirmed (specification task 13).
/// </summary>
public enum ShellStateKind
{
    Content,
    Loading,
    Empty,
    Unauthenticated,
    Forbidden,
    Conflict,
    Offline,
    UnknownOutcome,
    Failure,
}
