using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Catalog;

/// <summary>
/// The catalog list an operator actually works from (specification task 18, recipe step 1): server-side search over
/// product name, SKU and any variant's name, SKU or barcode; server-side cursor paging; out-of-service records kept
/// reachable for history; and a create step that describes the product and the one default variant it must have.
/// Nothing here computes a rule the server owns - it asks for the same values the API checks and shows the refusal.
/// </summary>
public partial class Products
{
    [Inject] private IProductApiClient Catalog { get; set; } = default!;
    [Inject] private IMasterDataApiClient MasterData { get; set; } = default!;
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private const int PageSize = 25;

    private readonly PagedListState<ProductListItem> _list;

    private readonly Dictionary<MasterDataResource, IReadOnlyList<MasterDataDto>> _references = new();

    private IReadOnlyList<TaxRateDto> _taxRates = [];

    private bool _initialized;
    private bool _busy;
    private bool _canWrite;
    private bool _includeInactive;
    private string _searchText = string.Empty;
    private string? _problem;

    private bool _creating;
    private readonly ProductForm _form = new();
    private string _variantName = string.Empty;
    private decimal _sellingPrice;
    private decimal? _purchasePrice;

    public Products()
    {
        _list = new PagedListState<ProductListItem>(LoadPageAsync, StateHasChanged, PageSize);
    }

    // Runs after the first interactive render, never during static prerender (no session cookie yet).
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
        {
            return;
        }

        _initialized = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _problem = null;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                await _list.ReloadAsync().ConfigureAwait(false);
                return;
            }

            _canWrite = session.Permissions.Contains(Permissions.ProductsWrite, StringComparer.Ordinal);

            if (!session.Permissions.Contains(Permissions.ProductsRead, StringComparer.Ordinal))
            {
                _problem = $"Seeing the catalog needs the '{Permissions.ProductsRead}' authority.";
                return;
            }

            await LoadReferencesAsync().ConfigureAwait(false);
            await _list.ReloadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task LoadReferencesAsync()
    {
        // Every reference list is read with retired entries included: a product filed under a category the
        // business stopped using must still show that category's name, not "uncategorised".
        foreach (var kind in Enum.GetValues<MasterDataResource>())
        {
            _references[kind] = await MasterData.ListAsync(kind, includeInactive: true).ConfigureAwait(false);
        }

        _taxRates = await Pricing.ListTaxRatesAsync(includeInactive: true).ConfigureAwait(false);
    }

    private async Task<PagedRows<ProductListItem>> LoadPageAsync(string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await Catalog.GetProductsAsync(
            search, cursor, pageSize, _includeInactive, token).ConfigureAwait(false);

        return new PagedRows<ProductListItem>(
            [.. page.Items.Select(product => new ProductListItem(product, NameOf))],
            page.NextCursor);
    }

    private string? NameOf(Guid? id)
    {
        if (id is not { } wanted)
        {
            return null;
        }

        foreach (var rows in _references.Values)
        {
            var match = rows.FirstOrDefault(row => row.Id == wanted);

            if (match is not null)
            {
                return match.Name;
            }
        }

        var rate = _taxRates.FirstOrDefault(candidate => candidate.Id == wanted);

        return rate is null ? null : $"{rate.Name} ({CatalogRules.Percent(rate.Rate)})";
    }

    private IReadOnlyList<MasterDataDto> ReferenceOf(MasterDataResource kind)
        => _references.TryGetValue(kind, out var rows) ? rows : [];

    private Task SearchChanged(string term)
    {
        _searchText = term;
        _problem = null;
        return _list.SearchAsync(term);
    }

    private Task IncludeInactiveChanged()
    {
        _problem = null;
        return _list.SearchAsync(_searchText);
    }

    private Task ReloadAsync()
    {
        _problem = null;
        return _list.ReloadAsync();
    }

    private Task NextAsync() => _list.NextPageAsync();

    private Task PreviousAsync() => _list.PreviousPageAsync();

    private void Open(Guid id) => Navigation.NavigateTo($"catalog/products/{id:D}");

    // ------------------------------------------------------------------ create

    private void BeginCreate()
    {
        _problem = null;
        _creating = true;
        _form.IsNew = true;
        _form.ProductId = null;
        _form.Version = 0;
        _variantName = string.Empty;
        _sellingPrice = 0m;
        _purchasePrice = null;
    }

    private void CancelCreate()
    {
        _creating = false;
        _form.Note = null;
    }

    private async Task CreateAsync()
    {
        if (_busy || !_form.CanSave)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            var created = await Catalog.CreateProductAsync(
                _form.ToCreate(_variantName, _sellingPrice, _purchasePrice)).ConfigureAwait(false);

            // The reference pickers refresh from the server's answer, and the screen moves to the record it just
            // made, where the committed version is visible (task 18, recipe step 4).
            await LoadReferencesAsync().ConfigureAwait(false);
            Snackbar.Add(CatalogRules.CommittedVersion(created.Product.Version, created.Product.Name), Severity.Success);
            Navigation.NavigateTo($"catalog/products/{created.Product.Id:D}");
        }
        catch (ApiException exception)
        {
            // The typed description stays on the screen; only the refusal is added to it.
            _form.HoldNote(NoteFor(exception));
            _problem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    /// <summary>The sentence the screen shows for a refusal, keeping a duplicate key separate from a lost race.</summary>
    internal static string NoteFor(ApiException exception) => CatalogRules.RefusalNote(exception);

    private static string Describe(ApiException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return exception.Message;
        }

        var lines = exception.Errors.Select(pair => $"{pair.Key}: {string.Join(" ", pair.Value)}");

        return string.Join(" ", [exception.Message, .. lines]);
    }

    public async ValueTask DisposeAsync()
    {
        await _list.DisposeAsync().ConfigureAwait(false);
    }
}
