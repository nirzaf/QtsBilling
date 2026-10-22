using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Orders;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The fulfillment workbench (specification task 23, recipe steps 1 to 4): an open order's quantities shown apart -
/// ordered, delivered, cancelled, outstanding, reserved, released, invoiced and on hand - with reserve, release,
/// dispatch and invoice-from-delivery as four separate intentions, each carrying its own version and its own key.
/// Nothing here can move a quantity the server has not agreed to move, and every refusal is answered by a reload or
/// an explicit release, never by a substituted figure.
/// </summary>
public partial class Fulfillment : IAsyncDisposable
{
    [Parameter] public Guid? OrderId { get; set; }

    [Inject] private IFulfillmentApiClient FulfillmentApi { get; set; } = default!;
    [Inject] private ISalesOrderApiClient OrdersApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly PagedListState<SalesOrderSummaryDto> _orders;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private string BusinessContext { get; set; } = "Loading this business…";
    private bool _busy;
    private bool _started;
    private bool _canDispatch;

    private string _search = string.Empty;
    private string _statusFilter = "Confirmed";

    private OrderFulfillmentDto? _view;

    // Four intentions, four keys: each is held while its own answer is outstanding (specification D06).
    private string? _reserveKey;
    private string? _deliveryKey;
    private string? _invoiceKey;
    private bool _awaitingReservation;
    private bool _awaitingDelivery;
    private bool _awaitingInvoice;

    private int _reserveDays = 14;
    private bool _releasing;
    private string _reason = string.Empty;
    private DateTime? _deliveryDate;
    private string _deliveryNote = string.Empty;
    private readonly Dictionary<Guid, decimal> _deliver = [];
    private SalesOrderDeliveryDto? _notedDelivery;
    private SalesOrderDeliveryDto? _invoicing;
    private DateTime? _issueDate;
    private DateTime? _dueDate;
    private string _invoiceNote = string.Empty;
    private readonly Dictionary<Guid, decimal> _invoiceQuantities = [];

    public Fulfillment()
        => _orders = new PagedListState<SalesOrderSummaryDto>(LoadOrdersAsync, StateHasChanged, 10);

    private bool HasActiveReservation => _view?.Reservation?.Status == "Active";

    private bool _awaitingAny => _awaitingReservation || _awaitingDelivery || _awaitingInvoice;

    private bool CanEditDeliver => _canDispatch && !_busy && !_awaitingAny && _view?.Status is "Confirmed" or "PartiallyFulfilled";

    private bool CanReserve
        => _canDispatch
            && _view is { } view
            && FulfillmentRules.CanReserve(view, _busy, _awaitingAny, ReservationExpiry, DateTimeOffset.UtcNow);

    private bool CanDeliver
        => _canDispatch
            && _view is { } view
            && _deliveryDate is not null
            && FulfillmentRules.CanDeliver(view, _deliver, _busy, _awaitingAny);

    private bool CanCreateInvoice
        => _canDispatch
            && _invoicing is not null
            && _issueDate is not null
            && !_busy
            && !_awaitingAny
            && _invoiceQuantities.Any(entry => entry.Value > 0m);

    private DateTimeOffset ReservationExpiry => DateTimeOffset.UtcNow.AddDays(Math.Clamp(_reserveDays, 1, 120));

    private string UnresolvedCommandNote
        => "A reservation, a delivery or an invoice whose answer never arrived keeps its own key: retrying asks the "
            + "server about that same command instead of moving the stock or recognising the revenue twice. Re-read "
            + "this order to see what actually happened.";

    private string ReservationSentence
        => _view is not { } view
            ? "Read the order to see its reservation."
            : view.Reservation is { Status: "Active" } active
                ? $"{FulfillmentRules.Money(active.Lines.Sum(line => Math.Max(0m, line.QuantityRemaining)))} of this order's stock is reserved until "
                    + $"{active.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. A reservation promises the stock to this order; "
                    + "it moves nothing and posts nothing."
                : view.Reservation is { } closed
                    ? $"The last reservation is {closed.Status}. Nothing is promised to this order right now."
                    : "Nothing is reserved on this order. Reserving promises the stock to this order; it moves nothing and posts nothing.";

    private string DispatchSentence
        => "Delivery is the only step here that moves stock: it consumes the reservation first, then the branch's own "
            + "quantities, in one transaction, at the cost the order was confirmed at.";

    private string DispatchAuthorityNote
        => $"Dispatching needs the '{Permissions.InventoryDispatch}' or the order authority this account does not hold; the quantities above stay readable.";

