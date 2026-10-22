using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Serialization;
using QtsModels.ValueObjects;

namespace QtsWebApp.Client.Services;

/// <summary>
/// Online browser POS orchestration over the authenticated server API. An attempt whose answer was lost is kept
/// exactly as it was sent - same operation id, same body, same drawer - and stored in browser local storage (which
/// survives a refresh or a restart) so the operator resolves that attempt rather than ringing a second sale for the
/// same cash (specification task 20, recipe step 2). The stored envelope holds no credential: no token, no cookie,
/// no card data - only the line identifiers, amounts and the key.
/// </summary>
public sealed class WebPosCheckoutOrchestrator : IPosCheckoutOrchestrator
{
    private const int CatalogPageSize = 25;

    /// <summary>The one local-storage slot the unresolved online attempt lives in.</summary>
    public const string PendingAttemptKey = "qts.pos.pending-attempt";

    private readonly IProductApiClient _products;
    private readonly IInventoryApiClient _inventory;
    private readonly ICashRegisterApiClient _registers;
    private readonly ISaleApiClient _sales;
    private readonly IJSRuntime _js;

    public WebPosCheckoutOrchestrator(
        IProductApiClient products,
        IInventoryApiClient inventory,
        ICashRegisterApiClient registers,
        ISaleApiClient sales,
        IJSRuntime js)
    {
        _products = products;
        _inventory = inventory;
        _registers = registers;
        _sales = sales;
        _js = js;
    }

    public async Task<PosCheckoutContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var locationsTask = _inventory.GetLocationsAsync(cancellationToken);
        var registersTask = _registers.GetRegistersAsync(cancellationToken);
        await Task.WhenAll(locationsTask, registersTask).ConfigureAwait(false);

