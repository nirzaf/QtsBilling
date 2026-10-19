using System.Text.Json;
using QtsBilling.Contracts.Abstractions;

namespace QtsWebApp.Infrastructure;

/// <summary>
/// Typed failure boundary for the API (QB-14): unhandled exceptions on <c>/api</c> paths become a
/// ProblemDetails body with the stable <see cref="ApiErrorCodes.InternalError"/> code and the correlation
/// id - never an empty list, an HTML error page or a stack trace. Legacy failures that produced "empty
/// result" classes (and an error logger that wrote to the failing database, recursing) are structurally
/// impossible here: logging is best-effort, never writes to the database, and a failing sink can only be
/// swallowed - it can never change the response or recurse.
/// </summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (IsApiRequest(context))
        {
            await WriteFailureAsync(context, exception);
        }
    }

    private static bool IsApiRequest(HttpContext context)
        => context.Request.Path.StartsWithSegments("/api");

    private async Task WriteFailureAsync(HttpContext context, Exception exception)
    {
        var traceId = context.TraceIdentifier;

        try
        {
            logger.LogError(
                exception,
                "Unhandled API failure for {Method} {Path} (trace {TraceId}).",
                context.Request.Method,
                context.Request.Path.Value,
                traceId);
        }
        catch
        {
            // A logging sink failure must never replace the typed failure or recurse into another handler.
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        // Domain rules that escaped an endpoint keep their precise code/status; malformed requests keep
        // their 400; everything else is a typed internal error. Details are deliberately generic: no
        // exception message, no stack, no SQL.
        var (status, code, title, detail) = exception switch
        {
            DomainRuleException domain => (domain.StatusCode, domain.Code, domain.Title, domain.Message),
            BadHttpRequestException bad when bad.StatusCode is >= 400 and < 500 =>
                (bad.StatusCode,
                    ApiErrorCodes.ValidationFailed,
                    "The request could not be read.",
                    "The request body or one of its parameters is not valid for this route (for example malformed JSON or a bad date/identifier)."),
            _ => (StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "An unexpected error occurred.",
                "The request failed before it could produce a result. Retry the same request with the same Idempotency-Key where applicable; quote the correlation id if the failure persists."),
        };

        context.Response.StatusCode = status;

        var problem = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.com/{status}",
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["code"] = code,
            ["traceId"] = traceId,
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
