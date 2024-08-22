using MudBlazor.Services;
using QtsServices;
using QtsWebApp.Components;
using static MudBlazor.Defaults.Classes.Position;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Register the CustomerService class as a singleton service
builder.Services.AddSingleton<CustomerService>();
builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = TopCenter;
    options.SnackbarConfiguration.PreventDuplicates = true;
    options.SnackbarConfiguration.NewestOnTop = true;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(QtsWebApp.Client._Imports).Assembly);

app.Run();
