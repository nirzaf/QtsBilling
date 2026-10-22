using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The quote workbench (specification task 22, recipe steps 1 to 3): what the quote is, what its lines were priced at,
/// which commands its status actually allows, and the amendment, rejection or conversion that a status demands a
/// reason for. A draft is edited; a quote that has been sent is only ever superseded by a new revision with its own
/// reason, and conversion keeps one key so a lost answer resolves the same order instead of creating a second one.
/// </summary>
public partial class QuoteEditor : IAsyncDisposable
{
    [Parameter] public Guid QuoteId { get; set; }

    [Inject] private IQuoteApiClient QuotesApi { get; set; } = default!;
    [Inject] private ICustomerApiClient Customers { get; set; } = default!;
    [Inject] private IProductApiClient Products { get; set; } = default!;
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;

    private SalesQuoteDto? _quote;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private bool _busy;
    private bool _started;
    private bool _canManage;

    private bool _editingLines;
    private bool _amending;
    private readonly List<QuoteLineForm> _lines = [];
    private ReferenceItem? _customer;
    private DateTime? _expiresOn;
    private DateTime? _promisedOn;
    private string? _note;
    private IReadOnlyList<TaxRateDto> _taxRates = [];
    private ReferenceItem? _pickedProduct;

    private OrderRules.QuoteAction? _pendingAction;
    private string _reason = string.Empty;
    private string _sendNote = string.Empty;

    // Conversion is the one command that creates a document, so it keeps its key while the answer is outstanding
    // (specification D06, task 20's rule applied to the quote-to-order step).
    private bool _awaitingAnswer;
    private string? _conversionKey;
    private ConvertQuoteRequest? _conversionBody;
    private Guid? _convertedOrderId;

    private bool _printing;
    private int? _printedRevision;

    private string Headline => _quote is not { } quote
        ? "Reading the server's own record of this quote."
        : $"{quote.LocationName ?? "a branch no longer listed"} - "
            + $"{quote.CustomerName ?? "a counter quote"}, quoted {quote.QuoteDate:yyyy-MM-dd}, valid to {quote.ExpiryDate:yyyy-MM-dd}.";

    private string StatusSentence => _quote is not { } quote
        ? "Reading…"
        : OrderRules.QuoteStatusText(quote.Status, quote.ExpiryDate, DateOnly.FromDateTime(DateTime.UtcNow));

    private IReadOnlyList<OrderRules.QuoteAction> AllowedActions
        => _quote is not { } quote
            ? []
            // The server is the judge of whether a quote has already produced its order, so the convert action
            // stays offered for an accepted quote and its refusal is shown rather than a guess being made here.
            : OrderRules.AllowedQuoteActions(quote.Status, alreadyConverted: false);

    private bool Amending => _amending;

    private bool IsEditingLines
        => _canManage && _editingLines && (_quote?.Status == "Draft" || _amending);

    private string LinesHeading => _amending ? "The amended lines" : "The lines of this draft";

    private string LinesHelp
        => _amending
            ? OrderRules.RevisionNote
            : "A draft is still editable. The server prices every line - a catalog line at its catalog price and tax, "
                + "a written line at the price and rate you choose - and returns the document it snapshotted.";

    private string PreviewSentence
        => "The preview below is this screen's arithmetic, labelled as a preview: the server recomputes the document "
            + "and its rounding when it is written.";

    private string ActionsSentence
        => _canManage
            ? "Only the commands this quote's current state allows are offered, and each one is refused with the server's own sentence if it no longer applies."
            : $"Reading this quote needs no special authority; the commands need '{Permissions.OrdersManage}', which this account does not hold.";

    private string NoActionsSentence
        => _quote?.Status switch
        {
            "Rejected" => "This quote was rejected. Nothing further can be done with it - write a new quote for the customer.",
            "Expired" => "This quote expired. Its revisions stay as evidence; a new quote is a new document.",
            "Cancelled" => "This quote was cancelled. It is kept for the record and cannot be sent again.",
            "Accepted" => "This accepted quote has already produced its order. The order is where the commitment continues.",
            _ => "There is nothing to do with this quote from here.",
        };

