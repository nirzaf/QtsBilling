using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Catalog;

/// <summary>
/// The product workbench (specification task 18, recipe steps 2 to 4): the description and its references, the
/// variants a counter actually sells, the price split the customer pays, and the stock the branches hold - each
/// read from the owner of that fact and written through the versioned contract task 17 shipped.
/// </summary>
public partial class ProductEditor
{
    [Parameter] public Guid ProductId { get; set; }

    [Inject] private IProductApiClient Catalog { get; set; } = default!;
    [Inject] private IMasterDataApiClient MasterData { get; set; } = default!;
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly Dictionary<MasterDataResource, IReadOnlyList<MasterDataDto>> _references = new();

    private IReadOnlyList<TaxRateDto> _taxRates = [];

    private ProductDetailDto? _detail;

    private readonly List<VariantRow> _rows = [];

    private readonly ProductForm _form = new();

    private readonly VariantForm _adding = new();

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private bool _busy;
    private bool _canWrite;
    private bool _started;
    private bool _addingVariant;
    private Guid? _selected;
    private IReadOnlyList<InventoryBalanceDto> _balances = [];
    private string _archiveReason = string.Empty;
    private string _restoreReason = string.Empty;
    private string _variantReason = string.Empty;
    private Guid? _replacementDefaultId;

    private string? _variantProblem;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private ProductDto? Product => _detail?.Product;

    public string TitleText => Product is { } product ? product.Name : "Product";

    private IReadOnlyList<VariantRow> ActiveRows => [.. _rows.Where(row => row.IsActive)];

    private IEnumerable<VariantRow> DefaultCandidates =>
        [.. _rows.Where(row => row.IsActive && row.Id != (_selected ?? Guid.Empty))];

    private PricePreview Preview
        => CatalogRules.Preview(
            Selected?.Variant.SellingPrice ?? 0m,
            Product?.TaxRate ?? 0m,
            Product?.TaxType);

    private VariantRow? Selected
        => _rows.FirstOrDefault(row => row.Id == _selected) ?? _rows.FirstOrDefault();

    private async Task LoadAsync()
    {
        _busy = true;
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

            _canWrite = session.Permissions.Contains(Permissions.ProductsWrite, StringComparer.Ordinal);

            if (!session.Permissions.Contains(Permissions.ProductsRead, StringComparer.Ordinal))
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Seeing the catalog needs the '{Permissions.ProductsRead}' authority.";
                return;
            }

            await LoadReferencesAsync().ConfigureAwait(false);

            var detail = await Catalog.GetProductAsync(ProductId).ConfigureAwait(false);

            Apply(detail);

            _state = ShellStateKind.Content;

            await LoadStockAsync().ConfigureAwait(false);
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
            _message = _state == ShellStateKind.Empty
                ? "This product does not belong to the business you are signed in to, or it was never created."
                : Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void Apply(ProductDetailDto detail)
    {
        _detail = detail;
        _form.Load(detail.Product);

        var keepSelected = _selected;

        _rows.Clear();
        _rows.AddRange(detail.Variants.Select(variant => new VariantRow(variant)));

        _selected = keepSelected is { } held && _rows.Any(row => row.Id == held)
            ? held
            : detail.Variants.FirstOrDefault(variant => variant.IsDefault)?.Id ?? detail.Variants.FirstOrDefault()?.Id;

        // A variant write moves the product's version too, so every buffer is re-armed against the version this
        // screen can now honestly send (task 17).
        foreach (var row in _rows)
        {
            row.Buffer.ExpectedProductVersion = detail.Product.Version;
        }

        _adding.ExpectedProductVersion = detail.Product.Version;
    }

    private async Task LoadReferencesAsync()
    {
        foreach (var kind in Enum.GetValues<MasterDataResource>())
        {
            _references[kind] = await MasterData.ListAsync(kind, includeInactive: true).ConfigureAwait(false);
        }

        _taxRates = await Pricing.ListTaxRatesAsync(includeInactive: true).ConfigureAwait(false);
    }

    private async Task LoadStockAsync()
    {
        _balances = [];

        if (Selected is not { } row || Product?.TrackStock != true)
        {
            return;
        }

        // One bounded request per selected variant: the branches that hold it, not a page of every balance the
        // business owns.
        var page = await Inventory.GetBalancesAsync(variantId: row.Id, pageSize: 100).ConfigureAwait(false);

        _balances = page.Items;
        row.StockTotal = page.Items.Sum(balance => balance.OnHand);
    }

    private IReadOnlyList<MasterDataDto> ReferenceOf(MasterDataResource kind)
        => _references.TryGetValue(kind, out var rows) ? rows : [];

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

    private Task SelectVariantAsync(Guid id)
    {
        _selected = id;
        _variantProblem = null;
        return LoadStockAsync();
    }

    // ------------------------------------------------------------------ writes

