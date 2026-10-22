using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Reports;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Inventory;

/// <summary>
/// What each branch holds, what it is worth, and what is between branches right now (specification task 29).
/// Quantities come from the movement-backed balances, value from the posted stock summary, and in-transit from
/// the transfers the server has already valued at dispatch - no figure on this screen is typed, repaired or
/// borrowed from a device that is still offline.
/// </summary>
public partial class Stock : IAsyncDisposable
{
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;

    private const int PageSize = 25;

    private readonly PagedListState<InventoryBalanceDto> _list;

    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private StockSummaryDto? _summary;
    private InventoryReconciliationDto? _reconciliation;
    private IReadOnlyList<StockTransferSummaryDto> _transfers = [];

    private Guid? _locationId;
    private string? _searchBuffer;
    private bool _canRead;
    private bool _canReport;
    private bool _started;
    private bool _summaryFailed;
    private int _loadVersion;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;

    private InventoryBalanceDto? _movementRow;
    private IReadOnlyList<InventoryMovementDto> _movements = [];
    private bool _movementsBusy;
    private string? _movementsError;

    public Stock()
    {
        _list = new PagedListState<InventoryBalanceDto>(LoadPageAsync, StateHasChanged, PageSize);
    }

    /// <summary>The value still between branches: dispatched transfers, valued at dispatch, not yet received.</summary>
    private IEnumerable<StockTransferSummaryDto> InTransit => _transfers
        .Where(transfer => transfer.Status == StockTransferState.Dispatched);

    private decimal InTransitQuantity => InTransit.Sum(transfer => transfer.TotalQuantity);

    private decimal InTransitValue => InTransit.Sum(transfer => transfer.TotalValue ?? 0m);

    private bool AnyInTransit => InTransit.Any();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _state = ShellStateKind.Loading;
        _message = null;
        _summary = null;
        _reconciliation = null;
        _summaryFailed = false;

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canRead = session.Permissions.Contains(Permissions.ProductsRead, StringComparer.Ordinal);
            _canReport = session.Permissions.Contains(Permissions.ReportsView, StringComparer.Ordinal);

