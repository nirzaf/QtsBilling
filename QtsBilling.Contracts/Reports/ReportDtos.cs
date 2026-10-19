namespace QtsBilling.Contracts.Reports;

/// <summary>
/// Statements are derived exclusively from posted journal lines and inventory movements - never from a
/// stored balance. Every statement carries the business it was computed for so scoping is auditable, and
/// states explicitly whether provisional offline captures are included (they never are: device-local
/// provisional money only becomes authoritative after it posts through the idempotent write paths).
/// </summary>
public sealed record StatementAccountDto(
    Guid AccountId,
    string Code,
    string Name,
    decimal Amount);

public sealed record BalanceSheetDto(
    Guid BusinessId,
    DateOnly AsOf,
    string Currency,
    IReadOnlyList<StatementAccountDto> Assets,
    IReadOnlyList<StatementAccountDto> Liabilities,
    IReadOnlyList<StatementAccountDto> Equity,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalEquity,
    decimal CurrentEarnings,
    decimal TotalLiabilitiesAndEquity,
    bool Balanced,
    bool IncludesProvisional);

public sealed record ProfitAndLossDto(
    Guid BusinessId,
    DateOnly From,
    DateOnly To,
    string Currency,
    IReadOnlyList<StatementAccountDto> Revenue,
    IReadOnlyList<StatementAccountDto> Expenses,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetProfit,
    bool IncludesProvisional);

public sealed record StockSummaryRowDto(
    Guid LocationId,
    string LocationName,
    Guid VariantId,
    string VariantName,
    string? Sku,
    decimal OnHand,
    decimal StockValue,
    DateTimeOffset? LastMovementAtUtc);

public sealed record StockSummaryDto(
    Guid BusinessId,
    DateOnly AsOf,
    string Currency,
    IReadOnlyList<StockSummaryRowDto> Rows,
    decimal TotalQuantity,
    decimal TotalValue,
    bool IncludesProvisional);

public sealed record CashSummaryRowDto(
    Guid AccountId,
    string Code,
    string Name,
    decimal OpeningBalance,
    decimal Debits,
    decimal Credits,
    decimal ClosingBalance);

public sealed record CashSummaryDto(
    Guid BusinessId,
    DateOnly From,
    DateOnly To,
    string Currency,
    IReadOnlyList<CashSummaryRowDto> Accounts,
    decimal TotalOpening,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal TotalClosing,
    bool IncludesProvisional);

/// <summary>Confirmed money received per method inside the window; unconfirmed or voided money never counts.</summary>
public sealed record PaymentMethodTotalDto(
    string Method,
    int Count,
    decimal Amount);

public sealed record SalesSummaryDto(
    Guid BusinessId,
    DateOnly From,
    DateOnly To,
    string Currency,
    int SalesCount,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Total,
    IReadOnlyList<PaymentMethodTotalDto> PaymentsByMethod,
    bool IncludesProvisional);
