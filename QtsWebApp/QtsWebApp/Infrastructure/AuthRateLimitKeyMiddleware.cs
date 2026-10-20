using System.Text.Json;
using System.Threading.RateLimiting;

namespace QtsWebApp.Infrastructure;

/// <summary>Reads only the non-secret account identifier needed to partition authentication limits.</summary>
public sealed class AuthRateLimitKeyMiddleware(RequestDelegate next)
{
    public const string PartitionKeyItem = "qts:auth_rate_partition";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isAuthApi = path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase);
        var isToken = string.Equals(path, "/connect/token", StringComparison.OrdinalIgnoreCase);
        if (isAuthApi || isToken)
        {
            var identity = isAuthApi
                ? await ReadJsonIdentityAsync(context).ConfigureAwait(false)
                : await ReadFormIdentityAsync(context).ConfigureAwait(false);
            var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
            context.Items[PartitionKeyItem] = $"{ip}|{identity}";
        }

        await next(context).ConfigureAwait(false);
    }

    public static RateLimitPartition<string> CreatePartition(string partitionKey)
        => RateLimitPartition.GetTokenBucketLimiter(partitionKey, static _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

    private static async Task<string> ReadJsonIdentityAsync(HttpContext context)
    {
        if (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) != true
            || !context.Request.Body.CanRead)
        {
            return "";
        }

        context.Request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String)
            {
                return Normalize(email.GetString());
            }
        }
        catch (JsonException)
        {
            // The endpoint owns malformed-payload reporting; this middleware only partitions limits.
        }
        finally
        {
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }

        return "";
    }

    private static async Task<string> ReadFormIdentityAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return context.Request.Headers["X-Client-ID"].ToString().Trim().ToLowerInvariant();
        }

        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var client = form["client_id"].ToString().Trim().ToLowerInvariant();
            var user = Normalize(form["username"].ToString());
            return $"{client}|{user}";
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
