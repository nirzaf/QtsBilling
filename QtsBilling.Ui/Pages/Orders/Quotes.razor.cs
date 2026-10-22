using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Orders;

/// <summary>
/// The quote list a salesperson starts from and the one place a new draft is opened (specification task 22,
/// recipe step 1). The list is the server's own: bounded search on the quote number, a status filter, a cursor the
/// server issued, and the branch and customer names the read model carries so nothing on screen is an identifier a
/// person has to recognise. Creating a draft is deliberately small - the editor is where a quote gets its lines.
/// </summary>
public partial class Quotes : IAsyncDisposable
{
    private const int PageSize = 20;

    [Inject] private IQuoteApiClient QuotesApi { get; set; } = default!;
    [Inject] private ISalesOrderApiClient OrdersApi { get; set; } = default!;
    [Inject] private ICustomerApiClient Customers { get; set; } = default!;
    [Inject] private IProductApiClient Products { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly PagedListState<SalesQuoteSummaryDto> _list;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string BusinessContext { get; set; } = "Loading this business…";
    private bool _busy;
    private bool _started;

    private string _search = string.Empty;
    private string _status = string.Empty;

    // ---------------------------------------------------------------- create panel
    private bool _creating;
    private string? _createProblem;
    private readonly QuoteDraftForm _draft = new();
    private ReferenceItem? _draftCustomer;
    private DateTime? _quotedOn;
    private DateTime? _expiresOn;
    private DateTime? _promisedOn;
    private string? _draftNote;
    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private IReadOnlyList<TaxRateDto> _taxRates = [];

    private ReferenceItem? _pickedProduct;
    private Guid? _pickedVariantId;
    private string? _pickedVariantName;
    private string _lineDescription = string.Empty;
    private decimal _lineQuantity = 1m;
    private decimal _lineUnitPrice;
    private Guid? _lineTaxRateId;

    private DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public Quotes()
        => _list = new PagedListState<SalesQuoteSummaryDto>(LoadPageAsync, StateHasChanged, PageSize);

    /// <summary>The line the panel will send, or null while it is not yet describable.</summary>
    private QuoteLineForm? OpeningLine
    {
        get
        {
            if (_pickedVariantId is { } variantId)
            {
                return new QuoteLineForm
                {
                    VariantId = variantId,
                    VariantName = _pickedVariantName,
                    Description = _lineDescription,
                    Quantity = _lineQuantity,
                    DiscountAmount = 0m,
                };
            }

            if (string.IsNullOrWhiteSpace(_lineDescription))
            {
                return null;
            }

            return new QuoteLineForm
            {
                Description = _lineDescription.Trim(),
                Quantity = _lineQuantity,
                UnitPrice = _lineUnitPrice,
                TaxRateId = _lineTaxRateId,
                TaxMode = _lineTaxRateId is null ? "Exempt" : "Exclusive",
            };
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task<PagedRows<SalesQuoteSummaryDto>> LoadPageAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await QuotesApi.ListAsync(
            string.IsNullOrEmpty(_status) ? null : _status, search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<SalesQuoteSummaryDto>(page.Items, page.NextCursor);
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

            BusinessContext = session.BusinessName is { Length: > 0 } business
                ? $"{business} - quotes of the branches this account may reach."
                : "No business is selected for this session.";

            await LoadReferencesAsync().ConfigureAwait(false);
            await _list.SearchAsync(_search).ConfigureAwait(false);

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
                ? $"Quoting needs the '{QtsBilling.Contracts.Identity.Permissions.OrdersManage}' authority or the sale authority this account does not hold."
                : exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    /// <summary>The reference data the create panel needs, read once and only when something is being created.</summary>
    private async Task LoadReferencesAsync()
    {
        _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);
        _taxRates = await Pricing.ListTaxRatesAsync().ConfigureAwait(false);
    }

    private void ToggleCreate()
    {
        _creating = !_creating;
        _createProblem = null;

        if (_creating && _locations.Count == 0)
        {
            _ = LoadReferencesAsync();
        }

        StateHasChanged();
    }

    private Task ReloadAsync() => LoadAsync();

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
        _state = _list.State;
        _message = _list.Message;
        StateHasChanged();
    }

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

    // ------------------------------------------------------------------ create panel

    private Task DraftCustomerChangedAsync(ReferenceItem? customer)
    {
        _draftCustomer = customer;
        _draft.CustomerId = customer?.Id;
        _draft.CustomerName = customer?.Name;

        return Task.CompletedTask;
    }

    private Task QuotedOnChangedAsync(DateTime? day)
    {
        _quotedOn = day;
        _draft.QuoteDate = day is null ? null : DateOnly.FromDateTime(day.Value);

        return Task.CompletedTask;
    }

    private Task ExpiresOnChangedAsync(DateTime? day)
    {
        _expiresOn = day;
        _draft.ExpiryDate = day is null ? default : DateOnly.FromDateTime(day.Value);
        StateHasChanged();

        return Task.CompletedTask;
    }

    private Task PromisedOnChangedAsync(DateTime? day)
    {
        _promisedOn = day;
        _draft.PromisedDeliveryDate = day is null ? null : DateOnly.FromDateTime(day.Value);

        return Task.CompletedTask;
    }

    private async Task PickedProductChangedAsync(ReferenceItem? product)
    {
        _pickedProduct = product;
        _createProblem = null;

        if (product is null)
        {
            _pickedVariantId = null;
            _pickedVariantName = null;
            StateHasChanged();

            return;
        }

        try
        {
            // A quote line references a concrete variant, so the product's default active variant is chosen by name
            // here and named on the line; a product with no active variant is refused rather than guessed at.
            var detail = await Products.GetProductAsync(product.Id).ConfigureAwait(false);
            var variant = detail.Variants.FirstOrDefault(candidate => candidate.IsActive && candidate.IsDefault)
                ?? detail.Variants.FirstOrDefault(candidate => candidate.IsActive);

            if (variant is null)
            {
                _pickedVariantId = null;
                _pickedVariantName = null;
                _createProblem = $"{product.Name} has no active variant to quote. Quote it as a written line or fix the catalog record first.";
            }
            else
            {
                _pickedVariantId = variant.Id;
                _pickedVariantName = string.IsNullOrWhiteSpace(variant.Sku) ? variant.Name : $"{variant.Name} ({variant.Sku})";
                _lineDescription = string.Empty;
                _lineUnitPrice = 0m;
                _lineTaxRateId = null;
            }
        }
        catch (ApiException exception)
        {
            _pickedVariantId = null;
            _createProblem = exception.Message;
        }

        StateHasChanged();
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchCatalogAsync(string term, CancellationToken token)
    {
        var page = await Products.GetProductsAsync(term, cursor: null, pageSize: 15, cancellationToken: token)
            .ConfigureAwait(false);

        return page.Items
            .Select(product => ReferenceItem.From(product.Id, product.Name, product.Sku))
            .ToArray();
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchCustomersAsync(string term, CancellationToken token)
    {
        var page = await Customers.GetCustomersAsync(term, cursor: null, pageSize: 15, cancellationToken: token)
            .ConfigureAwait(false);

        return page.Items
            .Select(customer => ReferenceItem.From(
                customer.Id,
                customer.Name,
                customer.IsActive ? null : "out of service - kept for the history it belongs to"))
            .ToArray();
    }

    private async Task CreateAsync()
    {
        if (!CanCreate || OpeningLine is not { } line)
        {
            return;
        }

        _draft.LocationId ??= _locations.FirstOrDefault()?.Id;
        _draft.Note = _draftNote;
        _draft.Lines.Clear();
        _draft.Lines.Add(line);

        _busy = true;
        _createProblem = null;

        try
        {
            var created = await QuotesApi.CreateAsync(
                _draft.ToCreateRequest(), OrderRules.NewCommandKey("quote-draft")).ConfigureAwait(false);

            Navigation.NavigateTo($"/orders/quotes/{created.Id}");
        }
        catch (ApiException exception)
        {
            _createProblem = OrderRules.RefusalSentence(exception, "quote");
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    public async ValueTask DisposeAsync() => await _list.DisposeAsync().ConfigureAwait(false);

    /// <summary>How a status is coloured, so a state that commits nothing is not dressed as progress.</summary>
    private static Color StatusColor(string status) => status switch
    {
        "Accepted" => Color.Success,
        "Sent" => Color.Info,
        "Draft" => Color.Default,
        "Rejected" or "Expired" or "Cancelled" => Color.Secondary,
        _ => Color.Default,
    };
}
