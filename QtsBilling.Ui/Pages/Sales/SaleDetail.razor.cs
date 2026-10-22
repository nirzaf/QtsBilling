using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Sales;

/// <summary>
/// One posted sale as an operator has to see it (specification task 21, recipe steps 1 to 4): the immutable
/// snapshots it was written with, its payment and refund history, a reprint that changes nothing, and the refund
/// panel whose every limit comes from the server's own position. The screen's totals are previews; the refund's
/// money, quantities and cost are decided by the server when it commits, and an unresolved refund keeps its exact
/// key and body until the answer arrives - the same rule task 20 established for taking money.
/// </summary>
public partial class SaleDetail : IAsyncDisposable
{
    [Parameter] public Guid SaleId { get; set; }

    [Inject] private ISaleApiClient SalesApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private bool _busy;
    private bool _started;
    private bool _canRefund;
    private bool _canSeeCost;

    private PostedSaleDto? _sale;
    private SaleReceiptDto? _receipt;
    private string? _receiptProblem;
    private SaleRefundPositionDto? _position;
    private IReadOnlyList<RefundDto> _refunds = [];

    private readonly Dictionary<Guid, decimal> _quantities = [];
    private readonly Dictionary<Guid, RefundDisposition> _dispositions = [];

    private string _reason = string.Empty;
    private string _note = string.Empty;
    private Guid? _drawerId;
    private bool _payoutConfirmed;

    /// <summary>
    /// True while a refund the screen sent has no answer. The exact key and body stay held, the inputs are locked,
    /// and the only permitted action is asking about that same refund (specification D06, task 20's rule).
    /// </summary>
    private bool _refundUnresolved;
    private string? _refundKey;

    private string SaleNumber => _sale?.SaleNumber ?? "…";

    private string SummaryLine => _sale is { } sale
        ? $"{sale.Currency} sale posted {sale.PostedAtUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC - "
            + $"{sale.Lines.Count} line(s), {StateSentenceOf(sale)}. Branch and cashier are named on the reprint below."
        : "Reading the server's own record of this sale.";

    private string StateSentence => _sale is { } sale ? StateTextOf(sale) : "Reading…";

    private Color StateColor
    {
        get
        {
            if (_sale is not { } sale)
            {
                return Color.Default;
            }

            return Uncollected
                ? Color.Warning
                : sale.Status == SaleState.Reversed
                    ? Color.Secondary
                    : Color.Success;
        }
    }

    private bool Uncollected => _sale is { } sale
        && (sale.Provisional
            || sale.Status == SaleState.NeedsReview
            || sale.Payments.All(payment => payment.Status != PaymentStatus.Confirmed));

    private string UncollectedNote
        => "The money on this sale has not been confirmed by the server, so there is nothing here to pay back. "
            + "A capture a terminal holds is a recovery case on that terminal, never a settled sale.";

    private string ReprintSentence
        => _receipt is null
            ? "The reprint is the server's stored snapshot; it is read from the same record the sale was posted with."
            : SalesRules.ReprintNote;

    private string RefundTotalsSentence => _position is { } position
        ? $"{SalesRules.MoneyText(position.RefundedAmount, position.Currency)} paid back of "
            + $"{SalesRules.MoneyText(position.TotalAmount, position.Currency)}; "
            + $"{SalesRules.MoneyText(position.RemainingRefundableAmount, position.Currency)} still refundable."
        : "Cumulative figures come from the server's own refund lines.";

    private string PreviewSentence
    {
        get
        {
            if (_position is not { } position)
            {
                return "Nothing is chosen yet.";
            }

            var preview = SalesRules.PreviewTotal(position, _quantities);

            return preview <= 0m
                ? $"Nothing is chosen yet. {SalesRules.PreviewNote}"
                : $"Preview of this screen's choice: {SalesRules.MoneyText(preview, position.Currency)}. {SalesRules.PreviewNote}";
        }
    }

    private string PayoutSentenceText
        => _position is not { } position
            ? "Read the server's position before offering a refund."
            : SalesRules.PayoutSentence(
                position,
                SalesRules.PreviewTotal(position, _quantities),
                position.Drawers.FirstOrDefault(drawer => drawer.RegisterId == _drawerId)?.RegisterName);

