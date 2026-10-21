using System.Text.Json;
using QtsBilling.Contracts.Serialization;

namespace QtsWebApp.Client.Services;

/// <summary>
/// Carries the browser's anti-forgery proof on every state-changing call. The token is fetched once per
/// client instance from the owned server and sent as a header, never in a query string, so a hostile page
/// cannot ride the session cookie (specification task 09).
/// </summary>
public sealed class AntiforgeryHandler : DelegatingHandler
{
    private string? _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!ApiHttp.IsUnsafe(request.Method))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = _token;
        if (token is null)
        {
            token = await FetchTokenAsync(request, cancellationToken).ConfigureAwait(false);
            _token = token;
        }

        ApiHttp.AttachAntiforgeryToken(request, token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A proof taken before signing in belongs to the anonymous session, and a session can expire between
        // pages. Either way the server refuses the mutation before running it, so replaying once with a fresh
        // proof cannot double-execute: the rejected attempt never reached the endpoint.
        if (await IsAntiforgeryRejectionAsync(response).ConfigureAwait(false))
        {
            response.Dispose();
            _token = null;

            var fresh = await FetchTokenAsync(request, cancellationToken).ConfigureAwait(false);
            _token = fresh;

            return await base.SendAsync(await BuildRetryAsync(request, fresh, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Rebuilds the same request against the same URL with the same body and headers. The rejected attempt was
    /// refused before it reached the endpoint, so replaying the identical payload is not a second execution.
    /// </summary>
    private static async Task<HttpRequestMessage> BuildRetryAsync(HttpRequestMessage failed, string? token, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(failed.Method, failed.RequestUri);

        foreach (var header in failed.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (failed.Content is not null)
        {
            // The first send buffered the body, so it can be handed to the replay unchanged - including its
            // Idempotency-Key and command headers, which is what makes the replay safe rather than a duplicate.
            clone.Content = new ByteArrayContent(await failed.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            clone.Content.Headers.ContentType = failed.Content.Headers.ContentType;
            clone.Content.Headers.ContentLength = failed.Content.Headers.ContentLength;
        }

        ApiHttp.AttachAntiforgeryToken(clone, token);
        return clone;
    }

    private static async Task<bool> IsAntiforgeryRejectionAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return body.Contains("anti-forgery", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FetchTokenAsync(HttpRequestMessage origin, CancellationToken cancellationToken)
    {
        // Same client, same origin, same cookies: only the host of the request being sent is known here, so the
        // token is fetched against that absolute URL and passes straight through (a GET needs no proof).
        var tokenUrl = new Uri(origin.RequestUri!.GetLeftPart(UriPartial.Authority) + ApiHttp.AntiforgeryTokenEndpoint);

        using var probe = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

        try
        {
            using var response = await base.SendAsync(probe, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("requestToken", out var token) ? token.GetString() : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
