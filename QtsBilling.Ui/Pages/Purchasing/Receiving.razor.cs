using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// Receiving and returns against one purchase order (specification task 26, recipe steps 2 and 4). The quantities a
/// person may enter are bounded by what the order still owes or by what the branch actually holds, so an
/// over-receipt is refused before it is typed; the document is posted under one idempotency key and, if its answer
/// is lost, the identical document is asked about again rather than a second one invented. Received quantities are
/// never typed and never edited: only the documents that moved them change them.
/// </summary>
public partial class Receiving
{
    [Parameter] public Guid OrderId { get; set; }

    [Inject] private IPurchasingApiClient Purchasing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IDocumentOutput Output { get; set; } = default!;

    private enum DeliveryMode
    {
        Receipt,
        Return,
    }

    private PurchaseOrderDetailDto? _detail;
    private IReadOnlyList<PurchaseReceiptDto> _receipts = [];
    private IReadOnlyList<PurchaseOrderSummaryDto> _openOrders = [];
    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private readonly Dictionary<Guid, decimal> _quantities = [];

    private DeliveryMode _mode = DeliveryMode.Receipt;
    private string? _note;
    private string? _reference;
    private string? _key;
    private PurchaseReceiptRequest? _body;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _canManage;
    private bool _started;
    private Guid _loadedId;
    private int _loadVersion;

    private bool IsOpen => _detail is not null
        && _detail.Order.Status is PurchaseOrderState.Ordered or PurchaseOrderState.PartiallyReceived;

    /// <summary>An unanswered document is held by this screen: its key and its exact quantities stay as they were.</summary>
    private bool AwaitingAnswer => _key is not null;

    private bool IsQueue => OrderId == Guid.Empty;

    private decimal TotalQuantity => _quantities.Values.Sum();

    private InvoicePresentationDto? Presentation => _detail is { } detail
        ? PurchasingRules.PresentationOf(BranchZone(detail.Order.LocationId))
        : null;

    private string NoteHint => _mode == DeliveryMode.Receipt
        ? "What arrived, how it was checked, and anything short or damaged - the note belongs to the goods received note itself."
        : "Why the goods are going back: the reason is read by whoever reconciles the supplier's account.";

    private string KeyHint => AwaitingAnswer
        ? "This document is still awaiting its answer. Its quantities are held exactly as they were: pressing again asks the server about the same document, so one delivery is never recorded twice."
        : "One key per document: press once, and if the answer is lost the same document is asked about again rather than a new one invented.";

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
        if (_started && OrderId != _loadedId)
        {
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _state = ShellStateKind.Loading;
        _message = null;
        _detail = null;
        _error = null;

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

            _canManage = session.Permissions.Contains(Permissions.PurchasingManage, StringComparer.Ordinal);

            if (!_canManage)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Recording a delivery needs the '{Permissions.PurchasingManage}' authority, which this account does not hold in this business.";
                return;
            }

            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (IsQueue)
            {
                // The store's work queue reads both open states: a placed order whose first van has arrived is
                // still open, and an order is always chosen from this list rather than typed in.
                var placed = await Purchasing.ListOrdersAsync(PurchaseOrderState.Ordered, pageSize: 50).ConfigureAwait(false);
                var partial = await Purchasing.ListOrdersAsync(PurchaseOrderState.PartiallyReceived, pageSize: 50).ConfigureAwait(false);

                if (version != _loadVersion)
                {
                    return;
                }

                _openOrders = [.. placed.Items, .. partial.Items];
                _state = _openOrders.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
                _loadedId = OrderId;
                return;
            }

            var detail = await Purchasing.GetOrderAsync(OrderId).ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            _detail = detail;
            _loadedId = detail.Order.Id;
            _quantities.Clear();