    private string KeyHeldNote => _refundUnresolved
        ? "This attempt keeps its key: pressing the button again asks the server about the same refund."
        : SalesRules.UnresolvedRefundNote;

    private string RefundAuthorityNote => SalesRules.RefundAuthorityNote(_canRefund);

    private string RefundButtonLabel => _refundUnresolved
        ? "Ask the server about this same refund"
        : "Refund the chosen quantities";

    private bool CanSendRefund
        => !_busy
            && _position is { } position
            && (_refundUnresolved
                || SalesRules.CanSubmit(_reason, _drawerId, _payoutConfirmed, _quantities, position));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
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

            _canRefund = session.Permissions.Contains(Permissions.SalesRefund, StringComparer.Ordinal);

            // The cost a restock returns is a book-keeping figure: it is shown to an account that may post to the
            // books, and named as the server's figure for anyone else (task 21, recipe step 2).
            _canSeeCost = session.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal);

            _sale = await SalesApi.GetSaleAsync(SaleId).ConfigureAwait(false);

            // The position and the history are read together: the panel an operator acts on has to be the one the
            // server would answer with, and the refunds are the cumulative evidence behind it.
            _position = await SalesApi.GetRefundPositionAsync(SaleId).ConfigureAwait(false);
            _refunds = await SalesApi.GetRefundsAsync(SaleId).ConfigureAwait(false);

            foreach (var line in _position.Lines)
            {
                _dispositions.TryAdd(line.SaleLineId, RefundDisposition.Restock);
            }

            await LoadReceiptAsync().ConfigureAwait(false);

            _state = ShellStateKind.Content;
            _message = null;
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                404 => ShellStateKind.Empty,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = exception.StatusCode switch
            {
                403 => "This sale belongs to a branch the account may not read, or the authority for the sale history is missing.",
                404 => "No sale with that identity exists in this business.",
                _ => exception.Message,
            };
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task LoadReceiptAsync()
    {
        try
        {
            _receipt = await SalesApi.GetReceiptAsync(SaleId).ConfigureAwait(false);
            _receiptProblem = null;
        }
        catch (ApiException exception)
        {
            // The sale is posted either way; a reprint that cannot be read is said out loud rather than hidden.
            _receipt = null;
            _receiptProblem = $"The sale is posted, but its receipt could not be read: {exception.Message}";
        }
    }

    // ------------------------------------------------------------------ refund panel

    private decimal QuantityFor(SaleRefundLinePositionDto line) => _quantities.GetValueOrDefault(line.SaleLineId);

    private RefundDisposition DispositionFor(SaleRefundLinePositionDto line)
        => _dispositions.GetValueOrDefault(line.SaleLineId, RefundDisposition.Restock);

    private Task SetQuantityAsync(SaleRefundLinePositionDto line, decimal value)
    {
        if (_refundUnresolved)
        {
            return Task.CompletedTask;
        }

        // The server's remaining quantity is the bound; the field cannot go past it and the refusal is restated if a
        // stale page somehow sends one.
        _quantities[line.SaleLineId] = Math.Clamp(value, 0m, line.RemainingQuantity);

        return Task.CompletedTask;
    }

    private Task SetDispositionAsync(SaleRefundLinePositionDto line, RefundDisposition value)
    {
        if (_refundUnresolved || !line.MovedStock)
        {
            return Task.CompletedTask;
        }

        _dispositions[line.SaleLineId] = value;

        return Task.CompletedTask;
    }

    private string CostText(SaleRefundLinePositionDto line)
        => _canSeeCost
            ? SalesRules.Money(line.UnitCost)
            : "the server's cost figure";

    private async Task SendRefundAsync()
    {
        if (_position is not { } position || _busy)
        {
            return;
        }

        _refundKey ??= SalesRules.NewRefundKey(position.SaleNumber);

        var request = BuildRequest(position);

        _busy = true;
        _problem = null;
        _committed = null;
        StateHasChanged();

        try
        {
            var refund = await SalesApi.RefundSaleAsync(SaleId, request, _refundKey).ConfigureAwait(false);

            _committed = $"Refund {refund.RefundNumber} of {SalesRules.MoneyText(refund.Amount, refund.Currency)} is posted "
                + $"against {refund.SaleNumber}, with its journal {(refund.JournalEntryNumber is { Length: > 0 } entry ? entry : "number")} and its drawer movement.";

            // The intention is complete: the key is forgotten and the next refund gets its own.
            _refundKey = null;
            _refundUnresolved = false;
            _quantities.Clear();
            _dispositions.Clear();
            _reason = string.Empty;
            _note = string.Empty;
            _payoutConfirmed = false;

            await RefreshPositionAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;

            // A refusal that proves nothing was committed frees the operator to correct and start again; anything
            // else - a lost answer above all - keeps this exact key and body (specification D06, task 21 step 3).
            _refundUnresolved = !SalesRules.CommittedNothing(exception.Code);

            if (!_refundUnresolved)
            {
                _refundKey = null;
            }

            if (exception.Code is ApiErrorCodes.AlreadyRefunded or ApiErrorCodes.SameKeyDifferentPayload)
            {
                await RefreshPositionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _refundUnresolved = true;
            _problem = $"The refund's outcome is unknown: {exception.Message}. {SalesRules.UnresolvedRefundNote}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private RefundSaleRequest BuildRequest(SaleRefundPositionDto position)
    {
        var lines = position.Lines
            .Where(line => _quantities.GetValueOrDefault(line.SaleLineId) > 0m)
            .Select(line => new RefundLineRequest(
                line.SaleLineId,
                _quantities[line.SaleLineId],
                DispositionFor(line)))
            .ToList();

        return new RefundSaleRequest(
            _reason.Trim(),
            string.IsNullOrWhiteSpace(_note) ? null : _note.Trim(),
            _drawerId!.Value,
            lines);
    }

    private async Task AbandonRefundAsync()
    {
        if (!_refundUnresolved || _busy)
        {
            return;
        }

        _busy = true;

        try
        {
            // Abandoning is not forgetting: the position is read again so the operator sees whether the refund they
            // are walking away from was committed after all.
            await RefreshPositionAsync().ConfigureAwait(false);

            _refundKey = null;
            _refundUnresolved = false;
            _quantities.Clear();
            _payoutConfirmed = false;
            _message = "The unresolved refund was dropped from this screen. The position above is the server's, so if "
                + "that refund did post, it shows in the totals and the remaining quantities.";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task RefreshPositionAsync()
    {
        _position = await SalesApi.GetRefundPositionAsync(SaleId).ConfigureAwait(false);
        _refunds = await SalesApi.GetRefundsAsync(SaleId).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ wording

    private static string StateTextOf(PostedSaleDto sale) => sale switch
    {
        { Status: SaleState.NeedsReview } => "Awaiting review - not settled money",
        { Status: SaleState.Reversed } => "Reversed - nothing more can be refunded",
        { PaymentStatus: PaymentStatus.Pending } => "Payment not confirmed",
        { PaymentStatus: PaymentStatus.Bounced } => "Payment bounced",
        { PaymentStatus: PaymentStatus.Voided } => "Payment voided",
        { PaymentStatus: PaymentStatus.Refunded } => "Fully refunded",
        _ => "Posted and paid",
    };

    private string StateSentenceOf(PostedSaleDto sale) => StateTextOf(sale);

    private string PaymentStateText(PaymentStatus status) => status switch
    {
        PaymentStatus.Confirmed => "Confirmed",
        PaymentStatus.Pending => "Not confirmed",
        PaymentStatus.Bounced => "Bounced",
        PaymentStatus.Refunded => "Refunded",
        PaymentStatus.Voided => "Voided",
        _ => status.ToString(),
    };

    private static decimal? ChangeOf(PostedPaymentDto payment)
        => payment.Method.Equals("Cash", StringComparison.OrdinalIgnoreCase) && payment.CashReceived is { } received
            ? received - payment.Amount
            : null;

    private static string TaxRateText(decimal rate)
        => $"{Math.Round(rate * 100m, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
