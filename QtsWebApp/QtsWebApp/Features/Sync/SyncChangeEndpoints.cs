using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// The master-data change feed (QB-21). The feed is commit ordered: the per-business revision is allocated
/// by a row-locked increment inside the transaction of the business write, so a device that walks the
/// (revision, ordinal) cursor sees every committed change exactly once and in the order it was committed.
/// The feed is tenant scoped and permission scoped: a caller without <c>customers.read</c> never sees the
/// customer projections.
/// </summary>
public static class SyncChangeEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapSyncChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sync").WithTags("Sync");

        group.MapGet("/changes", GetChangesAsync).RequireAuthorization(Permissions.ProductsRead);

        return app;
    }

    private static async Task<IResult> GetChangesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        long? afterRevision,
        int? afterOrdinal,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var revision = afterRevision ?? 0;
        var ordinal = afterOrdinal ?? 0;

        if (revision < 0 || ordinal < 0)
        {
            return ApiResults.ValidationProblem(
                "The change cursor is invalid.",
                new Dictionary<string, string[]>
                {
                    ["afterRevision"] = ["The cursor cannot be negative."],
                    ["afterOrdinal"] = ["The cursor cannot be negative."],
                });
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var canReadCustomers = principal.HasPermission(Permissions.CustomersRead);

        var current = await db.SyncRevisions.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId)
            .Select(candidate => (long?)candidate.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        // A cursor that points past the server means the device and the server disagree about the history:
        // the device must re-pull the baseline instead of silently missing changes.
        if (revision > current)
        {
            return Results.Ok(new SyncChangesPage(
                [],
                current,
                0,
                false,
                SyncChangesPage.CurrentSchemaVersion,
                ResyncRequired: true));
        }

        var query = db.SyncChanges.AsNoTracking()
            .Where(change => change.BusinessId == businessId
                && (change.Revision > revision
                    || (change.Revision == revision && change.Ordinal > ordinal)));

        if (!canReadCustomers)
        {
            query = query.Where(change => change.EntityType != "customer");
        }

        var rows = await query
            .OrderBy(change => change.Revision)
            .ThenBy(change => change.Ordinal)
            .Take(size + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > size;

        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var nextRevision = revision;
        var nextOrdinal = ordinal;

        if (rows.Count > 0)
        {
            nextRevision = rows[^1].Revision;
            nextOrdinal = rows[^1].Ordinal;
        }

        var changes = rows
            .Select(change => new SyncChangeDto(
                change.Revision,
                change.Ordinal,
                change.EntityType,
                change.EntityId,
                change.Kind == QtsBilling.Persistence.Postgres.SyncChangeKind.Tombstone
                    ? QtsBilling.Contracts.Sync.SyncChangeKind.Tombstone
                    : QtsBilling.Contracts.Sync.SyncChangeKind.Upsert,
                Parse(change.PayloadJson),
                change.ChangedAtUtc))
            .ToList();

        return Results.Ok(new SyncChangesPage(
            changes,
            nextRevision,
            nextOrdinal,
            hasMore,
            SyncChangesPage.CurrentSchemaVersion,
            ResyncRequired: false));
    }

    private static JsonElement? Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
