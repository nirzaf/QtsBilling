using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using QtsModels;
using QtsServices;

namespace QtsWebApp.Client.Pages;

public partial class Customers : IDisposable
{
    [Inject] private CustomerService CustomerService { get; set; } = new();
    [Inject] private IDialogService DialogService { get; set; } = default!;

    private List<Customer> _pagedCustomers = [];
    private string _searchText = string.Empty;
    private int CurrentPage { get; set; } = 1;
    private int PageSize { get; set; } = 10;

    [Parameter]
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                SearchCustomersOnInput(_searchText).ConfigureAwait(false);
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _pagedCustomers = await CustomerService.PagedCustomersAsync(CurrentPage, PageSize);
    }

    private async Task SearchCustomersOnInput(string searchText)
    {
        if (string.IsNullOrEmpty(SearchText) || string.IsNullOrWhiteSpace(SearchText))
        {
            _pagedCustomers = await CustomerService.GetCustomersAsync();
        }
        else
        {
            SearchText = searchText;
            _pagedCustomers = await CustomerService.SearchCustomersAsync(SearchText);
        }
    }

    private async Task ToggleEdit(MouseEventArgs args, Customer customer)
    {
        if (customer.IsEditing)
        {
            await UpdateCustomer(customer);
        }
        customer.IsEditing = !customer.IsEditing;
    }

    private async Task DeleteCustomerConfirmation(MouseEventArgs args, Customer customer)
    {
        bool? result = await DialogService.ShowMessageBox(
            "Warning",
            "Deleting can not be undone!",
            yesText: "Delete!", cancelText: "Cancel");

        if (result == true)
        {
           bool response = await DeleteCustomer(customer.Id);
           if (response)
           {
               _pagedCustomers = await CustomerService.GetCustomersAsync();
               StateHasChanged();
           }
        }
    }

    private async Task<bool> AddCustomer(Customer customer)
    {
       bool response = await CustomerService.AddCustomerAsync(customer);
       return response;
    }

    private async Task UpdateCustomer(Customer customer)
    {
       await CustomerService.UpdateCustomerAsync(customer, customer.Id);
    }

    private async Task<bool> DeleteCustomer(string? id)
    {
       bool response = await CustomerService.DeleteCustomerAsync(id);
       return response;
    }
    
    private bool _addCustomerDialogVisible;
    private readonly Customer _newCustomer = new() { Id = Guid.NewGuid().ToString() };
    private async Task AddCustomer()
    {
        var parameters = new DialogParameters { { "NewCustomer", new Customer { Id = Guid.NewGuid().ToString() } } };

        var dialog = await DialogService.ShowAsync<AddCustomerDialog>("Add New Customer", parameters);
        var result = await dialog.Result;

        if (result is { Canceled: false })
        {
            var newCustomer = (Customer)result.Data!;
            bool response = await CustomerService.AddCustomerAsync(newCustomer);
            if (response)
            {
                _pagedCustomers = await CustomerService.GetCustomersAsync();
                StateHasChanged();
            }
            _addCustomerDialogVisible = false;
            StateHasChanged();
        }
    }
    private void CancelAddCustomer()
    {
        _addCustomerDialogVisible = false;
    }

    private async Task<bool> SubmitAddCustomer()
    {
        bool response = await AddCustomer(_newCustomer);
        if (response)
        {
            _pagedCustomers = await CustomerService.GetCustomersAsync();
            StateHasChanged();
        }
        _addCustomerDialogVisible = false;
        StateHasChanged();
        return response;
    }


    public void Dispose()
    {
        // Dispose of any resources here.
    }
}