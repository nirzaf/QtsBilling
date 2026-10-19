using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using QtsBilling.Contracts.Clients;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Every browser call goes through the authenticated server API (QB-15): no database driver, connection
// string or server secret is ever part of the WebAssembly bundle.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ICustomerApiClient, CustomerApiClient>();
builder.Services.AddScoped<IAccountApiClient, AccountApiClient>();

builder.Services.AddBlazoredLocalStorage();
builder.Services.AddMudServices();
builder.Services.AddLocalization();

await builder.Build().RunAsync();
