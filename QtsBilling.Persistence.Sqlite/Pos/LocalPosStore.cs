using System.Text.Json;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Sqlite.Entities;

namespace QtsBilling.Persistence.Sqlite.Pos;

/// <summary>Who is capturing, and where. Supplied by the host that owns the device identity.</summary>
public sealed record LocalDeviceContext(
    Guid DeviceId,
    Guid BusinessId,
    Guid LocationId,
    Guid UserId,
    Guid RegisterId);

/// <summary>
/// The device-local point of sale: it captures cash sales into the local queue and reads back the
/// pending list. It never reaches the network - refreshing the catalog is the host's job - and it never
/// reports a capture as accepted unless the money rows are durably committed (QB-19).
/// </summary>
public sealed class LocalPosStore
{
    /// <summary>Operation type of a locally captured cash sale (specification §4.4).</summary>
    public const string CashSaleOperationType = "pos.sale.capture.v1";

    private readonly SqliteLocalStore _store;

    public LocalPosStore(SqliteLocalStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>The cached, locally sellable catalog with the last known stock.</summary>
    public async Task<IReadOnlyList<LocalCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var products = await _store.GetCachedProductsAsync(cancellationToken).ConfigureAwait(false);
        var stockRows = await _store.GetCachedStockAsync(cancellationToken).ConfigureAwait(false);
        var stock = stockRows
            .GroupBy(row => row.VariantId)
            .ToDictionary(group => group.Key, group => group.First().ServerQuantity);

        return
        [
            .. products.Select(product => new LocalCatalogItem(
                product.ProductId,
                product.Sku,
                product.Name,
                product.Price,
                product.TaxRate,
                stock.TryGetValue(product.ProductId, out var onHand) ? onHand : null,
                product.Active,
                product.Revision))
        ];
    }

    /// <summary>
    /// Captures a cash sale into the local queue. The draft, its lines, the cash movement and the outbox
    /// row are committed in one SQLite transaction; a failure leaves nothing behind and is reported as a
    /// rejection, never as a success.
    /// </summary>
    public async Task<LocalSaleCaptureResult> CaptureCashSaleAsync(
        LocalDeviceContext context,
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.OperationId == Guid.Empty)
        {
            return Rejected(draft, "A stable operation id is required.");
        }

        if (draft.Lines is null || draft.Lines.Count == 0)
        {
            return Rejected(draft, "A sale requires at least one line.");
        }

        if (draft.CashReceived < 0m)
        {
            return Rejected(draft, "The cash received cannot be negative.");
        }

        var total = Total(draft.Lines);

        if (total <= 0m)
        {
            return Rejected(draft, "A sale must have a positive total.");
        }

        if (draft.CashReceived < total)
        {
            return Rejected(draft, $"The cash received ({draft.CashReceived}) does not cover the total ({total}).");
        }

        var payload = BuildCanonicalPayload(context, draft, total);

        try
        {
            await _store.CaptureProvisionalCashSaleAsync(
                new ProvisionalCashSaleInput
                {
                    OperationId = draft.OperationId,
                    DeviceId = context.DeviceId,
                    BusinessId = context.BusinessId,
                    LocationId = context.LocationId,
                    UserId = context.UserId,
                    RegisterId = context.RegisterId,
                    Amount = draft.CashReceived,
                    CanonicalPayload = payload.Canonical,
                    Sha256Fingerprint = payload.Fingerprint,
                    Lines =
                    [
                        .. draft.Lines.Select(line => new ProvisionalSaleLineInput(
                            line.VariantId,
                            line.Quantity,
                            line.UnitPrice,
                            line.Tax,
                            line.Discount,
                            line.ExpectedCatalogRevision))
                    ],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The transaction rolled back: nothing was stored, so nothing may be reported as captured.
            return new LocalSaleCaptureResult(
                false,
                draft.OperationId,
                null,
                total,
                LocalCaptureCodes.Rejected,
                $"The sale could not be stored on this device: {exception.Message}");
        }

        return new LocalSaleCaptureResult(
            true,
            draft.OperationId,
            $"PROV-{draft.OperationId:N}",
            total);
    }

    /// <summary>The locally captured sales that are still waiting for the server.</summary>
    public async Task<IReadOnlyList<LocalPendingSale>> GetPendingSalesAsync(CancellationToken cancellationToken = default)
    {
        var drafts = await _store.GetSaleDraftsAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. drafts.Select(entry => new LocalPendingSale(
                entry.Draft.OperationId,
                entry.Draft.ProvisionalReference,
                entry.Draft.CreatedAtUtc,
                entry.Draft.Amount,
                entry.Draft.State.ToString(),
                entry.Draft.LastError,
                [
                    .. entry.Lines.Select(line => new LocalSaleLineDraft(
                        line.Ordinal,
                        line.VariantId,
                        line.Quantity,
                        line.UnitPrice,
                        TaxRateOf(line),
                        line.Tax,
                        line.Discount,
                        line.ExpectedCatalogRevision))
                ]))
        ];
    }

