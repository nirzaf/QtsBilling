using System.Data;
using QtsModels;
using QtsServices;

namespace QtsBilling.UIForms;

public partial class Customers : Form
{
    private static string? SelectedCustomerId { get; set; }

    public Customers()
    {
        InitializeComponent();
    }

    private void BtnSaveCustomer_Click(object sender, EventArgs e)
    {
        CustomerService customerService = new();
        var customer = new Customer
        {
            Name = TextBoxForName.Text,
            Address = TextBoxForAddress.Text,
            Phone = TextBoxForMobile.Text,
            Email = TextBoxForEmail.Text
        };
        
        MessageBox.Show(customerService.AddCustomer(customer)? "Customer added successfully" : "Failed to add customer");
        LoadCustomersDgv();
    }

    private void Customers_Load(object sender, EventArgs e)
    {
        LoadCustomersDgv();
    }

    private void LoadCustomersDgv()
    {
        SystemUtilitiesService systemUtilitiesService = new();
        systemUtilitiesService.SyncLocalWithCloud();
        CustomerService customerService = new();
        var customers = customerService.GetCustomers();
        DgvCustomers.DataSource = customers;
        DgvCustomers.Columns["IsDeleted"]!.Visible = false;
        DgvCustomers.Columns["Id"]!.Visible = false;
    }

    private void DgvCustomers_CellClick(object sender, DataGridViewCellEventArgs e)
    {
        if (DgvCustomers.SelectedRows.Count <= 0) return;

        TextBoxForName.Text = DgvCustomers.SelectedRows[0].Cells["Name"].Value.ToString();
        TextBoxForEmail.Text = DgvCustomers.SelectedRows[0].Cells["Email"].Value.ToString();
        TextBoxForMobile.Text = DgvCustomers.SelectedRows[0].Cells["Phone"].Value.ToString();
        TextBoxForAddress.Text = DgvCustomers.SelectedRows[0].Cells["Address"].Value.ToString();
        CustomerService customerService = new();
        SelectedCustomerId = customerService.GetCustomerIdByPhone(TextBoxForMobile.Text.Trim());
    }

    private void BtnUpdateCustomer_Click(object sender, EventArgs e)
    {
        try
        {
            Customer updateCustomer = new()
            {
                Name = TextBoxForName.Text.Trim(),
                Email = TextBoxForEmail.Text.Trim(),
                Phone = TextBoxForMobile.Text.Trim(),
                Address = TextBoxForAddress.Text.Trim()
            };

            CustomerService cs = new();
            if (!cs.UpdateCustomer(updateCustomer, SelectedCustomerId)) return;

            MessageBox.Show(@"Customer updated successfully");
            LoadCustomersDgv();
        }
        catch (Exception ex)
        {
            ExceptionService es = new();
            es.CreateLog(ex);
        }
    }

    private void BtnAddCustomer_Click(object sender, EventArgs e)
    {
        TextBoxForName.Text = string.Empty;
        TextBoxForEmail.Text = string.Empty;
        TextBoxForMobile.Text = string.Empty;
        TextBoxForAddress.Text = string.Empty;
        SelectedCustomerId = string.Empty;
    }

    private void TextBoxForSearchCustomers_TextChanged(object sender, EventArgs e)
    {
        DgvCustomers.DataSource = GetSearchedCustomers(TextBoxForSearchCustomers.Text.Trim());
    }

    private static DataTable GetSearchedCustomers(string searchText)
    {
        CustomerService customerService = new();
        var customers = customerService.SearchCustomers(searchText);
        var searchedCustomers = new DataTable();
        searchedCustomers.Columns.Add("Name");
        searchedCustomers.Columns.Add("Email");
        searchedCustomers.Columns.Add("Phone");
        searchedCustomers.Columns.Add("Address");
        foreach (var customer in customers)
        {
            var row = searchedCustomers.NewRow();
            row["Name"] = customer.Name;
            row["Email"] = customer.Email;
            row["Phone"] = customer.Phone;
            row["Address"] = customer.Address;
            searchedCustomers.Rows.Add(row);
        }

        return searchedCustomers;
    }

    private void BtnDeleteCustomer_Click(object sender, EventArgs e)
    {
        if (DgvCustomers.SelectedRows.Count <= 0) return;

        CustomerService customerService = new();

        if (SelectedCustomerId is not null)
        {
            if (!customerService.DeleteCustomer(SelectedCustomerId)) return;
            MessageBox.Show(@"Customer deleted successfully");
            LoadCustomersDgv();
        }
        else
        {
            MessageBox.Show(@"Please select a customer to delete");
        }
    }
}