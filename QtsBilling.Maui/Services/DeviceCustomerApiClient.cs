using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Device customer client. The shared contract implementation is reused unchanged; the device only adds
/// the transport-failure translation so a shared component shows a readable "server unreachable" message
/// instead of an unhandled exception while the device is offline.
/// </summary>
public sealed class DeviceCustomerApiClient : ICustomerApiClient
{
    private readonly CustomerApiClient _inner;
    private readonly IDeviceApiLog _apiLog;

    public DeviceCustomerApiClient(HttpClient http, IDeviceApiLog apiLog)
    {
        _inner = new CustomerApiClient(http);
        _apiLog = apiLog;
    }

    public async Task<PagedResult<CustomerDto>> GetCustomersAsync(
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var page = await GuardAsync(() => _inner.GetCustomersAsync(search, cursor, pageSize, cancellationToken));
        await _apiLog.WriteAsync($"   customers items={page.Items.Count}", cancellationToken);
        return page;
    }

    public Task<CustomerDto> CreateCustomerAsync(
        SaveCustomerRequest request,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.CreateCustomerAsync(request, cancellationToken));

    public Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        SaveCustomerRequest request,
        long version,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.UpdateCustomerAsync(id, request, version, cancellationToken));

    public Task DeactivateCustomerAsync(Guid id, CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.DeactivateCustomerAsync(id, cancellationToken));

    private static async Task GuardAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ApiException(
                503,
                ApiErrorCodes.StoreUnavailable,
                "The server is unreachable.",
                new Dictionary<string, string[]>
                {
                    ["network"] = ["The device is offline or the server cannot be reached. Try again when the connection is restored."],
                });
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ApiException(
                503,
                ApiErrorCodes.StoreUnavailable,
                "The server is unreachable.",
                new Dictionary<string, string[]>
                {
                    ["network"] = ["The device is offline or the server cannot be reached. Try again when the connection is restored."],
                });
        }
    }
}