            _receipts = detail.Order.Status == PurchaseOrderState.Draft
                ? []
                : await Purchasing.ListReceiptsAsync(detail.Order.Id).ConfigureAwait(false);

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
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = PurchasingRules.RefusalSentence(exception, IsQueue ? "the receiving queue" : "this purchase order");
        }
        finally
        {
            if (version == _loadVersion)
            {
                StateHasChanged();
            }
        }
    }

    private void ChooseReceipt() => SwitchMode(DeliveryMode.Receipt);

    private void ChooseReturn() => SwitchMode(DeliveryMode.Return);

    private void SwitchMode(DeliveryMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        // Quantities belong to one kind of document: an entry made as a delivery must not be sent as a return
        // simply because the operator changed their mind mid-form.
        _mode = mode;
        _quantities.Clear();
        _error = null;
    }

    private string BranchText(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.Name ?? "the ordered branch";

    private string? BranchZone(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.TimeZone;

    private decimal CeilingFor(PurchaseOrderLineDto line)
        => _mode == DeliveryMode.Receipt ? PurchasingRules.Outstanding(line) : PurchasingRules.Returnable(line);

    private decimal QuantityFor(PurchaseOrderLineDto line)
        => _quantities.TryGetValue(line.Id, out var value) ? value : 0m;

    private void SetQuantity(PurchaseOrderLineDto line, decimal value)
    {
        var ceiling = CeilingFor(line);
        _quantities[line.Id] = value < 0m ? 0m : value > ceiling ? ceiling : value;
    }

    /// <summary>
    /// Posts the receipt or return. With a key already held the identical body is sent again, so the server's own
    /// replay returns the document it recorded instead of a second one (recipe step 4, case T26.02).
    /// </summary>
    private async Task SubmitAsync()
    {
        if (_detail is not { } order || !IsOpen || _busy)
        {
            return;
        }

        if (TotalQuantity <= 0m)
        {
            _error = _mode == DeliveryMode.Receipt
                ? "State what arrived on at least one line. The screen only allows up to what is still outstanding, so a delivery beyond that means the order moved - read it back."
                : "State what is going back on at least one line. Only quantities this branch actually received can be returned.";
            return;
        }

        _key ??= PurchasingRules.NewKey(_mode == DeliveryMode.Receipt ? "receive" : "return", order.Order.OrderNumber);
        _body ??= new PurchaseReceiptRequest(
            PurchasingRules.CombineNote(_reference, _note),
            _quantities.Where(pair => pair.Value > 0m)
                .Select(pair => new PurchaseReceiptLineRequest(pair.Key, pair.Value))
                .ToArray());

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var answer = _mode == DeliveryMode.Receipt
                ? await Purchasing.ReceiveAsync(order.Order.Id, _body, _key).ConfigureAwait(false)
                : await Purchasing.ReturnAsync(order.Order.Id, _body, _key).ConfigureAwait(false);

            ReleaseIntention();
            _notice = PurchasingRules.IsReturn(answer.Kind)
                ? $"{answer.ReceiptNumber} records {answer.Lines.Count} line(s) going back to {answer.OrderNumber}. Stock and the supplier's account follow it: {PurchasingRules.LedgerText(answer)}."
                : $"{answer.ReceiptNumber} records the delivery into {BranchText(answer.LocationId)} at the cost the order carried: {PurchasingRules.LedgerText(answer)}.";

            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = PurchasingRules.RefusalSentence(exception, _mode == DeliveryMode.Receipt ? "this delivery" : "this return");

            if (PurchasingRules.CommittedNothing(exception.Code))
            {
                ReleaseIntention();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The outcome is unknown: the key and the exact quantities stay, so the next press re-asks about the
            // same document instead of inventing a second one.
            _error = "The answer about this document did not reach the screen, so its key and its quantities are held. Press again to ask the server about the same document; a second delivery note will not be invented.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void ReleaseIntention()
    {
        _key = null;
        _body = null;
        _note = null;
        _reference = null;
        _quantities.Clear();
    }

    private async Task AbandonAsync()
    {
        ReleaseIntention();
        _error = null;
        _notice = "The unanswered document is no longer held by this screen. The order has been read back - if the server recorded it, the document is listed; if it did not, nothing moved.";
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task PrintAsync(PurchaseReceiptDto receipt)
    {
        _error = null;

        try
        {
            var document = PurchaseDocument.ForReceipt(
                receipt, BranchText(receipt.LocationId), reprint: true, BranchZone(receipt.LocationId));
            _notice = await Output.PresentAsync(document).ConfigureAwait(false)
                ? $"{receipt.ReceiptNumber} was handed to this host's own print dialogue from its stored lines."
                : "This host could not open a print dialogue, so nothing was printed, written or fetched.";
        }
        catch (ArgumentException exception)
        {
            _error = $"The document could not be prepared: {exception.Message}";
        }
    }
}
