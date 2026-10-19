using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Audit;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Audit;

/// <summary>
/// Structured audit feed (QB-14). Entries are append-only rows written inside the same transaction as the
/// action they describe (business documents) or alongside the action (authentication). Reading is
/// tenant-scoped: a member sees their business's entries plus their own account events, never another
/// business's or another user's authentication history.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit-events").WithTags("Audit");

        group.MapGet("/", ListAuditEventsAsync).RequireAuthorization(Permissions.AuditView);

        return app;
    }

    private static async Task<IResult> ListAuditEventsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? entityType,
        string? action,
        Guid? userId,
        DateOnly? from,
        DateOnly? to,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var callerUserId))
        {
            return BusinessScope.MissingMembership();
        }

        if (from is { } start && to is { } end && start > end)
        {
            return ApiResults.ValidationProblem(
                "The date range is invalid.",
                new Dictionary<string, string[]> { ["from"] = ["The start date must not be after the end date."] });
        }

        var size = Math.Clamp(pageSize ?? 50, 1, 200);

        var query = db.AuditEvents.AsNoTracking()
            .Where(audit => audit.BusinessId == businessId
                || (audit.BusinessId == null && audit.UserId == callerUserId));

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var value = entityType.Trim();
            query = query.Where(audit => audit.EntityType == value);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var value = action.Trim();
            query = query.Where(audit => audit.Action == value);
        }

        if (userId is { } filterUser)
        {
            query = query.Where(audit => audit.UserId == filterUser);
        }

        if (from is { } rangeFrom)
        {
            var fromUtc = new DateTimeOffset(rangeFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(audit => audit.OccurredAtUtc >= fromUtc);
        }

        if (to is { } rangeTo)
        {
            var toUtcExclusive = new DateTimeOffset(rangeTo.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(audit => audit.OccurredAtUtc < toUtcExclusive);
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(audit => audit.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(audit => audit.Id)
            .Take(size + 1)
            .Select(audit => new AuditEventDto(
                audit.Id,
                audit.BusinessId,
                audit.UserId,
                audit.DeviceId,
                audit.EntityType,
                audit.EntityId,
                audit.Action,
                audit.OccurredAtUtc,
                audit.CorrelationId,
                audit.Summary))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<AuditEventDto>(rows, nextCursor, size));
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
}