            if (!_canRead)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading stock needs the '{Permissions.ProductsRead}' authority, which this account does not hold in this business.";
                return;
            }

            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);
            _transfers = await Inventory.GetTransfersAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            // Value and reconciliation sit behind the reporting authority: without it the screen shows
            // quantities honestly and says why no money figure appears, rather than estimating one.
            if (_canReport)
            {
                try
                {
                    _summary = await Inventory.GetStockSummaryAsync(locationId: _locationId).ConfigureAwait(false);
                    _reconciliation = await Inventory.GetReconciliationAsync().ConfigureAwait(false);
                }
                catch (ApiException exception) when (exception.StatusCode is 401 or 403)
                {
                    _summaryFailed = true;
                }
            }

            if (version != _loadVersion)
            {
                return;
            }

            await _list.ReloadAsync().ConfigureAwait(false);
            _state = _list.State;
            _message = _list.Message;
        }
        catch (ApiException exception)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                _ => ShellStateKind.Failure,
            };
            _message = StockRules.RefusalSentence(exception, "the stock screen");
        }
        catch (Exception exception)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _state = ShellStateKind.Failure;
            _message = exception.Message;
        }
        finally
        {
            if (version == _loadVersion)
            {
                StateHasChanged();
            }
        }
    }

    private async Task<PagedRows<InventoryBalanceDto>> LoadPageAsync(string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await Inventory.GetBalancesAsync(search, cursor, pageSize, _locationId, variantId: null, token)
            .ConfigureAwait(false);

        return new PagedRows<InventoryBalanceDto>(page.Items, page.NextCursor);
    }

    private async Task LocationChanged(Guid? locationId)
    {
        _locationId = locationId;
        _movementRow = null;

        // One reload re-reads the balances for the new branch and, when the session holds the reporting
        // authority, the value and reconciliation figures beside them - never a mixture of two branches.
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task SearchChanged(string? term)
    {
        _movementRow = null;
        await _list.SearchAsync(term).ConfigureAwait(false);
        SyncListState();
    }

    /// <summary>Paging and search repaint through the list's own state, so the screen never shows one state's body under another's heading.</summary>
    private async Task NextPageAsync()
    {
        await _list.NextPageAsync().ConfigureAwait(false);
        SyncListState();
    }

    private async Task PreviousPageAsync()
    {
        await _list.PreviousPageAsync().ConfigureAwait(false);
        SyncListState();
    }

    private void SyncListState()
    {
        _state = _list.State;
        _message = _list.Message;
    }

    private Task ShowMovementsAsync(InventoryBalanceDto balance) => OpenMovementsAsync(balance);

    private async Task OpenMovementsAsync(InventoryBalanceDto balance)
    {
        _movementRow = balance;
        _movements = [];
        _movementsError = null;
        _movementsBusy = true;
        StateHasChanged();

        try
        {
            var page = await Inventory.GetMovementsAsync(
                variantId: balance.VariantId,
                locationId: balance.LocationId,
                cursor: null,
                pageSize: 50).ConfigureAwait(false);
            _movements = page.Items;
        }
        catch (ApiException exception)
        {
            _movementsError = StockRules.RefusalSentence(exception, "the movement history");
        }
        catch (Exception exception)
        {
            _movementsError = $"The movement history could not be read: {exception.Message}";
        }
        finally
        {
            _movementsBusy = false;
            StateHasChanged();
        }
    }

    private void CloseMovements()
    {
        _movementRow = null;
        _movements = [];
        _movementsError = null;
    }

    private string LocationName(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.Name ?? "that branch";

    /// <summary>
    /// The item behind a reconciliation exception, named the way an operator knows it. The exception rows carry
    /// identifiers only, so the stock summary - the same authority that may see the exceptions - resolves the
    /// name; an identifier is never put on the screen for a person to recognise.
    /// </summary>
    private string VariantLabel(Guid variantId)
        => _summary?.Rows.FirstOrDefault(row => row.VariantId == variantId) is { } row
            ? string.IsNullOrWhiteSpace(row.Sku) ? row.VariantName : $"{row.VariantName} ({row.Sku})"
            : "an item no longer in the stock summary";

    public async ValueTask DisposeAsync()
    {
        await _list.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The stock screen's own words (specification task 29), shared with the transfers screen so both say the
/// same thing about custody, value and reconciliation - and neither ever offers to repair a balance.
/// </summary>
public static class StockRules
{
    public const int MaxNoteLength = 2_000;

    /// <summary>What dispatch and receipt do to stock and value - the custody the transfers screen enforces.</summary>
    public const string CustodyRule =
        "A transfer is custody, not a copy: dispatching reduces the source and parks the value in the "
        + "inventory-in-transit account at the cost the source held, and receipt is the only thing that puts the "
        + "stock on the destination shelf - at that same cost. Nothing is repriced on the way, and a dispatch "
        + "that would leave the source short is refused whole.";

    /// <summary>Why no offline estimate ever appears in these figures.</summary>
    public const string ProvisionalRule =
        "Every figure here comes from posted movements: provisional estimates a device holds while it is "
        + "offline are never mixed into on-hand, value or in-transit, because they are not yet facts.";

    /// <summary>What a reconciliation difference is, and what this screen will never do about it.</summary>
    public const string ReconciliationRule =
        "A difference between a balance and its movement history is an exception to investigate - the "
        + "movements are the record. This screen never repairs a movement-backed value, and no button here "
        + "overwrites what the ledger says.";

    /// <summary>How a short or over arrival at the far end is handled instead of editing a transfer.</summary>
    public const string DiscrepancyRule =
        "If the far end discovers a shortage or an overage, it is recorded as a reviewed adjustment against "
        + "the destination - a transfer that already posted keeps its lines, its valuation and its history.";

    public static string StateText(StockTransferState state) => state switch
    {
        StockTransferState.Draft => "draft, not yet dispatched",
        StockTransferState.Dispatched => "dispatched, in transit",
        StockTransferState.Received => "received",
        _ => state.ToString(),
    };

    /// <summary>Money at the register's own scale, so a value on this screen reads the way cash does.</summary>
    public static string NiceMoney(decimal value) => Sales.RegisterRules.Money(value);

    /// <summary>
    /// What the transfers queue prints in its value column. A draft has no posted valuation - the list may
    /// answer with nothing for it - so the screen shows no money figure until dispatch has priced the lines.
    /// </summary>
    public static string QueueValueText(StockTransferSummaryDto transfer)
        => transfer.Status == StockTransferState.Draft || transfer.TotalValue is not { } value
            ? "-"
            : $"{NiceMoney(value)} {transfer.Currency}";

    public static string MovementText(StockMovementKind kind) => kind switch
    {
        StockMovementKind.Opening => "opening",
        StockMovementKind.PurchaseReceipt => "purchase receipt",
        StockMovementKind.Sale => "sale",
        StockMovementKind.SaleReversal => "sale reversal",
        StockMovementKind.PurchaseReturn => "purchase return",
        StockMovementKind.StockAdjustment => "reviewed adjustment",
        StockMovementKind.TransferIn => "transfer received",
        StockMovementKind.TransferOut => "transfer dispatched",
        _ => kind.ToString(),
    };

    /// <summary>Why a refusal happened, with the action that answers it, in the operator's words.</summary>
    public static string RefusalSentence(ApiException error, string action) => error.Code switch
    {
        ApiErrorCodes.Forbidden =>
            $"This membership cannot reach both ends of that transfer, so {action} was refused. Grant access to both locations; nothing moved.",
        ApiErrorCodes.NotFound =>
            $"That transfer is not in this business, so {action} had nothing to act on. Nothing was posted.",
        ApiErrorCodes.SameKeyDifferentPayload =>
            $"This key already recorded a different transfer, so {action} was not posted twice. Start a new intention instead of changing the body under the old key.",
        ApiErrorCodes.ValuationRequired =>
            $"The source stock carries no value, so {action} was refused before anything moved. Value the stock at its source location first.",
        ApiErrorCodes.InsufficientStock =>
            $"The source does not hold that quantity, so {action} was refused whole and the draft stayed as it was.",
        ApiErrorCodes.OptimisticConcurrencyFailure =>
            $"Someone else changed this transfer while it was posting, so {action} was refused. Read the transfer back and decide again.",
        ApiErrorCodes.AccountingPeriodClosed =>
            $"The period for that posting date is closed, so {action} was refused. No movement or journal was written outside an open period.",
        ApiErrorCodes.Conflict =>
            $"The transfer is not in the state {action} needs, or it changed while being read: {error.Message} Read it back and follow its current state.",
        ApiErrorCodes.ValidationFailed =>
            $"The transfer payload was refused before anything was written: {error.Message}",
        _ => $"The server refused {action}: {error.Message}",
    };

    /// <summary>
    /// True only for codes that prove nothing was written: every refusal above is raised inside the posting
    /// transaction (which rolls back) or during validation, so the intention may be released. An internal
    /// error or an unknown answer is never treated as a clean refusal.
    /// </summary>
    public static bool CommittedNothing(string? code) => code switch
    {
        ApiErrorCodes.Forbidden => true,
        ApiErrorCodes.NotFound => true,
        ApiErrorCodes.SameKeyDifferentPayload => true,
        ApiErrorCodes.ValuationRequired => true,
        ApiErrorCodes.InsufficientStock => true,
        ApiErrorCodes.OptimisticConcurrencyFailure => true,
        ApiErrorCodes.AccountingPeriodClosed => true,
        ApiErrorCodes.Conflict => true,
        ApiErrorCodes.ValidationFailed => true,
        _ => false,
    };

    /// <summary>One key per transfer draft, bounded by the header the server validates.</summary>
    public static string NewKey(string intent, string seed)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var key = $"{intent}-{seed}-{suffix}";
        return key.Length <= 64 ? key : key[..64];
    }
}
