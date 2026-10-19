namespace QtsBilling.Maui.Services;

/// <summary>
/// Append-only device support log for API calls. It records the method, the route, the status and the
/// duration - never a credential, cookie value or payload body - so support can tell an unreachable
/// server from a rejected call without asking the user to reproduce the failure.
/// </summary>
public interface IDeviceApiLog
{
    Task WriteAsync(string entry, CancellationToken cancellationToken = default);
}

public sealed class DeviceApiLog : IDeviceApiLog
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LogPath => Path.Combine(FileSystem.AppDataDirectory, "api-calls.log");

    public async Task WriteAsync(string entry, CancellationToken cancellationToken = default)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {entry}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(LogPath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A support log must never break the application.
        }
        finally
        {
            _gate.Release();
        }
    }
}