    private QuoteRevisionDto PrintRevision
        => _quote is not { } quote
            ? throw new InvalidOperationException("the reprint needs a loaded quote")
            : quote.Revisions.FirstOrDefault(row => row.RevisionNumber == _printedRevision)
                ?? quote.Revisions.OrderByDescending(row => row.RevisionNumber).FirstOrDefault()
                ?? new QuoteRevisionDto(
                    Guid.Empty, quote.CurrentRevision, quote.QuoteDate, quote.ExpiryDate, quote.PromisedDeliveryDate,
                    quote.Currency, quote.SubtotalAmount, quote.DiscountAmount, quote.TaxAmount, quote.TotalAmount,
                    quote.RoundingAmount, quote.Note, "Current draft (not yet a revision)", Guid.Empty, quote.CreatedAtUtc,
                    quote.Lines);

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

            _canManage = session.Permissions.Contains(Permissions.OrdersManage, StringComparer.Ordinal);
            _taxRates = await Pricing.ListTaxRatesAsync().ConfigureAwait(false);
            _quote = await QuotesApi.GetAsync(QuoteId).ConfigureAwait(false);

            SeedForm();

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
                404 => "No quote with that identity exists in this business.",
                403 => "This quote belongs to a branch this account may not reach.",
                _ => exception.Message,
            };
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void SeedForm()
    {
        if (_quote is not { } quote)
        {
            return;
        }

        _lines.Clear();
        foreach (var line in quote.Lines)
        {
            _lines.Add(QuoteLineForm.FromOrderLine(line, line.Quantity));
        }

        _customer = quote.CustomerId is { } customerId && !string.IsNullOrEmpty(quote.CustomerName)
            ? ReferenceItem.From(customerId, quote.CustomerName)
            : null;
        _expiresOn = quote.ExpiryDate.ToDateTime(TimeOnly.MinValue);
        _promisedOn = quote.PromisedDeliveryDate?.ToDateTime(TimeOnly.MinValue);
        _note = quote.Note;
        _editingLines = _canManage && quote.Status == "Draft";
        _amending = false;
    }

    // ------------------------------------------------------------------ line editing

    private bool CanSaveLines
        => _canManage
            && !_busy
            && !_awaitingAnswer
            && _quote is { } quote
            && (quote.Status == "Draft" || _amending)
            && _expiresOn is not null
            && Form().IsComplete
            && (!_amending || !string.IsNullOrWhiteSpace(_reason));

    private string SaveLinesLabel => _amending ? "Record the amendment as a new revision" : "Save the draft";

    private QuoteDraftForm Form()
    {
        var form = new QuoteDraftForm
        {
            LocationId = _quote?.LocationId,
            CustomerId = _customer?.Id,
            CustomerName = _customer?.Name,
            QuoteDate = _quote is { } quote ? quote.QuoteDate : null,
            ExpiryDate = _expiresOn is { } expires ? DateOnly.FromDateTime(expires) : default,
            PromisedDeliveryDate = _promisedOn is { } promised ? DateOnly.FromDateTime(promised) : null,
            Note = _note,
        };

        foreach (var line in _lines)
        {
            form.Lines.Add(line);
        }

        return form;
    }

    private string LineSentence(QuoteLineForm line)
    {
        if (!line.IsComplete)
        {
            return "This line is not finished yet - it stays on the screen and is never sent half-written.";
        }

        if (line.HasVariant)
        {
            return $"{OrderRules.Money(line.Quantity)} of {line.VariantName ?? "the chosen item"} - the catalog price, tax rate and mode come with the item and a quote may not restate them.";
        }

        var rate = line.TaxRateId is { } rateId ? _taxRates.FirstOrDefault(candidate => candidate.Id == rateId)?.Rate ?? 0m : 0m;
        var preview = OrderRules.PreviewLineTotal(line.Quantity, line.UnitPrice, line.TaxMode, rate, line.DiscountAmount);

        return $"{OrderRules.Money(line.Quantity)} at {OrderRules.MoneyText(line.UnitPrice, _quote?.Currency ?? string.Empty)} - "
            + $"{TaxModes.Sentence(line.TaxMode)} This screen's preview is {OrderRules.MoneyText(preview, _quote?.Currency ?? string.Empty)}; "
            + "the server recomputes the document and its rounding when it is written.";
    }

    private void SetDescription(int index, string? value)
    {
        if (!_awaitingAnswer)
        {
            _lines[index].Description = value ?? string.Empty;
        }
    }

    private void SetQuantity(int index, decimal value)
    {
        if (!_awaitingAnswer)
        {
            _lines[index].Quantity = value;
        }
    }

    private void SetUnitPrice(int index, decimal value)
    {
        if (!_awaitingAnswer)
        {
            _lines[index].UnitPrice = value;
        }
    }

    private void SetDiscount(int index, decimal value)
    {
        if (!_awaitingAnswer)
        {
            _lines[index].DiscountAmount = value;
        }
    }

    private void SetTaxRate(int index, Guid? value)
    {
        if (!_awaitingAnswer)
        {
            _lines[index].TaxRateId = value;
        }
    }

    private void RemoveLine(int index)
    {
        if (_awaitingAnswer || _lines.Count == 0)
        {
            return;
        }

        _lines.RemoveAt(Math.Min(index, _lines.Count - 1));

        if (_lines.Count == 0)
        {
            _lines.Add(new QuoteLineForm());
        }
    }

    private Task CustomerChangedAsync(ReferenceItem? customer)
    {
        _customer = customer;

        return Task.CompletedTask;
    }

    private Task ExpiresOnChangedAsync(DateTime? day)
    {
        _expiresOn = day;

        return Task.CompletedTask;
    }

    private Task PromisedOnChangedAsync(DateTime? day)
    {
        _promisedOn = day;

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchCustomersAsync(string term, CancellationToken token)
    {
        var page = await Customers.GetCustomersAsync(term, cursor: null, pageSize: 15, cancellationToken: token)
            .ConfigureAwait(false);

        return page.Items
            .Select(customer => ReferenceItem.From(customer.Id, customer.Name, customer.IsActive ? null : "out of service"))
            .ToArray();
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchCatalogAsync(string term, CancellationToken token)
    {
        var page = await Products.GetProductsAsync(term, cursor: null, pageSize: 15, cancellationToken: token)
            .ConfigureAwait(false);

        return page.Items.Select(product => ReferenceItem.From(product.Id, product.Name, product.Sku)).ToArray();
    }

    private async Task AddCatalogLineAsync(ReferenceItem? product)
    {
        _pickedProduct = product;

        if (product is null || _awaitingAnswer)
        {
            return;
        }

        try
        {
            var detail = await Products.GetProductAsync(product.Id).ConfigureAwait(false);
            var variant = detail.Variants.FirstOrDefault(candidate => candidate.IsActive && candidate.IsDefault)
                ?? detail.Variants.FirstOrDefault(candidate => candidate.IsActive);

            if (variant is null)
            {
                _problem = $"{product.Name} has no active variant to quote.";

                return;
            }

            _lines.Add(new QuoteLineForm
            {
                VariantId = variant.Id,
                VariantName = string.IsNullOrWhiteSpace(variant.Sku) ? variant.Name : $"{variant.Name} ({variant.Sku})",
                Description = variant.Name,
                Quantity = 1m,
            });
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;
        }
    }

    private async Task SaveLinesAsync()
    {
        if (_quote is not { } quote || !CanSaveLines)
        {
            return;
        }

        _busy = true;
        _problem = null;
        _committed = null;
        StateHasChanged();

        try
        {
            var updated = _amending
                ? await QuotesApi.AmendAsync(quote.Id, Form().ToAmendRequest(quote.Version, _reason)).ConfigureAwait(false)
                : await QuotesApi.UpdateAsync(quote.Id, Form().ToUpdateRequest(quote.Version)).ConfigureAwait(false);

            _committed = _amending
                ? $"Revision {updated.CurrentRevision} was recorded with the reason you wrote, and the quote is sent again for the customer to accept."
                : $"The draft holds {updated.Lines.Count} priced line(s) totalling {OrderRules.MoneyText(updated.TotalAmount, updated.Currency)}.";

            _quote = updated;
            _amending = false;
            _editingLines = updated.Status == "Draft";
            _reason = string.Empty;
            SeedFormPreservingStatus(updated);
        }
        catch (ApiException exception)
        {
            _problem = OrderRules.RefusalSentence(exception, "quote");

            // A version conflict means the screen's numbers are no longer the server's: reload rather than guess.
            if (exception.Code == ApiErrorCodes.OptimisticConcurrencyFailure)
            {
                await LoadAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void SeedFormPreservingStatus(SalesQuoteDto updated)
    {
        _lines.Clear();
        foreach (var line in updated.Lines)
        {
            _lines.Add(QuoteLineForm.FromOrderLine(line, line.Quantity));
        }

        _expiresOn = updated.ExpiryDate.ToDateTime(TimeOnly.MinValue);
        _promisedOn = updated.PromisedDeliveryDate?.ToDateTime(TimeOnly.MinValue);
        _note = updated.Note;
    }

    // ------------------------------------------------------------------ commands

    private static string ActionLabel(OrderRules.QuoteAction action) => action switch
    {
        OrderRules.QuoteAction.SaveDraft => "Edit the draft",
        OrderRules.QuoteAction.Send => "Send it",
        OrderRules.QuoteAction.Accept => "Record the customer's acceptance",
        OrderRules.QuoteAction.Reject => "Record a rejection",
        OrderRules.QuoteAction.Amend => "Amend (new revision)",
        OrderRules.QuoteAction.Convert => "Convert to a sales order",
        OrderRules.QuoteAction.Cancel => "Cancel the quote",
        _ => action.ToString(),
    };

    private static bool Danger(OrderRules.QuoteAction action)
        => action is OrderRules.QuoteAction.Reject or OrderRules.QuoteAction.Cancel;

    private string CommandSentence(OrderRules.QuoteAction action) => action switch
    {
        OrderRules.QuoteAction.Send => OrderRules.SendNote,
        OrderRules.QuoteAction.Accept => "Recording an acceptance is a decision on this revision: it is what conversion later commits to.",
        OrderRules.QuoteAction.Reject => "A rejection keeps the quote and its revision as evidence and closes it. The reason is required.",
        OrderRules.QuoteAction.Amend => OrderRules.RevisionNote,
        OrderRules.QuoteAction.Cancel => "Cancelling leaves the document and its history in place; a converted quote can no longer be cancelled.",
        OrderRules.QuoteAction.Convert => OrderRules.ConversionKeyNote,
        _ => "The server judges this command and answers with its own sentence.",
    };

    private async Task BeginActionAsync(OrderRules.QuoteAction action)
    {
        _problem = null;
        _committed = null;
        _reason = string.Empty;
        _sendNote = string.Empty;

        switch (action)
        {
            case OrderRules.QuoteAction.SaveDraft:
                _editingLines = true;
                _amending = false;
                StateHasChanged();

                return;

            case OrderRules.QuoteAction.Amend:
                _editingLines = true;
                _amending = true;
                StateHasChanged();

                return;

            default:
                _pendingAction = action;
                StateHasChanged();

                return;
        }
    }

    private void CancelPending()
    {
        _pendingAction = null;
        StateHasChanged();
    }

    private bool CanRunPending
        => !_busy
            && !_awaitingAnswer
            && _pendingAction is { } action
            && OrderRules.CanSendQuoteCommand(
                action,
                action == OrderRules.QuoteAction.Send ? _sendNote : _reason,
                false,
                false,
                _quote?.Version ?? 0L)
            && (!OrderRules.QuoteActionNeedsReason(action) || !string.IsNullOrWhiteSpace(_reason));

    private async Task RunPendingAsync()
    {
        if (_quote is not { } quote || _pendingAction is not { } action || (!CanRunPending && !_awaitingAnswer))
        {
            return;
        }

        _busy = true;
        _problem = null;
        StateHasChanged();

        try
        {
            var reason = action == OrderRules.QuoteAction.Send
                ? (string.IsNullOrWhiteSpace(_sendNote) ? null : _sendNote.Trim())
                : _reason.Trim();

            var result = action switch
            {
                OrderRules.QuoteAction.Send => await QuotesApi.SendAsync(quote.Id, new QuoteCommandRequest(quote.Version, reason)).ConfigureAwait(false),
                OrderRules.QuoteAction.Accept => await QuotesApi.AcceptAsync(quote.Id, new QuoteCommandRequest(quote.Version, reason)).ConfigureAwait(false),
                OrderRules.QuoteAction.Reject => await QuotesApi.RejectAsync(quote.Id, new QuoteCommandRequest(quote.Version, reason)).ConfigureAwait(false),
                OrderRules.QuoteAction.Cancel => await QuotesApi.CancelAsync(quote.Id, new QuoteCommandRequest(quote.Version, reason)).ConfigureAwait(false),
                _ => await ConvertAsync(quote).ConfigureAwait(false),
            };

            if (result is not null)
            {
                _quote = result;
            }

            _committed = CommittedSentence(action);
            _pendingAction = null;
            _awaitingAnswer = false;
            _conversionKey = null;
            _conversionBody = null;
            SeedForm();
            _editingLines = _quote?.Status == "Draft" && _canManage;
        }
        catch (ApiException exception)
        {
            _problem = OrderRules.RefusalSentence(exception, "quote");

            if (action == OrderRules.QuoteAction.Convert)
            {
                // A refusal that proves nothing was written releases the intention; a lost answer keeps the exact
                // key and body so the retry resolves the same order rather than making a second one.
                _awaitingAnswer = !OrderRules.CommittedNothing(exception.Code);

                if (!_awaitingAnswer)
                {
                    _conversionKey = null;
                    _conversionBody = null;
                }
            }

            if (exception.Code is ApiErrorCodes.OptimisticConcurrencyFailure
                or ApiErrorCodes.QuoteExpired
                or ApiErrorCodes.QuoteNotAccepted
                or ApiErrorCodes.QuoteNotDraft)
            {
                await LoadAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _awaitingAnswer = true;
            _problem = $"The answer to that command never arrived: {exception.Message}. {OrderRules.ConversionKeyNote}";
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task<SalesQuoteDto?> ConvertAsync(SalesQuoteDto quote)
    {
        _conversionKey ??= OrderRules.NewConversionKey(quote.QuoteNumber);
        _conversionBody ??= new ConvertQuoteRequest(quote.Version, string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim());

        var conversion = await QuotesApi.ConvertAsync(quote.Id, _conversionBody, _conversionKey).ConfigureAwait(false);

        _convertedOrderId = conversion.Order.Id;
        _committed = $"Order {conversion.Order.OrderNumber} was created from the accepted revision "
            + $"{conversion.Order.CurrentRevision} with the quoted terms unchanged - {OrderRules.MoneyText(conversion.Order.TotalAmount, conversion.Order.Currency)}, "
            + "no stock moved and no revenue recognised.";

        return conversion.Quote;
    }

    private string CommittedSentence(OrderRules.QuoteAction action) => action switch
    {
        OrderRules.QuoteAction.Send => "Sent: the revision is recorded as issued with its note. Nothing was emailed - this product has no mail service, and the audit shows who sent it and when.",
        OrderRules.QuoteAction.Accept => "The acceptance is recorded against this revision. Conversion is now available.",
        OrderRules.QuoteAction.Reject => "The rejection is recorded with its reason and the quote is closed. Its history stays.",
        OrderRules.QuoteAction.Cancel => "The quote is cancelled. The document and its revisions remain for the record.",
        _ => "The command is recorded.",
    };

    private async Task AbandonConversionAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            // Abandoning is not forgetting: the quote is re-read, so an order that did get created is seen rather
            // than retried into a second one.
            _quote = await QuotesApi.GetAsync(QuoteId).ConfigureAwait(false);

            _awaitingAnswer = false;
            _conversionKey = null;
            _conversionBody = null;
            _pendingAction = null;
            SeedForm();
            _message = "The unresolved conversion was dropped from this screen and the quote was re-read above. If the "
                + "order did reach the server it is on the orders screen; a second conversion is refused by the server "
                + "rather than silently allowed, so no order is duplicated from here.";
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ reprint

    private void TogglePrint()
    {
        _printing = !_printing;
        StateHasChanged();
    }

    private Task ShowRevisionAsync(int revisionNumber)
    {
        _printedRevision = revisionNumber;
        _printing = true;
        StateHasChanged();

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