    /// <summary>Queue status: what is waiting, what was refused, how much cash is at risk.</summary>
    public async Task<LocalQueueSummary> GetQueueSummaryAsync(CancellationToken cancellationToken = default)
    {
        var summary = await _store.GetPendingSummaryAsync(cancellationToken).ConfigureAwait(false);
        return new LocalQueueSummary(summary.PendingOperations, summary.RejectedOperations, summary.ProvisionalCashAtRisk);
    }

    /// <summary>
    /// Replaces the cached catalog and the cached stock for one business/location. The cache is rebuilt
    /// first: a device that was enrolled to another business must not keep the rows of that business.
    /// </summary>
    public async Task<int> ReplaceCatalogAsync(
        Guid businessId,
        Guid locationId,
        IReadOnlyList<LocalCatalogItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _store.RebuildCacheAsync(cancellationToken).ConfigureAwait(false);

        var products = new List<CachedProduct>(items.Count);
        var stock = new List<CachedStock>(items.Count);

        foreach (var item in items)
        {
            products.Add(new CachedProduct
            {
                BusinessId = businessId,
                LocationId = locationId,
                ProductId = item.VariantId,
                Sku = item.Sku,
                Name = item.Name,
                Price = item.UnitPrice,
                TaxRate = item.TaxRate,
                Active = item.Active,
                Revision = item.Revision,
            });

            if (item.OnHand is { } onHand)
            {
                stock.Add(new CachedStock
                {
                    BusinessId = businessId,
                    LocationId = locationId,
                    VariantId = item.VariantId,
                    ServerQuantity = onHand,
                    Revision = item.Revision,
                });
            }
        }

        await _store.ReplaceCachedProductsAsync(businessId, locationId, products, cancellationToken).ConfigureAwait(false);
        await _store.ReplaceCachedStockAsync(businessId, locationId, stock, cancellationToken).ConfigureAwait(false);
        return products.Count;
    }

    /// <summary>The sale total computed from the captured lines (net of discount, including tax).</summary>
    public static decimal Total(IReadOnlyList<LocalSaleLineDraft> lines)
    {
        var total = 0m;

        foreach (var line in lines)
        {
            var gross = line.Quantity * line.UnitPrice;
            var net = gross - line.Discount;
            total += net + line.Tax;
        }

        return total;
    }

    /// <summary>
    /// The store keeps the tax amount of a line; the rate is derived again for display and for the
    /// pending list. A free line has no rate.
    /// </summary>
    private static decimal TaxRateOf(LocalSaleLine line)
    {
        var gross = line.Quantity * line.UnitPrice;

        if (gross == 0m)
        {
            return 0m;
        }

        return line.Tax / gross;
    }

    private static LocalSaleCaptureResult Rejected(LocalCashSaleDraft draft, string message)
        => new(false, draft.OperationId, null, 0m, LocalCaptureCodes.Rejected, message);

    private static (string Canonical, string Fingerprint) BuildCanonicalPayload(
        LocalDeviceContext context,
        LocalCashSaleDraft draft,
        decimal total)
    {
        var element = JsonSerializer.SerializeToElement(
            new
            {
                type = CashSaleOperationType,
                version = 1,
                deviceId = context.DeviceId,
                businessId = context.BusinessId,
                locationId = context.LocationId,
                userId = context.UserId,
                registerId = context.RegisterId,
                operationId = draft.OperationId,
                paymentMethod = "cash",
                cashReceived = draft.CashReceived,
                total,
                note = draft.Note,
                lines = draft.Lines.Select(line => new
                {
                    ordinal = line.Ordinal,
                    variantId = line.VariantId,
                    quantity = line.Quantity,
                    unitPrice = line.UnitPrice,
                    taxRate = line.TaxRate,
                    tax = line.Tax,
                    discount = line.Discount,
                    expectedCatalogRevision = line.ExpectedCatalogRevision,
                }).ToArray(),
            },
            ContractJson.Options);

        var canonical = CanonicalJson.ToCanonicalString(element);
        return (canonical, CanonicalJson.ComputeFingerprint(canonical));
    }
}
