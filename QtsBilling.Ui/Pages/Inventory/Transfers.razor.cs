using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Inventory;

/// <summary>
/// Valued inter-location transfers (specification task 29): a draft names its two branches and its lines,
/// dispatch reduces the source and parks the value in transit at the cost the source held, and receipt is the
/// only thing that stocks the destination. Dispatch and receipt are explicit controlled transitions - the
/// screen never fakes an arrival for a branch that has not acknowledged it, and never edits a transfer that
/// already posted.
/// </summary>
public partial class Transfers : IAsyncDisposable
{
    [Parameter] public Guid Id { get; set; }

    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private sealed record LineDraft(InventoryBalanceDto Stock, decimal Quantity);

    private IReadOnlyList<StockTransferSummaryDto> _transfers = [];
    private StockTransferDetailDto? _detail;
    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private readonly List<LineDraft> _lines = [];

    private Guid? _sourceId;
    private Guid? _destinationId;
    private InventoryBalanceDto? _picked;
    private decimal _pickedQuantity = 1m;
    private string? _note;
    private string? _createKey;

    private bool _canRead;
    private bool _canAdjust;
    private bool _started;
    private bool _busy;
    private bool _showCreate;
    private Guid? _loadedId;
    private int _loadVersion;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _error;
    private string? _notice;
    private bool _errorRetry;

    private bool IsQueue => Id == Guid.Empty;

    /// <summary>An unanswered draft is held by this screen: its key and its exact body stay as they were.</summary>
    private bool AwaitingAnswer => _createKey is not null;

    private string KeyHint => AwaitingAnswer
        ? "This draft is still awaiting its answer. Its lines are held exactly as they were: pressing again asks the server about the same draft, so one transfer is never created twice."
        : "One key per draft: press once, and if the answer is lost the same draft is asked about again rather than a new one invented.";

    private IEnumerable<StockTransferSummaryDto> Dispatched => _transfers
        .Where(transfer => transfer.Status == StockTransferState.Dispatched);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // Queue and detail share one component, so a route change - in either direction - is a new read.
        if (_started && _loadedId != Id)
        {
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _loadedId = Id;
        _state = ShellStateKind.Loading;
        _message = null;
        _error = null;
        _detail = null;

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
            _canAdjust = session.Permissions.Contains(Permissions.InventoryAdjust, StringComparer.Ordinal);

            if (!_canRead)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading transfers needs the '{Permissions.ProductsRead}' authority, which this account does not hold in this business.";
                return;
            }

            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (IsQueue)
            {
                _transfers = await Inventory.GetTransfersAsync().ConfigureAwait(false);

                if (version != _loadVersion)
                {
                    return;
                }

                _state = _transfers.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
                _message = _transfers.Count == 0
                    ? "No inter-location transfer exists in this business yet. Draft one with 'New transfer'; nothing moves until it is dispatched."
                    : null;
                return;
            }

            try
            {
                _detail = await Inventory.GetTransferAsync(Id).ConfigureAwait(false);
            }
            catch (ApiException exception) when (exception.StatusCode is 403)
            {
                if (version != _loadVersion)
                {
                    return;
                }

                _state = ShellStateKind.Forbidden;
                _message = StockRules.RefusalSentence(exception, "this transfer");
                return;
            }

            if (version != _loadVersion)
            {
                return;
            }

