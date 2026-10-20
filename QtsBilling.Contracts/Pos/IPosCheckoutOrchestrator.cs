using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;

namespace QtsBilling.Contracts.Pos;

public sealed record PosCheckoutContext(
    IReadOnlyList<BusinessLocationDto> Locations,
    IReadOnlyList<CashRegisterDto> Registers);

public sealed record PosCatalogPage(
    IReadOnlyList<LocalCatalogItem> Items,
    string? NextCursor);

/// <summary>Host-level online POS workflow. The browser posts through typed APIs; it never accesses a database.</summary>
public interface IPosCheckoutOrchestrator
{
    Task<PosCheckoutContext> GetContextAsync(CancellationToken cancellationToken = default);

    Task<CashSessionDetailDto> OpenCashDrawerAsync(
        Guid registerId,
        decimal openingFloat,
        CancellationToken cancellationToken = default);

    Task<PosCatalogPage> GetCatalogAsync(
        Guid locationId,
        string? search = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<SaleCaptureResult> CaptureCashSaleAsync(
        Guid locationId,
        Guid registerId,
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default);
}