    private async Task SaveProductAsync()
    {
        var product = Product;

        if (_busy || product is null || !_form.CanSave)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            var saved = await Catalog.UpdateProductAsync(product.Id, _form.ToUpdate()).ConfigureAwait(false);

            Apply(saved);
            await LoadReferencesAsync().ConfigureAwait(false);
            await LoadStockAsync().ConfigureAwait(false);

            _committed = CatalogRules.CommittedVersion(saved.Product.Version, "Product");
            Snackbar.Add(_committed, Severity.Success);
        }
        catch (ApiException exception)
        {
            // The correction stays on screen; the version the refusal names is read back so the next attempt is
            // deliberate (task 18, T18.02).
            _form.HoldNote(NoteFor(exception));
            _problem = NoteFor(exception);

            if (exception.IsConflict)
            {
                await ReloadQuietlyAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task AddVariantAsync()
    {
        var product = Product;

        if (_busy || product is null || !_adding.CanSave)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await Catalog.CreateVariantAsync(product.Id, _adding.ToCreate()).ConfigureAwait(false);

            _addingVariant = false;
            _adding.ClearForNew(0);
            await SaveAndRefreshAsync("Variant added").ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _adding.HoldNote(NoteFor(exception));
            _variantProblem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task SaveVariantAsync(VariantRow row)
    {
        var product = Product;

        if (_busy || product is null || !row.Buffer.CanSave)
        {
            return;
        }

        _busy = true;
        _variantProblem = null;

        try
        {
            await Catalog.UpdateVariantAsync(product.Id, row.Id, row.Buffer.ToUpdate()).ConfigureAwait(false);
            await SaveAndRefreshAsync("Variant saved").ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            row.Buffer.HoldNote(NoteFor(exception));
            _variantProblem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task AddVariantKeydown(KeyboardEventArgs args)
    {
        // A keyboard wedge sends Enter after the code, so Enter is the save action for a scanned barcode. Both
        // key names are accepted because a scanner's numeric keypad codes report the numpad Enter.
        if (args.Key is "Enter" or "NumpadEnter")
        {
            await AddVariantAsync().ConfigureAwait(false);
        }
    }

    private void BeginAddVariant()
    {
        _adding.ClearForNew(Product?.Version ?? 0);
        _adding.Name = string.Empty;
        _addingVariant = true;
        _variantProblem = null;
    }

    private void CancelVariant(VariantRow row)
    {
        row.Cancel();
        _addingVariant = false;
        _variantProblem = null;
    }

    private void BeginArchiveVariant(VariantRow row)
    {
        _selected = row.Id;
        _variantReason = string.Empty;
        _replacementDefaultId = row.IsDefault
            ? ActiveRows.FirstOrDefault(other => other.Id != row.Id)?.Id
            : null;
        _variantProblem = null;
    }

    private async Task ArchiveVariantAsync(VariantRow row)
    {
        var product = Product;

        if (_busy || product is null || string.IsNullOrWhiteSpace(_variantReason))
        {
            return;
        }

        _busy = true;
        _variantProblem = null;

        try
        {
            await Catalog.ArchiveVariantAsync(
                product.Id,
                row.Id,
                new ArchiveVariantRequest(product.Version, _variantReason.Trim(), _replacementDefaultId)).ConfigureAwait(false);

            _variantReason = string.Empty;
            _replacementDefaultId = null;
            await SaveAndRefreshAsync("Variant taken out of service").ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _variantProblem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task ArchiveProductAsync()
    {
        var product = Product;

        if (_busy || product is null || string.IsNullOrWhiteSpace(_archiveReason))
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Take this product out of service",
            ArchiveDisclosure(product),
            yesText: "Take out of service",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await Catalog.ArchiveProductAsync(
                product.Id, new ArchiveProductRequest(product.Version, _archiveReason.Trim())).ConfigureAwait(false);

            _archiveReason = string.Empty;
            await SaveAndRefreshAsync("Product taken out of service").ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task RestoreProductAsync()
    {
        var product = Product;

        if (_busy || product is null || string.IsNullOrWhiteSpace(_restoreReason))
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Bring this product back",
            CatalogRules.RestoreSentence(ActiveRows.Count),
            yesText: "Bring it back",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await Catalog.RestoreProductAsync(
                product.Id, new RestoreProductRequest(product.Version, _restoreReason.Trim())).ConfigureAwait(false);

            _restoreReason = string.Empty;
            await SaveAndRefreshAsync("Product back in service").ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = NoteFor(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    /// <summary>The disclosure the archive confirmation shows, stated only from what this screen has read.</summary>
    private string ArchiveDisclosure(ProductDto product)
        => CatalogRules.ArchiveSentence(
            product.Name,
            ActiveRows.Count,
            Selected?.Name,
            Selected?.StockTotal ?? 0m,
            product.TrackStock);

    private async Task SaveAndRefreshAsync(string what)
    {
        var detail = await Catalog.GetProductAsync(ProductId).ConfigureAwait(false);

        Apply(detail);
        await LoadStockAsync().ConfigureAwait(false);

        _committed = CatalogRules.CommittedVersion(detail.Product.Version, what);
        Snackbar.Add(_committed, Severity.Success);
    }

    private async Task ReloadQuietlyAsync()
    {
        try
        {
            var detail = await Catalog.GetProductAsync(ProductId).ConfigureAwait(false);

            Apply(detail);
            await LoadStockAsync().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // A reload that fails leaves the note and the typed values on screen; the record itself is unchanged.
        }
    }

    private void BackToList() => Navigation.NavigateTo("catalog/products");

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

    private string TaxModeLine
        => Product is { } product
            ? $"{CatalogRules.TaxModeText(product.TaxType)} - {CatalogRules.Percent(product.TaxRate)}"
            : string.Empty;

    private string CostingLine => CatalogRules.CostingText(Product?.TrackStock ?? false);

    private string KindLine => CatalogRules.StockKindText(Product?.TrackStock ?? false);
}
