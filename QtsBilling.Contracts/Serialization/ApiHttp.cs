using System.Net.Http.Json;
using System.Text.Json;
using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Contracts.Serialization;

/// <summary>
/// Shared response handling for every QtsBilling HTTP client: success passes through, failures raise an
/// <see cref="ApiException"/> carrying the server's stable error code so the UI can react to the cause.
/// </summary>
public static class ApiHttp
{
    /// <summary>
    /// Header carrying the anti-forgery proof of a cookie session (specification task 09). It travels only in a
    /// header, never in a query string, and a native bearer-only client neither fetches nor sends it.
    /// </summary>
    public const string AntiforgeryHeaderName = "X-QTS-CSRF";

    /// <summary>Same-origin GET that hands a cookie client its own proof; the response is never cacheable.</summary>
    public const string AntiforgeryTokenEndpoint = "/api/v1/auth/csrf-token";

    /// <summary>Verbs that can change server state, so a cookie client has to prove the request was its own.</summary>
    public static bool IsUnsafe(HttpMethod method)
        => !HttpMethod.Get.Equals(method) && !HttpMethod.Head.Equals(method) && !HttpMethod.Options.Equals(method);

    /// <summary>
    /// Attaches the session's proof to a mutating request. Safe methods are left untouched so a token never
    /// appears in a cacheable GET, and an existing value is never overwritten.
    /// </summary>
    public static void AttachAntiforgeryToken(HttpRequestMessage request, string? token)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(token) && IsUnsafe(request.Method) && !request.Headers.Contains(AntiforgeryHeaderName))
        {
            request.Headers.TryAddWithoutValidation(AntiforgeryHeaderName, token);
        }
    }

    public static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var code = status switch
        {
            400 => ApiErrorCodes.ValidationFailed,
            401 => ApiErrorCodes.Unauthenticated,
            403 => ApiErrorCodes.Forbidden,
            404 => ApiErrorCodes.NotFound,
            409 => ApiErrorCodes.Conflict,
            429 => ApiErrorCodes.RateLimited,
            _ => ApiErrorCodes.InternalError,
        };
        var message = response.ReasonPhrase ?? $"Request failed with status {status}.";
        Dictionary<string, string[]>? errors = null;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var problem = document.RootElement;

            if (problem.ValueKind == JsonValueKind.Object)
            {
                if (problem.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                {
                    code = codeElement.GetString() ?? code;
                }

                // "detail" is the most specific human-readable cause; fall back to "title".
                foreach (var member in new[] { "detail", "title" })
                {
                    if (problem.TryGetProperty(member, out var element) && element.ValueKind == JsonValueKind.String
                        && element.GetString() is { Length: > 0 } text)
                    {
                        message = text;
                        break;
                    }
                }

                if (problem.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
                {
                    errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in errorsElement.EnumerateObject())
                    {
                        errors[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                            ? property.Value.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                            : [property.Value.GetString() ?? string.Empty];
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (proxy, HTML error page); keep the status-derived defaults.
        }

        throw new ApiException(status, code, message, errors);
    }

    /// <summary>Reads a required JSON payload or fails with a structured error.</summary>
    public static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        var payload = await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new ApiException(500, ApiErrorCodes.InternalError, "The server returned an empty payload.");
    }
}
