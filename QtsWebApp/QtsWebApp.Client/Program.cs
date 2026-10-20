using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Banking;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Orders;
using QtsWebApp.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Every browser call goes through the authenticated server API (QB-15): no database driver, connection
// string or server secret is ever part of the WebAssembly bundle.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ICustomerApiClient, CustomerApiClient>();
builder.Services.AddScoped<IAccountApiClient, AccountApiClient>();
builder.Services.AddScoped<IProductApiClient, ProductApiClient>();
builder.Services.AddScoped<IInventoryApiClient, InventoryApiClient>();
builder.Services.AddScoped<IBankingApiClient, BankingApiClient>();
builder.Services.AddScoped<ICashRegisterApiClient, CashRegisterApiClient>();
builder.Services.AddScoped<ISaleApiClient, SaleApiClient>();
builder.Services.AddScoped<IPaymentTerminalApiClient, PaymentTerminalApiClient>();
builder.Services.AddScoped<IQuoteApiClient, QuoteApiClient>();
builder.Services.AddScoped<ISalesOrderApiClient, SalesOrderApiClient>();
builder.Services.AddScoped<IPosCheckoutOrchestrator, WebPosCheckoutOrchestrator>();

builder.Services.AddBlazoredLocalStorage();
builder.Services.AddMudServices();
builder.Services.AddLocalization();

await builder.Build().RunAsync();