    private string InvoiceSentence
        => "The invoice is created from this delivery's own lines - the delivered-but-not-yet-invoiced quantities - so "
            + "recognition can never exceed what left the branch.";

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
        if (_started && OrderId is { } id && _view?.OrderId != id)
        {
            await LoadViewAsync(id).ConfigureAwait(false);
        }
    }

    private async Task<PagedRows<SalesOrderSummaryDto>> LoadOrdersAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await OrdersApi.ListAsync(_statusFilter, search, cursor, pageSize, token).ConfigureAwait(false);

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

            _canDispatch = session.Permissions.Contains(Permissions.InventoryDispatch, StringComparer.Ordinal)
                || session.Permissions.Contains(Permissions.OrdersManage, StringComparer.Ordinal);
            BusinessContext = session.BusinessName is { Length: > 0 } business
                ? $"{business} - dispatch from the branches this account may reach."
                : "No business is selected for this session.";

            await _orders.SearchAsync(_search).ConfigureAwait(false);

            if (OrderId is { } id)
            {
                await LoadViewAsync(id).ConfigureAwait(false);

                return;
            }

            _state = _orders.State;
            _message = _orders.Message;
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
            _message = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task LoadViewAsync(Guid orderId)
    {
        try
        {
            _view = await FulfillmentApi.GetFulfillmentAsync(orderId).ConfigureAwait(false);
            _state = ShellStateKind.Content;
            _message = null;
            _deliveryDate ??= DateTime.Today;
            _issueDate ??= DateTime.Today;
            _notedDelivery = null;
            _invoicing = null;
            _deliver.Clear();
            _invoiceQuantities.Clear();
        }
        catch (ApiException exception)
        {
            _view = null;
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                404 => ShellStateKind.Empty,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = exception.StatusCode == 403
                ? "This order belongs to a branch this account may not reach."
                : exception.Message;
        }
    }

    // ------------------------------------------------------------------ quantities

    private string OrderSummary(OrderFulfillmentDto view)
        => $"{view.LocationName} - {(view.CustomerName ?? "no customer named")}, ordered {view.OrderDate:yyyy-MM-dd}"
            + (view.PromisedDeliveryDate is { } promised ? $", promised {promised:yyyy-MM-dd}" : string.Empty);

    private static string DescriptionFor(OrderFulfillmentDto view, Guid orderLineId)
        => view.Lines.FirstOrDefault(line => line.OrderLineId == orderLineId)?.Description ?? "a line of this order";

    private static string DeliverySummary(OrderFulfillmentDto view, SalesOrderDeliveryDto delivery)
        => $"{delivery.DeliveryNumber} - {delivery.DeliveryDate:yyyy-MM-dd}, {FulfillmentRules.Money(delivery.TotalQuantity)} units, "
            + $"{FulfillmentRules.MoneyText(delivery.CostValue, view.Currency)} at the cost the order was confirmed at";

    private decimal DeliverQuantity(FulfillmentLineDto line)
        => _deliver.TryGetValue(line.OrderLineId, out var value) ? value : 0m;

    private void SetDeliverQuantity(FulfillmentLineDto line, decimal value)
    {
        if (!CanEditDeliver)
        {
            return;
        }

        _deliver[line.OrderLineId] = Math.Clamp(value, 0m, line.Outstanding);
    }

    private decimal InvoiceQuantity(SalesOrderDeliveryLineDto line)
        => _invoiceQuantities.TryGetValue(line.Id, out var value) ? value : FulfillmentRules.InvoiceableQuantity(line);

    private void SetInvoiceQuantity(SalesOrderDeliveryLineDto line, decimal value)
    {
        if (_awaitingAny || _busy)
        {
            return;
        }

        _invoiceQuantities[line.Id] = Math.Clamp(value, 0m, FulfillmentRules.InvoiceableQuantity(line));
    }

    // ------------------------------------------------------------------ filters

    private Task SearchChangedAsync(string term)
    {
        _search = term?.Trim() ?? string.Empty;

        return RefilterAsync();
    }

    private Task StatusFilterChangedAsync(string status)
    {
        _statusFilter = string.IsNullOrWhiteSpace(status) ? "Confirmed" : status;

        return RefilterAsync();
    }

    private async Task RefilterAsync()
    {
        _problem = null;
        await _orders.SearchAsync(_search).ConfigureAwait(false);

        if (_view is null)
        {
            _state = _orders.State;
            _message = _orders.Message;
        }

        StateHasChanged();
    }

    private Task ReloadAsync() => LoadAsync();

    private Task PreviousPageAsync() => GoPageAsync(_orders.PreviousPageAsync());

    private Task NextPageAsync() => GoPageAsync(_orders.NextPageAsync());

    private async Task GoPageAsync(Task move)
    {
        _problem = null;
        await move.ConfigureAwait(false);
        StateHasChanged();
    }

    // ------------------------------------------------------------------ commands

    private async Task ReserveAsync()
    {
        if (_view is not { } view || !CanReserve)
        {
            return;
        }

        _reserveKey ??= FulfillmentRules.NewKey("reserve", view.OrderNumber);
        _awaitingReservation = true;

        await RunAsync(async () =>
        {
            var reservation = await FulfillmentApi.ReserveAsync(
                view.OrderId, new ReserveSalesOrderRequest(view.Version, ReservationExpiry), _reserveKey).ConfigureAwait(false);

            _committed = $"Reserved until {reservation.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}: "
                + $"{FulfillmentRules.Money(reservation.Lines.Sum(line => Math.Max(0m, line.QuantityRemaining)))} units are promised to this order. "
                + "No stock moved and nothing was posted.";
            _awaitingReservation = false;
            _reserveKey = null;
        }, "reservation");
    }

    private void BeginRelease()
    {
        _releasing = true;
        _reason = string.Empty;
        StateHasChanged();
    }

    private void CancelRelease()
    {
        _releasing = false;
        StateHasChanged();
    }

    private async Task ReleaseAsync()
    {
        if (_view is not { } view || _busy || string.IsNullOrWhiteSpace(_reason) || view.Reservation is not { } reservation)
        {
            return;
        }

        _releasing = false;

        // A release is a command too, but the server keys it by the order version and its reason, so a repeat
        // answers with the same released state rather than a second movement.
        await RunAsync(async () =>
        {
            await FulfillmentApi.ReleaseReservationsAsync(
                view.OrderId, new ReleaseSalesOrderReservationRequest(view.Version, _reason.Trim())).ConfigureAwait(false);

            _committed = "The unfulfilled reservation was released - the promise is gone, and no stock moved.";
        }, "release");
    }

    private async Task DeliverAsync()
    {
        if (_view is not { } view || !CanDeliver || _deliveryDate is not { } date)
        {
            return;
        }

        _deliveryKey ??= FulfillmentRules.NewKey("deliver", view.OrderNumber);
        _awaitingDelivery = true;

        var lines = view.Lines
            .Where(line => _deliver.GetValueOrDefault(line.OrderLineId) > 0m)
            .Select(line => new DeliverSalesOrderLineRequest(line.OrderLineId, _deliver[line.OrderLineId]))
            .ToList();

        var request = new DeliverSalesOrderRequest(
            view.Version,
            DateOnly.FromDateTime(date),
            string.IsNullOrWhiteSpace(_deliveryNote) ? null : _deliveryNote.Trim(),
            lines);

        await RunAsync(async () =>
        {
            var delivery = await FulfillmentApi.DeliverAsync(view.OrderId, request, _deliveryKey!).ConfigureAwait(false);

            _committed = $"Delivery {delivery.DeliveryNumber} posted: {FulfillmentRules.Money(delivery.TotalQuantity)} units left "
                + $"{view.LocationName} at {FulfillmentRules.MoneyText(delivery.CostValue, view.Currency)} cost, with its own journal. "
                + "It can now be invoiced from this delivery - never from a remembered quantity.";
            _awaitingDelivery = false;
            _deliveryKey = null;
            _deliver.Clear();
            _deliveryNote = string.Empty;
            _notedDelivery = delivery;
        }, "delivery");
    }

    private Task DeliveryDateChangedAsync(DateTime? date)
    {
        if (!CanEditDeliver)
        {
            return Task.CompletedTask;
        }

        _deliveryDate = date;

        return Task.CompletedTask;
    }

    private Task IssueDateChangedAsync(DateTime? date)
    {
        if (!_awaitingAny && !_busy)
        {
            _issueDate = date;
        }

        return Task.CompletedTask;
    }

    private Task DueDateChangedAsync(DateTime? date)
    {
        if (!_awaitingAny && !_busy)
        {
            _dueDate = date;
        }

        return Task.CompletedTask;
    }

    private Task ToggleNoteAsync(SalesOrderDeliveryDto delivery)
    {
        _notedDelivery = _notedDelivery?.Id == delivery.Id ? null : delivery;
        StateHasChanged();

        return Task.CompletedTask;
    }

    private Task BeginInvoiceAsync(SalesOrderDeliveryDto delivery)
    {
        if (!CanInvoiceFrom(delivery))
        {
            return Task.CompletedTask;
        }

        _invoicing = delivery;
        _invoiceQuantities.Clear();
        _invoiceKey = null;
        _awaitingInvoice = false;
        StateHasChanged();

        return Task.CompletedTask;
    }

    private void CancelInvoice()
    {
        _invoicing = null;
        _invoiceQuantities.Clear();
        StateHasChanged();
    }

    private bool CanInvoiceFrom(SalesOrderDeliveryDto delivery)
        => _canDispatch
            && !_awaitingAny
            && delivery.Lines.Any(line => FulfillmentRules.InvoiceableQuantity(line) > 0m);

    private async Task CreateInvoiceAsync()
    {
        if (_view is not { } view || _invoicing is not { } delivery || !CanCreateInvoice)
        {
            return;
        }

        _invoiceKey ??= FulfillmentRules.NewKey("invoice", delivery.DeliveryNumber);
        _awaitingInvoice = true;

        var lines = _invoiceQuantities
            .Where(entry => entry.Value > 0m)
            .Select(entry => new CreateDeliveryInvoiceLineRequest(entry.Key, entry.Value))
            .ToList();

        var request = new CreateDeliveryInvoiceRequest(
            DateOnly.FromDateTime(_issueDate!.Value),
            _dueDate is { } due ? DateOnly.FromDateTime(due) : null,
            string.IsNullOrWhiteSpace(_invoiceNote) ? null : _invoiceNote.Trim(),
            lines);

        await RunAsync(async () =>
        {
            var invoice = await FulfillmentApi.CreateInvoiceAsync(
                view.OrderId, delivery.Id, request, _invoiceKey!).ConfigureAwait(false);

            var created = invoice.Invoice;

            _committed = $"Invoice {created.InvoiceNumber} created from {delivery.DeliveryNumber} - "
                + $"{FulfillmentRules.MoneyText(created.TotalAmount, created.Currency)} recognised once, against these delivered quantities.";
            _awaitingInvoice = false;
            _invoiceKey = null;
            _invoicing = null;
            _invoiceNote = string.Empty;

            // Recognition lives in the invoice workbench, so the operator lands where the document is settled.
            Navigation.NavigateTo($"/invoices/{created.Id}");
        }, "invoice");
    }

    private async Task RunAsync(Func<Task> command, string what)
    {
        _busy = true;
        _problem = null;
        StateHasChanged();

        try
        {
            await command().ConfigureAwait(false);

            if (OrderId is { } id)
            {
                await LoadViewAsync(id).ConfigureAwait(false);
            }
        }
        catch (ApiException exception)
        {
            _problem = FulfillmentRules.RefusalSentence(exception, _view?.OrderNumber ?? "order");

            // A refusal that proves nothing was committed releases that intention's key; anything else - a lost
            // answer above all - keeps the exact key and body so the retry resolves the same document.
            if (FulfillmentRules.CommittedNothing(exception.Code))
            {
                ReleaseKey(what);
            }
            else
            {
                AwaitKey(what);
            }

            if (exception.Code is ApiErrorCodes.OptimisticConcurrencyFailure
                or ApiErrorCodes.Conflict
                or ApiErrorCodes.SalesOrderNotOpen)
            {
                if (OrderId is { } reloaded)
                {
                    await LoadViewAsync(reloaded).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _problem = $"The answer to that {what} never arrived: {exception.Message}. {UnresolvedCommandNote}";
            AwaitKey(what);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void ReleaseKey(string what)
    {
        switch (what)
        {
            case "reservation":
            case "release":
                _reserveKey = null;
                _awaitingReservation = false;
                break;
            case "delivery":
                _deliveryKey = null;
                _awaitingDelivery = false;
                break;
            default:
                _invoiceKey = null;
                _awaitingInvoice = false;
                break;
        }
    }

    private void AwaitKey(string what)
    {
        switch (what)
        {
            case "reservation":
                _awaitingReservation = true;
                break;
            case "delivery":
                _awaitingDelivery = true;
                break;
            default:
                _awaitingInvoice = true;
                break;
        }
    }

    private void AbandonCommands()
    {
        _reserveKey = null;
        _deliveryKey = null;
        _invoiceKey = null;
        _awaitingReservation = false;
        _awaitingDelivery = false;
        _awaitingInvoice = false;
        _problem = null;
        _message = "The held keys are gone and the order above is the server's latest state. Anything that did reach "
            + "the server shows in these quantities; a second document is refused by the server rather than retried from here.";
        StateHasChanged();
    }

    public async ValueTask DisposeAsync() => await _orders.DisposeAsync().ConfigureAwait(false);
}
