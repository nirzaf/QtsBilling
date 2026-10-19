using QtsBilling.Maui.Services;
using QtsBilling.Persistence.Sqlite;

namespace QtsBilling.Maui;

public partial class MainPage : ContentPage
{
    private bool _started;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_started)
        {
            return;
        }

        _started = true;
        await StartAsync();
    }

    /// <summary>
    /// Opens the device-local store before the UI asks for any data, and records what the device loaded.
    /// The app must start with or without a server, so a failure here is reported, never fatal.
    /// </summary>
    private async Task StartAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        SqliteStartupResult? startup = null;
        Exception? failure = null;

        try
        {
            if (services is not null)
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<SqliteLocalStore>();
                startup = await store.InitializeAsync();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            if (services is not null)
            {
                var diagnostics = services.GetRequiredService<IStartupDiagnostics>();
                await diagnostics.WriteAsync(startup, failure);
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            System.Diagnostics.Debug.WriteLine($"QtsBilling.Maui startup issue: {failure}");
        }
    }
}
