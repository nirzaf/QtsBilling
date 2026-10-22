using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The order workbench (specification task 22, recipe steps 1 and 3): the list bounded by number and status, and the
/// one order's commitments - what was ordered, what has gone out, what is still outstanding, and the commands its
/// state allows. Confirm re-checks authority and credit at the server; an amendment restates only the outstanding
/// quantities and becomes a new revision; a remainder cancellation needs its reason. Nothing here delivers, reserves,
/// invoices or moves stock - that is the fulfillment workflow's own surface (task 23).
/// </summary>
public partial class SalesOrders : IAsyncDisposable
{
    private const int PageSize = 20;

    [Parameter] public Guid? OrderId { get; set; }

    [Inject] private ISalesOrderApiClient OrdersApi { get; set; } = default!;
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly PagedListState<SalesOrderSummaryDto> _list;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private string BusinessContext { get; set; } = "Loading this business…";
    private bool _busy;
    private bool _started;
    private bool _canManage;

    private string _search = string.Empty;
    private string _status = string.Empty;

    private SalesOrderDto? _order;
    private IReadOnlyList<TaxRateDto> _taxRates = [];
    private readonly Dictionary<Guid, decimal> _keep = [];
    private readonly Dictionary<Guid, Guid?> _serviceRates = [];
    private OrderRules.OrderAction? _pendingOrderAction;
    private string _reason = string.Empty;
    private DateTime? _promisedOn;
    private bool _awaitingAnswer;
    private bool _printing;

    private sealed record AmendmentRow(OrderLineDto Line, decimal Keep);

    public SalesOrders()
        => _list = new PagedListState<SalesOrderSummaryDto>(LoadPageAsync, StateHasChanged, PageSize);

    private IReadOnlyList<OrderRules.OrderAction> AllowedOrderActions
        => _order is not { } order || !_canManage
            ? []
            : OrderRules.AllowedOrderActions(order.Status);

    private string OrderActionsSentence
        => _canManage
            ? "Only the commands this order's state allows are offered; each refusal is the server's own sentence."
            : $"These commands need the '{Permissions.OrdersManage}' authority, which this account does not hold. The order and its quantities stay readable.";