            _state = ShellStateKind.Content;
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
                404 => ShellStateKind.Empty,
                _ => ShellStateKind.Failure,
            };
            _message = StockRules.RefusalSentence(exception, IsQueue ? "the transfer list" : "this transfer");
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

    // ---------------------------------------------------------------- draft creation

    private async Task<IEnumerable<InventoryBalanceDto>> SearchSourceStock(string? text, CancellationToken cancellationToken)
    {
        if (_sourceId is not { } location)
        {
            return [];
        }

        try
        {
            var page = await Inventory.GetBalancesAsync(text, cursor: null, pageSize: 20, locationId: location,
                variantId: null, cancellationToken).ConfigureAwait(false);
            return page.Items;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string BalanceText(InventoryBalanceDto balance)
        => string.IsNullOrWhiteSpace(balance.Sku)
            ? $"{balance.VariantName} - {balance.OnHand:0.######} on hand"
            : $"{balance.VariantName} ({balance.Sku}) - {balance.OnHand:0.######} on hand";

    private Task SourceChanged(Guid? locationId)
    {
        _sourceId = locationId;
        _picked = null;
        _lines.Clear();
        return Task.CompletedTask;
    }

    private Task DestinationChanged(Guid? locationId)
    {
        _destinationId = locationId;
        return Task.CompletedTask;
    }

    private Task PickedChanged(InventoryBalanceDto? balance)
    {
        _picked = balance;
        return Task.CompletedTask;
    }

    private void AddLine()
    {
        _error = null;

        if (_picked is not { } stock)
        {
            _error = "Choose an item from the source branch's stock first.";
            return;
        }

        if (_pickedQuantity <= 0m)
        {
            _error = "A line needs a positive quantity.";
            return;
        }

        if (_pickedQuantity > stock.OnHand)
        {
            _error = $"The source holds {stock.OnHand:0.######} of {stock.VariantName}, so it cannot dispatch {_pickedQuantity:0.######}. A dispatch that would leave the source short is refused whole.";
            return;
        }

        if (_lines.Any(line => line.Stock.VariantId == stock.VariantId))
        {
            _error = $"{stock.VariantName} is already on a line. State its total quantity on that line instead of adding it twice.";
            return;
        }

        _lines.Add(new LineDraft(stock, _pickedQuantity));
        _picked = null;
        _pickedQuantity = 1m;
    }

    private void RemoveLine(LineDraft line)
    {
        _lines.Remove(line);
        _error = null;
    }

    private decimal LineTotal => _lines.Sum(line => line.Quantity);

    private async Task SubmitAsync()
    {
        if (_busy || _lines.Count == 0)
        {
            return;
        }

        if (_sourceId is not { } source || _destinationId is not { } destination)
        {
            _error = "Choose both the branch sending the stock and the branch receiving it.";
            return;
        }

        if (source == destination)
        {
            _error = "A transfer moves stock between two different branches: pick a destination that is not the source.";
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;
        _errorRetry = false;

        try
        {
            _createKey ??= StockRules.NewKey("transfer", Guid.NewGuid().ToString("N")[..8]);
            var request = new CreateStockTransferRequest(
                source,
                destination,
                _lines.Select(line => new StockTransferLineRequest(line.Stock.VariantId, line.Quantity)).ToArray(),
                string.IsNullOrWhiteSpace(_note) ? null : _note.Trim());

            var created = await Inventory.CreateTransferAsync(request, _createKey).ConfigureAwait(false);
            ReleaseIntention();
            _notice = $"{created.TransferNumber} is a draft from {created.SourceLocationName} to {created.DestinationLocationName}: nothing has moved yet. Dispatch it when the stock leaves.";
            Navigation.NavigateTo($"inventory/transfers/{created.Id:D}");
        }
        catch (ApiException exception)
        {
            _error = StockRules.RefusalSentence(exception, "the draft transfer");
            _errorRetry = !StockRules.CommittedNothing(exception.Code);
            if (StockRules.CommittedNothing(exception.Code))
            {
                ReleaseIntention();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The outcome is unknown: the key and the exact lines stay, so the next press re-asks about the
            // same draft instead of inventing a second one.
            _error = "The answer about this draft did not reach the screen, so its key and its lines are held. Press again to ask the server about the same draft; a second transfer will not be invented.";
            _errorRetry = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void ReleaseIntention()
    {
        _createKey = null;
        _sourceId = null;
        _destinationId = null;
        _note = null;
        _picked = null;
        _pickedQuantity = 1m;
        _lines.Clear();
    }

    private async Task AbandonAsync()
    {
        ReleaseIntention();
        _error = null;
        _notice = "The unanswered draft is no longer held by this screen. Read the list back: if the server recorded it, it is there; if it did not, nothing moved.";
        await LoadAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- transitions

    private async Task DispatchAsync()
    {
        await TransitionAsync(
            transfer => Inventory.DispatchTransferAsync(transfer.Id),
            "dispatch",
            "The stock has left the source: its quantity is reduced, the value sits in transit at the cost the source held, and receipt at the far end is still required.");
    }

    private async Task ReceiveAsync()
    {
        await TransitionAsync(
            transfer => Inventory.ReceiveTransferAsync(transfer.Id),
            "receipt",
            "The destination now holds the stock at the cost recorded at dispatch, and the in-transit value is cleared. Repeating this command never puts it there twice.");
    }

    private async Task TransitionAsync(Func<StockTransferDetailDto, Task<StockTransferDetailDto>> command, string what, string success)
    {
        if (_detail is not { } transfer || _busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;
        _errorRetry = false;

        try
        {
            var answer = await command(transfer).ConfigureAwait(false);
            _detail = answer;
            _notice = $"{answer.TransferNumber}: {success}";
            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            _error = StockRules.RefusalSentence(exception, $"the {what}");

            // The transition is guarded by the transfer's own state, so a refusal by that guard committed
            // nothing; only an unknown answer keeps the screen uncertain.
            _errorRetry = exception.StatusCode >= 500;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Outcome unknown. Repeating a transition never applies it twice - the server answers from the
            // transfer's state - so the operator can press again or read the transfer back.
            _error = $"The answer about this {what} did not reach the screen. Read the transfer back to see where it stands; repeating a transition never applies it twice.";
            _errorRetry = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReReadAsync()
    {
        await LoadAsync().ConfigureAwait(false);
    }

    private static Color StatusColor(StockTransferState state) => state switch
    {
        StockTransferState.Draft => Color.Info,
        StockTransferState.Dispatched => Color.Warning,
        StockTransferState.Received => Color.Success,
        _ => Color.Default,
    };

    /// <summary>A timestamp the way the rest of the shell prints one; the dash means "has not happened yet".</summary>
    private static string At(DateTimeOffset? moment)
        => moment is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";

    /// <summary>
    /// A draft has no value on purpose: only dispatch prices the lines, so quoting a number before that would
    /// be a figure nobody posted.
    /// </summary>
    private static string ValueText(decimal? totalValue, string currency)
        => totalValue is { } value ? $"{StockRules.NiceMoney(value)} {currency}" : "not yet valued - priced at dispatch";

    private void OpenDetail(Guid transferId) => Navigation.NavigateTo($"inventory/transfers/{transferId:D}");

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}