        return new PosCheckoutContext(
            (await locationsTask.ConfigureAwait(false)).Where(location => location.IsActive).ToArray(),
            (await registersTask.ConfigureAwait(false)).Where(register => register.IsActive).ToArray());
    }

    public Task<CashSessionDetailDto> OpenCashDrawerAsync(
        Guid registerId,
        decimal openingFloat,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => _registers.OpenSessionAsync(
            registerId,
            new OpenCashRegisterSessionRequest(openingFloat, null),
            idempotencyKey,
            cancellationToken);

    public async Task<PosCatalogPage> GetCatalogAsync(
        Guid locationId,
        string? search = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var locations = await _inventory.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        if (!locations.Any(location => location.Id == locationId && location.IsActive))
        {
            throw new ApiException(403, ApiErrorCodes.Forbidden, "Choose an active location available to this account.");
        }

        var productPage = await _products.GetProductsAsync(
            search: search,
            cursor: cursor,
            pageSize: CatalogPageSize,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var balances = new Dictionary<Guid, decimal>();
        string? balanceCursor = null;
        do
        {
            var balancePage = await _inventory.GetBalancesAsync(
                search: search,
                cursor: balanceCursor,
                pageSize: 200,
                locationId: locationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var balance in balancePage.Items)
            {
                balances[balance.VariantId] = balance.OnHand;
            }
            balanceCursor = balancePage.NextCursor;
        }
        while (balanceCursor is not null);

        var items = new List<LocalCatalogItem>();
        foreach (var product in productPage.Items.Where(product => product.IsActive))
        {
            var detail = await _products.GetProductAsync(product.Id, cancellationToken).ConfigureAwait(false);
            var taxMode = CommercialAmountCalculator.ParseTaxMode(detail.Product.TaxType, "taxType");
            foreach (var variant in detail.Variants.Where(variant => variant.IsActive))
            {
                items.Add(new LocalCatalogItem(
                    variant.Id,
                    variant.Sku ?? detail.Product.Sku ?? string.Empty,
                    string.IsNullOrWhiteSpace(variant.Name) ? detail.Product.Name : variant.Name,
                    variant.SellingPrice,
                    detail.Product.TaxRate,
                    balances.TryGetValue(variant.Id, out var onHand) ? onHand : null,
                    true,
                    variant.Version)
                {
                    TaxMode = taxMode,
                    PricingPolicyVersion = detail.Product.Version.ToString(CultureInfo.InvariantCulture),
                });
            }
        }

        return new PosCatalogPage(items, productPage.NextCursor);
    }

    public async Task<SaleCaptureResult> CaptureCashSaleAsync(
        Guid locationId,
        Guid registerId,
        LocalCashSaleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.OperationId == Guid.Empty || draft.Lines.Count == 0)
        {
            throw new ArgumentException("A sale operation id and at least one line are required.", nameof(draft));
        }

        var registers = await _registers.GetRegistersAsync(cancellationToken).ConfigureAwait(false);
        var register = registers.FirstOrDefault(candidate => candidate.Id == registerId
            && candidate.IsActive
            && candidate.LocationId == locationId
            && candidate.CurrentSession is { Status: CashRegisterSessionState.Open });
        if (register?.CurrentSession is not { } session)
        {
            throw new ApiException(409, ApiErrorCodes.CashRegisterNotOpen, "The selected cash register no longer has an open drawer session. Refresh the POS and choose an open register.");
        }

        var lines = draft.Lines.Select(line => new SaleLineRequest(
            line.VariantId,
            line.Quantity,
            line.UnitPrice,
            line.Discount,
            line.TaxRate,
            line.ExpectedCatalogRevision.ToString(CultureInfo.InvariantCulture))
        {
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        }).ToArray();

        var payable = draft.Lines.Sum(line => CommercialAmountCalculator.ComputeLine(
            line.Quantity,
            line.UnitPrice,
            line.TaxMode,
            line.TaxRate,
            line.Discount,
            line.PricingPolicyVersion,
            "lines").PayableAmount);

        var request = new SaleCaptureRequest(
            locationId,
            null,
            session.Currency,
            lines,
            new PaymentCaptureRequest("Cash", payable, draft.CashReceived, null),
            draft.Note,
            registerId);

        // The same operation id and unchanged draft are retained by the UI after uncertain outcomes: the server
        // replays its stored receipt for that key, so a retry resolves the sale instead of duplicating it. A
        // failure that never reached the server - or whose answer never came back - is reported as the distinct
        // outcome-unknown contract code rather than as a refusal (specification task 20, recipe step 2).
        SaleCaptureResult result;

        try
        {
            result = await _sales.PostSaleAsync(request, draft.OperationId.ToString("N"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ApiException(
                408,
                ApiErrorCodes.TransientRetry,
                $"The sale's outcome is unknown ({exception.GetType().Name}: the request could not be completed or its answer was lost). "
                    + $"The attempt is kept - key {draft.OperationId:N} and the same lines - so the cashier must check this same sale before taking money for another one.");
        }

        return result;
    }

    public Task<SaleReceiptDto> GetPostedReceiptAsync(Guid saleId, CancellationToken cancellationToken = default)
        => _sales.GetReceiptAsync(saleId, cancellationToken);

    public async Task<PendingCheckoutAttempt?> ReadPendingAttemptAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _js.InvokeAsync<string?>("localStorage.getItem", PendingAttemptKey).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingCheckoutAttempt>(stored, ContractJson.Options);
        }
        catch (JsonException exception)
        {
            // A damaged envelope is reported, never dropped: the sale it described may already be committed, and the
            // screen has to say so before the cashier takes money for a second one. The stored text is left exactly
            // where it is, so nothing is lost while the operator checks the register's history
            // (specification task 20, recipe step 2).
            throw new InvalidOperationException(
                $"the stored checkout attempt could not be read ({exception.Message}) - the sale it describes may already be posted",
                exception);
        }
    }

    public Task SavePendingAttemptAsync(PendingCheckoutAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return _js.InvokeVoidAsync(
            "localStorage.setItem",
            PendingAttemptKey,
            JsonSerializer.Serialize(attempt, ContractJson.Options)).AsTask();
    }

    public Task ClearPendingAttemptAsync(CancellationToken cancellationToken = default)
        => _js.InvokeVoidAsync("localStorage.removeItem", PendingAttemptKey).AsTask();
}