    private IReadOnlyList<AmendmentRow> AmendmentRows
        => _order is not { } order
            ? []
            : order.Lines
                .Where(OrderRules.IsOpen)
                .Select(line => new AmendmentRow(line, _keep.GetValueOrDefault(line.LineKey, OrderRules.Outstanding(line))))
                .ToArray();

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
        // Navigating from the list to another order is a parameter change without a new render cycle: the order is
        // re-read so the screen never shows one order under another's address.
        if (_started && OrderId is { } id && _order?.Id != id)
        {
            await LoadOrderAsync(id).ConfigureAwait(false);
        }
    }

    private async Task<PagedRows<SalesOrderSummaryDto>> LoadPageAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await OrdersApi.ListAsync(
            string.IsNullOrEmpty(_status) ? null : _status, search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<SalesOrderSummaryDto>(page.Items, page.NextCursor);
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _state = ShellStateKind.Loading;
        _problem = null;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.OrdersManage, StringComparer.Ordinal);
            _taxRates = await Pricing.ListTaxRatesAsync().ConfigureAwait(false);
            BusinessContext = session.BusinessName is { Length: > 0 } business
                ? $"{business} - orders of the branches this account may reach."
                : "No business is selected for this session.";

            await _list.SearchAsync(_search).ConfigureAwait(false);

            if (OrderId is { } id)
            {
                await LoadOrderAsync(id).ConfigureAwait(false);

                return;
            }

            _state = _list.State;
            _message = _list.Message;
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = _state == ShellStateKind.Forbidden
                ? "Reading orders needs the sale authority this account does not hold."
                : exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task LoadOrderAsync(Guid id)
    {
        _order = await OrdersApi.GetAsync(id).ConfigureAwait(false);
        _keep.Clear();
        _serviceRates.Clear();
        _promisedOn = _order.PromisedDeliveryDate?.ToDateTime(TimeOnly.MinValue);
        SeedServiceRates(_order);
        _pendingOrderAction = null;
        _reason = string.Empty;
        _printing = false;
        _state = ShellStateKind.Content;
        _message = null;
    }

    private void SeedServiceRates(SalesOrderDto order)
    {
        foreach (var line in order.Lines.Where(candidate => candidate.VariantId is null))
        {
            // The line stores the rate as a number; the amendment names a rate, so the business's active rate with
            // that value is matched and shown as the chosen one rather than silently invented (finding F21's rule).
            _serviceRates.TryAdd(line.LineKey, _taxRates.FirstOrDefault(rate => rate.Rate == line.TaxRate)?.Id);
        }
    }

    private static string OrderBranch(SalesOrderDto order)
        => order.LocationName ?? "a branch no longer listed";

    private static Color StatusColor(string status) => status switch
    {
        "Confirmed" => Color.Info,
        "PartiallyFulfilled" => Color.Warning,
        "Fulfilled" or "Closed" => Color.Success,
        "Draft" => Color.Default,
        _ => Color.Default,
    };

    // ------------------------------------------------------------------ list filters

    private Task SearchChangedAsync(string term)
    {
        _search = term?.Trim() ?? string.Empty;

        return RefilterAsync();
    }

    private Task StatusChangedAsync(string status)
    {
        _status = status ?? string.Empty;

        return RefilterAsync();
    }

    private async Task RefilterAsync()
    {
        _problem = null;
        await _list.SearchAsync(_search).ConfigureAwait(false);
        _state = _order is null ? _list.State : _state;
        _message = _order is null ? _list.Message : _message;
        StateHasChanged();
    }

    private Task ReloadAsync() => LoadAsync();

    private Task PreviousPageAsync() => GoPageAsync(_list.PreviousPageAsync());

    private Task NextPageAsync() => GoPageAsync(_list.NextPageAsync());

    private async Task GoPageAsync(Task move)
    {
        _problem = null;
        await move.ConfigureAwait(false);
        _state = _list.State;
        _message = _list.Message;
        StateHasChanged();
    }

    // ------------------------------------------------------------------ commands

    private static string OrderActionLabel(OrderRules.OrderAction action) => action switch
    {
        OrderRules.OrderAction.Confirm => "Confirm the order",
        OrderRules.OrderAction.Amend => "Amend the outstanding quantities",
        OrderRules.OrderAction.CancelRemainder => "Cancel the remaining quantities",
        _ => action.ToString(),
    };

    private string OrderActionHeading(OrderRules.OrderAction action) => action switch
    {
        OrderRules.OrderAction.Confirm => "Confirm this order",
        OrderRules.OrderAction.Amend => "Restate what is still outstanding",
        OrderRules.OrderAction.CancelRemainder => "Cancel what is still outstanding",
        _ => "This order's command",
    };

    private string OrderActionHelp(OrderRules.OrderAction action) => action switch
    {
        OrderRules.OrderAction.Confirm =>
            "A draft carries no commitment until it is confirmed. Confirmation is priced and checked again by the "
            + "server on the current values, so a confirmation can still be refused.",
        OrderRules.OrderAction.Amend =>
            "The outstanding quantity on each open line is cancelled and replaced by what you keep here - delivered "
            + "quantities are untouched history, and reservations this order already holds are released first.",
        OrderRules.OrderAction.CancelRemainder =>
            "Cancelling the remainder closes the order for future delivery. What has already gone out stays, and the "
            + "reason becomes part of the order's record.",
        _ => "The server judges the command and answers with its own sentence.",
    };

    private static string OrderActionReasonLabel(OrderRules.OrderAction action)
        => OrderRules.OrderActionNeedsReason(action)
            ? "Reason (required)"
            : "Reason (optional, recorded with the command)";

    private string OrderActionSentence(OrderRules.OrderAction action) => action switch
    {
        OrderRules.OrderAction.Confirm => OrderRules.CommitmentNote,
        OrderRules.OrderAction.Amend => "The amendment becomes a new revision with its reason; the revision that was confirmed stays as the evidence of what was agreed.",
        OrderRules.OrderAction.CancelRemainder => "A cancelled remainder is never reopened - a further commitment is a new order.",
        _ => string.Empty,
    };

    private Task BeginOrderActionAsync(OrderRules.OrderAction action)
    {
        _problem = null;
        _committed = null;
        _reason = string.Empty;
        _pendingOrderAction = action;

        if (_order is { } order)
        {
            foreach (var line in order.Lines.Where(OrderRules.IsOpen))
            {
                _keep.TryAdd(line.LineKey, OrderRules.Outstanding(line));
            }
        }

        StateHasChanged();

        return Task.CompletedTask;
    }

    private void SetKeep(OrderLineDto line, decimal value)
    {
        if (!_awaitingAnswer)
        {
            _keep[line.LineKey] = Math.Clamp(value, 0m, OrderRules.Outstanding(line));
        }
    }

    /// <summary>The promised date an amendment may move; the ordered date itself never changes.</summary>
    private Task PromisedOnChangedAsync(DateTime? day)
    {
        if (!_awaitingAnswer)
        {
            _promisedOn = day;
        }

        return Task.CompletedTask;
    }

    private void SetServiceRate(OrderLineDto line, Guid? rateId)
    {
        if (!_awaitingAnswer)
        {
            _serviceRates[line.LineKey] = rateId;
        }
    }

    private string? RateLabelFor(OrderLineDto line)
        => _serviceRates.TryGetValue(line.LineKey, out var rateId) && rateId is { } chosen
            ? _taxRates.FirstOrDefault(rate => rate.Id == chosen)?.Name ?? "an unknown rate"
            : "No tax";

    private bool CanRunOrderAction
        => _order is { } order
            && !_busy
            && !_awaitingAnswer
            && _canManage
            && _pendingOrderAction is { } action
            && (!OrderRules.OrderActionNeedsReason(action) || !string.IsNullOrWhiteSpace(_reason))
            && (action != OrderRules.OrderAction.Amend || AmendmentRows.Any(row => KeepOf(row.Line) > 0m && RateIsChosen(row.Line)))
            && (action != OrderRules.OrderAction.Amend || order.Lines.Where(OrderRules.IsOpen).All(RateIsChosen));

    private decimal KeepOf(OrderLineDto line)
        => _keep.TryGetValue(line.LineKey, out var value) ? Math.Clamp(value, 0m, OrderRules.Outstanding(line)) : OrderRules.Outstanding(line);

    private bool RateIsChosen(OrderLineDto line)
        => line.VariantId is not null || _serviceRates.TryGetValue(line.LineKey, out var rateId) && (rateId is not null || line.TaxRate == 0m);

    private async Task RunOrderActionAsync()
    {
        if (_order is not { } order || _pendingOrderAction is not { } action || !CanRunOrderAction)
        {
            return;
        }

        _busy = true;
        _problem = null;
        StateHasChanged();

        try
        {
            var result = action switch
            {
                OrderRules.OrderAction.Confirm => await OrdersApi.ConfirmAsync(
                    order.Id, new ConfirmSalesOrderRequest(order.Version, Reason())).ConfigureAwait(false),
                OrderRules.OrderAction.Amend => await OrdersApi.AmendAsync(
                    order.Id, AmendmentRequest(order)).ConfigureAwait(false),
                _ => await OrdersApi.CancelRemainderAsync(
                    order.Id, new CancelOrderRemainderRequest(order.Version, Reason() ?? "remainder")).ConfigureAwait(false),
            };

            _order = result;
            _committed = CommittedOrderSentence(action, result);
            _pendingOrderAction = null;
            _awaitingAnswer = false;
            _reason = string.Empty;
            _keep.Clear();
            SeedServiceRates(result);
            await _list.ReloadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = OrderRules.RefusalSentence(exception, "order");

            if (exception.Code is ApiErrorCodes.OptimisticConcurrencyFailure
                or ApiErrorCodes.SalesOrderNotDraft
                or ApiErrorCodes.SalesOrderNotOpen
                or ApiErrorCodes.CreditLimitExceeded)
            {
                await LoadOrderAsync(order.Id).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _awaitingAnswer = true;
            _problem = $"The answer to that order command never arrived: {exception.Message}. {OrderRules.UnresolvedOrderNote}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private string? Reason() => string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim();

    private AmendSalesOrderRequest AmendmentRequest(SalesOrderDto order)
        => new(
            order.Version,
            _promisedOn is { } promised ? DateOnly.FromDateTime(promised) : null,
            Reason() ?? "amended",
            order.Lines.Where(OrderRules.IsOpen)
                .Where(line => KeepOf(line) > 0m)
                .Select(line => new SalesOrderAmendmentLineRequest(
                    line.LineKey,
                    line.VariantId,
                    line.Description,
                    KeepOf(line),
                    line.VariantId is null ? line.UnitPrice : null,
                    line.DiscountAmount,
                    line.VariantId is null ? _serviceRates.GetValueOrDefault(line.LineKey) : null,
                    line.VariantId is null ? line.TaxMode : null))
                .ToArray());

    private static string CommittedOrderSentence(OrderRules.OrderAction action, SalesOrderDto order) => action switch
    {
        OrderRules.OrderAction.Confirm =>
            $"Confirmed as revision {order.CurrentRevision} - {OrderRules.MoneyText(order.TotalAmount, order.Currency)} "
            + "outstanding. Nothing has been reserved or delivered by confirming it.",
        OrderRules.OrderAction.Amend =>
            $"The amendment is revision {order.CurrentRevision}: the quantities you kept stand, what was outstanding "
            + "and is not here was cancelled, and delivered quantities were left untouched.",
        _ => $"The remaining quantities are cancelled and the order is {OrderRules.OrderStatusText(order.Status).ToLowerInvariant()}.",
    };

    private void TogglePrint()
    {
        _printing = !_printing;
        StateHasChanged();
    }

    public ValueTask DisposeAsync() => _list.DisposeAsync();
}
