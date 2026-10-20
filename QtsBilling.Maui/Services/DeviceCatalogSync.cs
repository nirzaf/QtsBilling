using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Sqlite.Pos;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Pulls the sellable catalog and the last known stock into the device-local cache and resolves the
/// site (location and cash register) the device is enrolled to. This is the device-side half of the
/// master-data synchronization; the change feed with revisions replaces it under QB-21.
/// </summary>
public sealed class DeviceCatalogSync
{
    private const int MaxProducts = 50;

    private readonly IProductApiClient _products;
    private readonly IInventoryApiClient _inventory;
    private readonly ICashRegisterApiClient _registers;
    private readonly IServerSettings _settings;
    private readonly LocalPosStore _pos;

    public DeviceCatalogSync(
        IProductApiClient products,
        IInventoryApiClient inventory,
        ICashRegisterApiClient registers,
        IServerSettings settings,
        LocalPosStore pos)
    {
        _products = products;
        _inventory = inventory;
        _registers = registers;
        _settings = settings;
        _pos = pos;
    }

    public async Task<int> RefreshAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        var locationId = await ResolveLocationAsync(cancellationToken).ConfigureAwait(false);
        await ResolveRegisterAsync(locationId, cancellationToken).ConfigureAwait(false);

        var balances = await _inventory.GetBalancesAsync(pageSize: 200, locationId: locationId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var onHand = balances.Items
            .GroupBy(balance => balance.VariantId)
            .ToDictionary(group => group.Key, group => group.First().OnHand);

        var items = new List<LocalCatalogItem>();
        string? cursor = null;
        var productCount = 0;

        do
        {
            var page = await _products.GetProductsAsync(cursor: cursor, pageSize: 25, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var product in page.Items)
            {
                if (productCount++ >= MaxProducts)
                {
                    break;
                }

                var detail = await _products.GetProductAsync(product.Id, cancellationToken).ConfigureAwait(false);

                foreach (var variant in detail.Variants.Where(variant => variant.IsActive))
                {
                    items.Add(new LocalCatalogItem(
                        variant.Id,
                        variant.Sku ?? product.Sku ?? string.Empty,
                        variant.Name,
                        variant.SellingPrice,
                        product.TaxRate,
                        onHand.TryGetValue(variant.Id, out var quantity) ? quantity : null,
                        variant.IsActive,
                        variant.Version)
                    {
                        TaxMode = CommercialAmountCalculator.ParseTaxMode(detail.Product.TaxType, "taxType"),
                        PricingPolicyVersion = detail.Product.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null && productCount < MaxProducts);

        await _pos.ReplaceCatalogAsync(businessId, locationId, items, cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    private async Task<Guid> ResolveLocationAsync(CancellationToken cancellationToken)
    {
        var locations = await _inventory.GetLocationsAsync(cancellationToken).ConfigureAwait(false);

        // The configured location is only kept when the server still knows it: a device that was
        // re-enrolled to another business must not keep the old location.
        if (_settings.LocationId is { } configured && configured != Guid.Empty
            && locations.Any(candidate => candidate.Id == configured && candidate.IsActive))
        {
            return configured;
        }

        var location = locations.FirstOrDefault(candidate => candidate.IsActive)
            ?? throw new ApiException(503, ApiErrorCodes.StoreUnavailable, "This business has no active location for the device.");

        _settings.LocationId = location.Id;
        return location.Id;
    }

    private async Task ResolveRegisterAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var registers = await _registers.GetRegistersAsync(cancellationToken).ConfigureAwait(false);

        // The same rule as for the location: a register of another business is never reused.
        var register = _settings.RegisterId is { } configured && configured != Guid.Empty
            ? registers.FirstOrDefault(candidate =>
                candidate.Id == configured && candidate.IsActive && candidate.LocationId == locationId)
            : null;
        register ??= registers.FirstOrDefault(candidate => candidate.IsActive && candidate.LocationId == locationId);

        if (register is null)
        {
            throw new ApiException(503, ApiErrorCodes.StoreUnavailable,
                "This business has no active cash register for the device location.");
        }

        _settings.RegisterId = register.Id;
        _settings.RegisterSessionId = register.CurrentSession is { Status: CashRegisterSessionState.Open } session
            ? session.Id
            : null;
    }
}
