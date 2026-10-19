using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages;

public partial class Customers
{
    [Inject] private ICustomerApiClient CustomerApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private const int PageSize = 100;

    private List<CustomerRow> _rows = [];
    private bool _initialized;
    private bool _busy;
    private string _searchText = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set => _searchText = value;
    }

    // Runs after the first interactive render, never during static prerender (no session cookie yet).
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
        {
            return;
        }

        _initialized = true;

        var session = await Accounts.GetSessionAsync();

        if (session is null || session.BusinessId is null)
        {
            Navigation.NavigateTo("login", replace: true);
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy = true;
        StateHasChanged();

        try
        {
            var page = await CustomerApi.GetCustomersAsync(SearchText, pageSize: PageSize);
            _rows = [.. page.Items.Select(item => new CustomerRow(item))];
        }
        catch (ApiException exception)
        {
            Snackbar.Add(exception.Message, Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SearchCustomersOnInput(string searchText)
    {
        SearchText = searchText;
        await LoadAsync();
    }

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
        try
        {
            var updated = await CustomerApi.UpdateCustomerAsync(row.Id, row.ToRequest(), row.Dto.Version);
            row.Apply(updated);
            Snackbar.Add("Customer saved.", Severity.Success);
        }
        catch (ApiException exception)
        {
            Snackbar.Add(exception.Message, Severity.Error);

            if (exception.IsConflict)
            {
                await LoadAsync();
            }
        }
    }

    private async Task DeleteCustomerConfirmation(MouseEventArgs args, CustomerRow row)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Deactivate customer",
            "Historical invoices and sales keep resolving this customer, so the record is deactivated instead of erased.",
            yesText: "Deactivate",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        try
        {
            await CustomerApi.DeactivateCustomerAsync(row.Id);
            Snackbar.Add("Customer deactivated.", Severity.Success);
            await LoadAsync();
        }
        catch (ApiException exception)
        {
            Snackbar.Add(exception.Message, Severity.Error);
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

        try
        {
            await CustomerApi.CreateCustomerAsync(model.ToRequest());
            Snackbar.Add("Customer added.", Severity.Success);
            await LoadAsync();
        }
        catch (ApiException exception)
        {
            Snackbar.Add(exception.Message, Severity.Error);
        }
    }
}
