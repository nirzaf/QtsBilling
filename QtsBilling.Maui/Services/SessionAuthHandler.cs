namespace QtsBilling.Maui.Services;

/// <summary>
/// Attaches the stored device session to every API call and captures the session cookie the server
/// issues. The cookie lives in the platform secure store, so a restart keeps the device signed in without
/// embedding any credential in the application package.
/// </summary>
public sealed class SessionAuthHandler : DelegatingHandler
{
    private readonly ISessionStore _sessionStore;
    private readonly IDeviceApiLog _apiLog;

    public SessionAuthHandler(ISessionStore sessionStore, IDeviceApiLog apiLog)
    {
        _sessionStore = sessionStore;
        _apiLog = apiLog;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var route = request.RequestUri is { } uri ? uri.PathAndQuery : "<unknown>";
        await _apiLog.WriteAsync($"-> {request.Method} {route}", cancellationToken).ConfigureAwait(false);

        var cookies = await _sessionStore.GetCookiesAsync(cancellationToken).ConfigureAwait(false);

        if (cookies.Count > 0 && !request.Headers.Contains("Cookie"))
        {
            request.Headers.Add(
                "Cookie",
                string.Join("; ", cookies.Select(entry => $"{entry.Key}={entry.Value}")));
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await _apiLog.WriteAsync(
                $"<- {request.Method} {route} status={(int)response.StatusCode} elapsedMs={started.ElapsedMilliseconds}",
                cancellationToken).ConfigureAwait(false);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    var pair = setCookie.Split(';', 2)[0].Trim();
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);

                    if (separator <= 0)
                    {
                        continue;
                    }

                    var name = pair[..separator].Trim();
                    var value = pair[(separator + 1)..].Trim();

                    if (value.Length == 0)
                    {
                        continue;
                    }

                    await _sessionStore.SaveCookieAsync(name, value, cancellationToken).ConfigureAwait(false);
                }
            }

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _apiLog.WriteAsync(
                $"!! {request.Method} {route} failed={exception.GetType().Name} elapsedMs={started.ElapsedMilliseconds}",
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
