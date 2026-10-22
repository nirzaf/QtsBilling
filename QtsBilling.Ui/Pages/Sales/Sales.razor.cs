using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Pricing;

namespace QtsBilling.Ui.Pages.Sales;

/// <summary>
/// The sales history an operator works from (specification task 21, recipe step 1): the server's posted sales,
/// bounded by number, business-zone date, branch and customer, each row carrying the payment and refund state the
/// server holds. Nothing is fetched without the operator asking, nothing is filtered in the browser over a whole
/// table, and a superseded page cannot repaint the list the operator has moved past (the task 02/13/14 rule).
/// </summary>
public partial class Sales : IAsyncDisposable
{
    private const int PageSize = 20;

    [Inject] private ISaleApiClient SalesApi { get; set; } = default!;
    [Inject] private ICustomerApiClient Customers { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IBusinessSetupApiClient Setup { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;

    private readonly PagedListState<SaleHistoryDto> _list;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string _businessContext = "Loading this business…";
    private bool _started;

    private string _search = string.Empty;
    private Guid? _locationFilter;
    private ReferenceItem? _customer;
    private DateTime? _fromDay;
    private DateTime? _toDay;
    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;
    private bool _zoneIsBusiness;

    public Sales()
        => _list = new PagedListState<SaleHistoryDto>(LoadAsync, StateHasChanged, PageSize);

    /// <summary>The dates are read in the business's own zone when it can be read, and the screen says which zone it used.</summary>
    private string DateZoneNote => _zoneIsBusiness ? _zone.Id : "this device's zone";

    private DateTimeOffset? FromUtc
        => _fromDay is { } day ? PricingRules.DayStartUtc(DateOnly.FromDateTime(day), _zone) : null;

    private DateTimeOffset? ToUtc
        => _toDay is { } day ? PricingRules.DayAfterUtc(DateOnly.FromDateTime(day), _zone) : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task<PagedRows<SaleHistoryDto>> LoadPageAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await SalesApi.GetSalesAsync(
            FromUtc, ToUtc, _locationFilter, _customer?.Id, search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<SaleHistoryDto>(page.Items, page.NextCursor);
    }

    private Task<PagedRows<SaleHistoryDto>> LoadAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
        => LoadPageAsync(search, cursor, pageSize, token);

    private async Task LoadAsync()
    {
        _problem = null;
        _state = ShellStateKind.Loading;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = null;
                return;
            }

            _businessContext = session.BusinessName is { Length: > 0 } business
                ? $"{business} - {session.AllowedLocationIds.Count} branch(es) this account may reach."
                : "No business is selected for this session.";

            _zone = await ResolveZoneAsync().ConfigureAwait(false);
            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

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
                ? "Reading the sales history needs the sale authority this account does not hold."
                : exception.Message;
        }
        finally
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// The business zone comes from its own settings, which not every counter may read; the fallback is named rather
    /// than silently assumed (finding F21, task 16's rule).
    /// </summary>
    private async Task<TimeZoneInfo> ResolveZoneAsync()
    {
        try
        {
            var settings = await Setup.GetBusinessSettingsAsync().ConfigureAwait(false);

            try
            {
                var found = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
                _zoneIsBusiness = true;

                return found;
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _zoneIsBusiness = false;

                return LocalZone();
            }
        }
        catch (ApiException)
        {
            _zoneIsBusiness = false;

            return LocalZone();
        }
    }

    private static TimeZoneInfo LocalZone()
    {
        try
        {
            return TimeZoneInfo.Local;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    // ------------------------------------------------------------------ filters

    private Task SearchChangedAsync(string term)
    {
        _search = term?.Trim() ?? string.Empty;

        return RefilterAsync();
    }

    private Task LocationChangedAsync(Guid? locationId)
    {
        _locationFilter = locationId;

        return RefilterAsync();
    }

    private Task CustomerChangedAsync(ReferenceItem? customer)
    {
        _customer = customer;

        return RefilterAsync();
    }

    private Task FromDayChangedAsync(DateTime? day)
    {
        _fromDay = day;

        return RefilterAsync();
    }

    private Task ToDayChangedAsync(DateTime? day)
    {
        _toDay = day;

        return RefilterAsync();
    }

    private Task ClearFiltersAsync()
    {
        _search = string.Empty;
        _locationFilter = null;
        _customer = null;
        _fromDay = null;
        _toDay = null;

        return RefilterAsync();
    }

    /// <summary>Every filter change is a new list, so the paging history that belonged to the old one is dropped.</summary>
    private async Task RefilterAsync()
    {
        _problem = null;

        try
        {
            await _list.SearchAsync(_search).ConfigureAwait(false);
            _state = _list.State;
            _message = _list.Message;
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;
        }

        StateHasChanged();
    }

    private async Task ReloadAsync()
    {
        _problem = null;

        try
        {
            await _list.ReloadAsync().ConfigureAwait(false);
            _state = _list.State;
            _message = _list.Message;
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;
        }

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

    /// <summary>The customer lookup the selector runs: server-side, by name, bounded to a page.</summary>
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

    public async ValueTask DisposeAsync() => await _list.DisposeAsync().ConfigureAwait(false);
}
