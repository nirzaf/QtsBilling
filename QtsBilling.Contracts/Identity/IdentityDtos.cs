namespace QtsBilling.Contracts.Identity;

/// <summary>Interactive sign-in request (cookie or token endpoint).</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// Current authenticated session. <see cref="BusinessId"/> is the tenant every request is scoped to;
/// a session without an active membership carries no business and can only complete account setup.
/// </summary>
public sealed record SessionDto(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? BusinessId,
    string? BusinessName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool PasswordResetRequired);

/// <summary>Requests a password reset/invitation ticket. The response never reveals whether the account exists.</summary>
public sealed record RequestPasswordResetRequest(string Email);

/// <summary>Completes account setup or password reset using a one-time token.</summary>
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

/// <summary>Development-only ticket delivery (production sends this by e-mail/ops channel).</summary>
public sealed record PasswordResetTicketDto(string Email, string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Permission names used by server authorization policies and checked by clients for UI affordances.
/// The client never enforces security — it only hides actions the server would reject.
/// </summary>
public static class Permissions
{
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string ProductsRead = "products.read";
    public const string ProductsWrite = "products.write";
    public const string InventoryAdjust = "inventory.adjust";
    public const string SalesPost = "sales.post";
    public const string SalesVoid = "sales.void";
    public const string SalesRefund = "sales.refund";
    public const string RegistersManage = "registers.manage";
    public const string AccountingPost = "accounting.post";
    public const string AccountingClose = "accounting.close";
    public const string InvoicesIssue = "invoices.issue";
    public const string PurchasingManage = "purchasing.manage";
    public const string ExpensesManage = "expenses.manage";
    public const string ReportsView = "reports.view";
    public const string DevicesManage = "devices.manage";
    public const string UsersManage = "users.manage";

    public static readonly IReadOnlyList<string> All =
    [
        CustomersRead, CustomersWrite,
        ProductsRead, ProductsWrite,
        InventoryAdjust,
        SalesPost, SalesVoid, SalesRefund,
        RegistersManage,
        AccountingPost, AccountingClose,
        InvoicesIssue,
        PurchasingManage, ExpensesManage,
        ReportsView, DevicesManage, UsersManage,
    ];
}

/// <summary>Role names; the role-to-permission mapping is configured server-side (QB-04).</summary>
public static class RoleNames
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Cashier = "Cashier";
    public const string Accountant = "Accountant";
    public const string Viewer = "Viewer";
}
