using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QtsBilling.Contracts.Sync;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Runs the device synchronization loop in the background (QB-22). The loop is quiet: it never blocks the
/// UI and never deletes anything on the device - a failed run simply leaves the queue where it is.
/// </summary>
public sealed class DeviceSyncTimer : IHostedService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private Timer? _timer;

    public DeviceSyncTimer(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = RunAsync(), null, InitialDelay, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<ISyncCoordinator>();

            if (!coordinator.IsSupported)
            {
                return;
            }

            await coordinator.RunAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The background loop never surfaces an error to the user: the queue keeps the work.
        }
    }
}
