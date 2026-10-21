using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages;

public partial class Customers
{
    [Inject] private ICustomerApiClient CustomerApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private const int PageSize = 25;

    private readonly PagedListState<CustomerRow> _list;

    private bool _initialized;
    private bool _busy;
    private bool _canWrite;
    private bool _includeInactive;
    private string _searchText = string.Empty;
    private string? _problem;

    public Customers()
    {
        // One page at a time, searched and paged by the server, and a superseded load cancelled rather than
        // allowed to repaint the list the operator has already moved past (specification task 02, task 13, task 15).
        _list = new PagedListState<CustomerRow>(LoadPageAsync, StateHasChanged, PageSize);
    }

    // Runs after the first interactive render, never during static prerender (no session cookie yet).
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

        _canWrite = session.Permissions.Contains(Permissions.CustomersWrite, StringComparer.Ordinal);

        await _list.ReloadAsync().ConfigureAwait(false);
        StateHasChanged();
    }

    private async Task<PagedRows<CustomerRow>> LoadPageAsync(string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var page = await CustomerApi.GetCustomersAsync(
            search, cursor, pageSize, _includeInactive ? true : null, token).ConfigureAwait(false);

        return new PagedRows<CustomerRow>(
            [.. page.Items.Select(item => new CustomerRow(item))],
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

    private async Task ToggleEdit(MouseEventArgs args, CustomerRow row)
    {
        if (!row.IsEditing)
        {
            row.IsEditing = true;
            return;
        }

        await SaveEditAsync(row);
    }

    private async Task SaveEditAsync(CustomerRow row)
    {
        // The operator's typed values stay where they are until the server has accepted them. A refusal is
        // reported and the row is left open for review, never silently re-read over the top of an edit
        // (specification task 15, T15.03).
        _busy = true;
        _problem = null;

        try
        {
            var updated = await CustomerApi.UpdateCustomerAsync(row.Id, row.ToRequest(), row.Dto.Version)
                .ConfigureAwait(false);

            row.Apply(updated);
            _problem = null;
            Snackbar.Add("Customer saved.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
            row.ConflictNote = exception.IsConflict ? "This row changed elsewhere. Correct it again against the version you can now reload, or reload the list." : null;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task DeleteCustomerConfirmation(MouseEventArgs args, CustomerRow row)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Deactivate customer",
            "Historical invoices and sales keep resolving this customer, so the record is deactivated instead of erased. "
                + "It disappears from the default list and from every picker for new work; switch \"out of service\" back on to reach it.",
            yesText: "Deactivate",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await CustomerApi.DeactivateCustomerAsync(row.Id).ConfigureAwait(false);
            Snackbar.Add("Customer deactivated. Nothing it posted was changed.", Severity.Success);
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

    private async Task AddCustomer()
    {
        var parameters = new DialogParameters { ["Model"] = new CustomerEditModel() };
        var dialog = await DialogService.ShowAsync<AddCustomerDialog>("Add New Customer", parameters);
        var result = await dialog.Result;

        if (result is null || result.Canceled)
        {
            return;
        }

        var model = (CustomerEditModel)result.Data!;
        _busy = true;
        _problem = null;

        try
        {
            await CustomerApi.CreateCustomerAsync(model.ToRequest()).ConfigureAwait(false);
            Snackbar.Add("Customer added.", Severity.Success);
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
        // Leaving the page must not leave a load that can still write to disposed state.
        await _list.DisposeAsync().ConfigureAwait(false);
    }
}
