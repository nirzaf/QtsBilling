using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages;

/// <summary>
/// The supplier half of the party workbench (specification task 15). Suppliers were API-only before this: an owner
/// could not add, correct or retire one without a request tool. The screen uses the same paged state, the same
/// deactivation-not-deletion rule and the same conflict handling as the customer workbench, through
/// <see cref="ISupplierApiClient"/> against the routes purchasing already published.
/// </summary>
public partial class Suppliers
{
    [Inject] private ISupplierApiClient SupplierApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private const int PageSize = 25;

    private readonly PagedListState<SupplierRow> _list;

    private bool _initialized;
    private bool _busy;
    private bool _canManage;
    private bool _includeInactive;
    private string _searchText = string.Empty;
    private string? _problem;

    private bool _creating;
    private string _newName = string.Empty;
    private string? _newEmail;
    private string? _newPhone;
    private string? _newAddress;

    public Suppliers()
    {
        _list = new PagedListState<SupplierRow>(LoadPageAsync, StateHasChanged, PageSize);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
        {
            return;
        }

        _initialized = true;

        var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

        if (session is null)
        {
            await _list.ReloadAsync().ConfigureAwait(false);
            StateHasChanged();
            return;
        }

        _canManage = session.Permissions.Contains(Permissions.PurchasingManage, StringComparer.Ordinal);

        await _list.ReloadAsync().ConfigureAwait(false);
        StateHasChanged();
    }

    private async Task<PagedRows<SupplierRow>> LoadPageAsync(string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await SupplierApi.GetSuppliersAsync(
            search, cursor, pageSize, _includeInactive ? true : null, token).ConfigureAwait(false);

        return new PagedRows<SupplierRow>(
            [.. page.Items.Select(item => new SupplierRow(item))],
            page.NextCursor);
    }

    private Task SearchChanged(string term)
    {
        _searchText = term;
        _problem = null;
        return _list.SearchAsync(term);
    }

    private Task IncludeInactiveChanged()
    {
        _problem = null;
        return _list.ReloadAsync();
    }

    private Task ReloadAsync()
    {
        _problem = null;
        return _list.ReloadAsync();
    }

    private Task NextAsync() => _list.NextPageAsync();

    private Task PreviousAsync() => _list.PreviousPageAsync();

    // ------------------------------------------------------------------ create

    private void BeginCreate()
    {
        _problem = null;
        _creating = true;
        _newName = string.Empty;
        _newEmail = null;
        _newPhone = null;
        _newAddress = null;
    }

    private void CancelCreate() => _creating = false;

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newName) || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await SupplierApi.CreateSupplierAsync(new SaveSupplierRequest(_newName, _newEmail, _newPhone, _newAddress))
                .ConfigureAwait(false);
            Snackbar.Add("Supplier added.", Severity.Success);
            CancelCreate();
            await _list.FirstPageAsync().ConfigureAwait(false);
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

    // ------------------------------------------------------------------ edit

    private async Task ToggleEdit(SupplierRow row)
    {
        if (!row.IsEditing)
        {
            row.IsEditing = true;
            return;
        }

        await SaveEditAsync(row);
    }

    private async Task SaveEditAsync(SupplierRow row)
    {
        // The correction the operator typed stays on screen until the server accepts it. A conflict is reported
        // and the row stays open for review rather than re-read over the top of the other edit (T15.03).
        _busy = true;
        _problem = null;

        try
        {
            var updated = await SupplierApi.UpdateSupplierAsync(row.Id, row.ToRequest(), row.Dto.Version)
                .ConfigureAwait(false);

            row.Apply(updated);
            Snackbar.Add("Supplier saved.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
            row.ConflictNote = exception.IsConflict
                ? "This supplier changed elsewhere first. Reload the list, then repeat the correction you still intend."
                : null;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task DeactivateConfirmation(SupplierRow row)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Take supplier out of service",
            "Every purchase order, receipt and bill that names this supplier keeps resolving it. The supplier stops "
                + "appearing in pickers for new purchasing, and stays reachable here for as long as that history exists.",
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
            await SupplierApi.DeactivateSupplierAsync(row.Id).ConfigureAwait(false);
            Snackbar.Add("Supplier taken out of service. Nothing it was part of was changed.", Severity.Success);
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
