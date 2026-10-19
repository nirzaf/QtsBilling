using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Sync;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Runs the device synchronization loop in the background (QB-22). The loop is quiet: it never blocks the
/// UI and never deletes anything on the device - a failed run simply leaves the queue where it is.
/// MAUI does not start hosted services on its own, so the window starts the loop explicitly.
/// </summary>
public sealed class DeviceSyncTimer
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private int _started;
    private Timer? _timer;

    public DeviceSyncTimer(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Starts the loop once; calling it again is a no-op.</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _timer = new Timer(_ => _ = RunAsync(), null, InitialDelay, Interval);
    }

    /// <summary>Stops the loop (used when the application shuts down).</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
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
