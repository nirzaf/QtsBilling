using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using QtsServices;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register the CustomerService class as a singleton service
builder.Services.AddSingleton<CustomerService>();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddMudServices();
builder.Services.AddLocalization();

await builder.Build().RunAsync();
