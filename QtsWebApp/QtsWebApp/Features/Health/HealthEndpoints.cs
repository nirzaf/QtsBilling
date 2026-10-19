using QtsBilling.Contracts.Abstractions;
using QtsBilling.Persistence.Postgres;

namespace QtsWebApp.Features.Health;

/// <summary>
/// Readiness endpoint (QB-05). Reports whether the authoritative PostgreSQL store is reachable; it never
/// leaks connection details and stays anonymous so probes work before sign-in.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", async (BillingDbContext db, CancellationToken cancellationToken) =>
        {
            var reachable = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);

            return reachable
                ? Results.Ok(new HealthDto("ready", "postgresql", DateTimeOffset.UtcNow))
                : Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The authoritative PostgreSQL store is unreachable.",
                    extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.StoreUnavailable });
        })
        .AllowAnonymous()
        .WithTags("Health")
        .WithName("Health");

        return app;
    }
}
