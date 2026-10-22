using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Banking;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Abstractions;
using QtsWebApp.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Every browser call goes through the authenticated server API (QB-15): no database driver, connection
// string or server secret is ever part of the WebAssembly bundle. The handler attaches this session's
// anti-forgery proof to state-changing calls, which the server requires from task 09 onwards.
builder.Services.AddScoped(_ => new HttpClient(new AntiforgeryHandler { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<ICustomerApiClient, CustomerApiClient>();
builder.Services.AddScoped<IAccountApiClient, AccountApiClient>();
builder.Services.AddScoped<IBusinessSetupApiClient, BusinessSetupApiClient>();
builder.Services.AddScoped<IAdministrationUserApiClient, AdministrationUserApiClient>();
builder.Services.AddScoped<IDeviceAdministrationApiClient, DeviceAdministrationApiClient>();
builder.Services.AddScoped<QtsBilling.Contracts.Accounting.IAccountingApiClient, QtsBilling.Contracts.Accounting.AccountingApiClient>();
builder.Services.AddScoped<ISupplierApiClient, SupplierApiClient>();
builder.Services.AddScoped<IMasterDataApiClient, MasterDataApiClient>();
builder.Services.AddScoped<QtsBilling.Contracts.Pricing.IPricingApiClient, QtsBilling.Contracts.Pricing.PricingApiClient>();
builder.Services.AddScoped<IProductApiClient, ProductApiClient>();
builder.Services.AddScoped<IInventoryApiClient, InventoryApiClient>();
builder.Services.AddScoped<IBankingApiClient, BankingApiClient>();
builder.Services.AddScoped<ICashRegisterApiClient, CashRegisterApiClient>();
builder.Services.AddScoped<ISaleApiClient, SaleApiClient>();
builder.Services.AddScoped<IPaymentTerminalApiClient, PaymentTerminalApiClient>();
builder.Services.AddScoped<IQuoteApiClient, QuoteApiClient>();
builder.Services.AddScoped<ISalesOrderApiClient, SalesOrderApiClient>();
builder.Services.AddScoped<IFulfillmentApiClient, FulfillmentApiClient>();
builder.Services.AddScoped<IInvoiceApiClient, InvoiceApiClient>();
builder.Services.AddScoped<IPaymentApiClient, PaymentApiClient>();
// Local document output (specification task 24): the authenticated client fetches the snapshot, this hands it to
// the browser's own print dialogue. No navigation, no token in a URL, no temporary file.
builder.Services.AddScoped<IDocumentOutput, WebDocumentOutput>();
builder.Services.AddScoped<IPosCheckoutOrchestrator, WebPosCheckoutOrchestrator>();

builder.Services.AddBlazoredLocalStorage();
builder.Services.AddMudServices();
builder.Services.AddLocalization();

await builder.Build().RunAsync();
