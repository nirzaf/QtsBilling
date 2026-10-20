using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Inventory;

/// <summary>Blind cycle counts and approval through the authoritative inventory and general ledgers.</summary>
internal static class StockCountEndpoints
{
    private const int MaxLinesPerUpdate = 10_000;
    private const int QuantityScale = 6;
    private const int MaxNoteLength = 2_000;
    private const string IdempotencyHeader = "Idempotency-Key";

    public static void MapStockCountEndpoints(RouteGroupBuilder inventory)
    {
        var counts = inventory.MapGroup("/stock-counts").WithTags("Inventory");
        counts.MapGet("/", ListAsync).RequireAuthorization(Permissions.ProductsRead);
        counts.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.ProductsRead);
        counts.MapPost("/", CreateAsync).RequireAuthorization(Permissions.InventoryAdjust);
        counts.MapPut("/{id:guid}/lines", RecordLinesAsync).RequireAuthorization(Permissions.InventoryAdjust);
        counts.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(Permissions.InventoryAdjust)
            .RequireAuthorization(Permissions.AccountingPost);
        counts.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(Permissions.InventoryAdjust);
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, BillingDbContext db, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var allLocations = await db.BusinessMemberships.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == userId && row.IsActive)
            .Select(row => (bool?)row.AllLocations)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (allLocations is null) return BusinessScope.MissingMembership();
        List<Guid> allowedLocations = allLocations.Value
            ? []
            : await (
                from grant in db.BusinessMembershipLocations.AsNoTracking()
                join membership in db.BusinessMemberships.AsNoTracking()
                    on new { grant.BusinessId, grant.MembershipId } equals new { membership.BusinessId, MembershipId = membership.Id }
                join location in db.BusinessLocations.AsNoTracking()
                    on new { grant.BusinessId, grant.LocationId } equals new { location.BusinessId, LocationId = location.Id }
                where membership.BusinessId == businessId && membership.UserId == userId && membership.IsActive && location.IsActive
                select grant.LocationId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var query =
            from session in db.StockCountSessions.AsNoTracking()
            join location in db.BusinessLocations.AsNoTracking() on new { session.LocationId, session.BusinessId } equals new { LocationId = location.Id, location.BusinessId }
            where session.BusinessId == businessId
            select new
            {
                session.Id,
                session.CountNumber,
                session.Status,
                session.LocationId,
                LocationName = location.Name,
                session.SnapshotAtUtc,
                session.SubmittedAtUtc,
                session.ApprovedAtUtc,
                LineCount = db.StockCountLines.Count(line => line.BusinessId == businessId && line.StockCountSessionId == session.Id),
                CountedLineCount = db.StockCountLines.Count(line => line.BusinessId == businessId && line.StockCountSessionId == session.Id && line.CountedQuantity != null),
            };
        if (!allLocations.Value) query = query.Where(row => allowedLocations.Contains(row.LocationId));
        var rows = await query.OrderByDescending(row => row.Id).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows.Select(row => new StockCountSummaryDto(row.Id, row.CountNumber,
            EnumBridge.To<StockCountState>(row.Status), row.LocationId, row.LocationName, row.SnapshotAtUtc,
            row.SubmittedAtUtc, row.ApprovedAtUtc, row.LineCount, row.CountedLineCount)).ToList());
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal principal, BillingDbContext db, Guid id, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
        if (dto is null) return NotFound();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, dto.LocationId, cancellationToken).ConfigureAwait(false))
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this stock-count location.");
        return Results.Ok(dto);
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateStockCountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length is 0 or > 64)
            return ApiResults.ValidationProblem("An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters."] });
        var errors = new Dictionary<string, string[]>();
        if (request.LocationId == Guid.Empty) errors["locationId"] = ["A location is required."];
        if (request.Note is { Length: > MaxNoteLength }) errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        if (errors.Count > 0) return ApiResults.ValidationProblem("The stock-count payload is invalid.", errors);
        var normalized = request with { Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim() };
        var fingerprint = RequestFingerprint.Compute(normalized);

        var existing = await db.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return await ReplayOrConflictAsync(db, businessId, existing, fingerprint, cancellationToken).ConfigureAwait(false);

        var locationExists = await db.BusinessLocations.AsNoTracking()
            .AnyAsync(row => row.BusinessId == businessId && row.Id == normalized.LocationId && row.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (!locationExists)
            return ApiResults.ValidationProblem("The stock-count payload is invalid.",
                new Dictionary<string, string[]> { ["locationId"] = ["The location must be active and belong to this business."] });
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, normalized.LocationId, cancellationToken).ConfigureAwait(false))
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this stock-count location.");

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var creation = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                var duplicate = await db.StockCountSessions.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                {
                    if (!string.Equals(duplicate.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    return (Id: duplicate.Id, Replayed: true);
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var number = await LedgerWriter.AllocateNumberAsync(db, businessId, "stock-count", "SC-",
                    LedgerWriter.LocalDate(null, now).Year, cancellationToken).ConfigureAwait(false);
                var session = new StockCountSession
                {
                    BusinessId = businessId,
                    LocationId = normalized.LocationId,
                    CountNumber = number,
                    Status = StockCountStatus.Open,
                    SnapshotAtUtc = now,
                    Note = normalized.Note,
                    IdempotencyKey = key,
                    PayloadHash = fingerprint,
                };
                db.StockCountSessions.Add(session);
                var snapshot = await (
                    from variant in db.ProductVariants.AsNoTracking()
                    join product in db.Products.AsNoTracking() on new { variant.ProductId, variant.BusinessId } equals new { ProductId = product.Id, product.BusinessId }
                    join balance in db.InventoryBalances.AsNoTracking().Where(row => row.BusinessId == businessId && row.LocationId == normalized.LocationId)
                        on new { variant.Id, variant.BusinessId } equals new { Id = balance.VariantId, balance.BusinessId } into balances
                    from balance in balances.DefaultIfEmpty()
                    where variant.BusinessId == businessId && variant.IsActive && product.TrackStock
                    orderby variant.Id
                    select new { variant.Id, ExpectedQuantity = balance == null ? 0m : balance.OnHand,
                        ExpectedUnitCost = balance == null ? 0m : balance.AverageUnitCost })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.Count == 0)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "There are no active stock-tracked variants to count.", "Activate a stock-tracked product before opening a count session.");
                foreach (var row in snapshot)
                {
                    db.StockCountLines.Add(new StockCountLine
                    {
                        BusinessId = businessId,
                        StockCountSessionId = session.Id,
                        VariantId = row.Id,
                        ExpectedQuantity = row.ExpectedQuantity,
                        ExpectedUnitCost = row.ExpectedUnitCost,
                    });
                }
                db.AuditEvents.Add(AuditEventFor(businessId, userId, session.Id, "created", now, http.TraceIdentifier,
                    new { session.CountNumber, session.LocationId, session.SnapshotAtUtc, lineCount = snapshot.Count }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (Id: session.Id, Replayed: false);
            }).ConfigureAwait(false);
            var dto = await ToDetailAsync(db, businessId, creation.Id, cancellationToken).ConfigureAwait(false);
            return creation.Replayed ? Results.Ok(dto) : Results.Created($"/api/v1/inventory/stock-counts/{creation.Id:D}", dto);
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName == "ux_stock_count_sessions_business_idempotency_key")
        {
            db.ChangeTracker.Clear();
            var winner = await db.StockCountSessions.AsNoTracking()
                .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                .ConfigureAwait(false);
            return winner is null
                ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.IdempotencyKeyReused, "A concurrent request with this key committed first.")
                : await ReplayOrConflictAsync(db, businessId, winner, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> RecordLinesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        RecordStockCountLinesRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var errors = ValidateCountLines(request);
        if (errors.Count > 0) return ApiResults.ValidationProblem("The physical count payload is invalid.", errors);

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var session = await db.StockCountSessions
                    .FromSql($"SELECT * FROM stock_count_sessions WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (session is null) throw NotFoundException();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, session.LocationId, cancellationToken).ConfigureAwait(false))
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The membership cannot access this stock-count location.", "Ask a business owner to grant access to the location.");
                var lines = await db.StockCountLines.Where(line => line.BusinessId == businessId && line.StockCountSessionId == id)
                    .OrderBy(line => line.VariantId).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (session.Status == StockCountStatus.Submitted)
                {
                    if (!MatchesSubmitted(lines, request.Lines))
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "This count has already been submitted.", "Submitted physical quantities are locked and cannot be changed.");
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (session.Status != StockCountStatus.Open)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This count is no longer open.", "Only an open count accepts physical quantities.");

                var requested = request.Lines.ToDictionary(line => line.VariantId, line => line.CountedQuantity);
                var byVariant = lines.ToDictionary(line => line.VariantId);
                foreach (var item in requested)
                {
                    if (!byVariant.TryGetValue(item.Key, out var line))
                        throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                            "A variant is not part of this count session.", "Use only the frozen variant list returned when the session was opened.");
                    line.CountedQuantity = item.Value;
                }

                var allCounted = lines.All(line => line.CountedQuantity is not null);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (allCounted)
                {
                    session.Status = StockCountStatus.Submitted;
                    session.SubmittedAtUtc = DateTimeOffset.UtcNow;
                    db.AuditEvents.Add(AuditEventFor(businessId, userId, session.Id, "submitted", session.SubmittedAtUtc.Value,
                        http.TraceIdentifier, new { session.CountNumber, countedLineCount = lines.Count }));
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFound() : Results.Ok(dto);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    private static async Task<IResult> ApproveAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var session = await db.StockCountSessions
                    .FromSql($"SELECT * FROM stock_count_sessions WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (session is null) throw NotFoundException();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, session.LocationId, cancellationToken).ConfigureAwait(false))
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The membership cannot access this stock-count location.", "Ask a business owner to grant access to the location.");
                if (session.Status == StockCountStatus.Approved)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (session.Status != StockCountStatus.Submitted)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Only a submitted count can be approved.", "Record physical quantities for every line before approval.");

                var lines = await db.StockCountLines.Where(line => line.BusinessId == businessId && line.StockCountSessionId == id)
                    .OrderBy(line => line.VariantId).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (lines.Count == 0 || lines.Any(line => line.CountedQuantity is null))
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The stock count is incomplete.", "Every frozen line must have a physical quantity before approval.");
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, ["1200", "5200"], cancellationToken).ConfigureAwait(false);
                if (!accounts.ContainsKey("1200") || !accounts.ContainsKey("5200"))
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Inventory adjustment accounts are not configured.", "Configure account 1200 Inventory and account 5200 Inventory Shrinkage before approval.");

                var now = DateTimeOffset.UtcNow;
                decimal decreaseValue = 0m;
                decimal increaseValue = 0m;
                foreach (var line in lines)
                {
                    var balance = await InventoryLedger.LockBalanceAsync(db, businessId, session.LocationId, line.VariantId, now, cancellationToken).ConfigureAwait(false);
                    var delta = line.CountedQuantity!.Value - balance.OnHand;
                    line.VarianceQuantity = delta;
                    if (delta == 0m)
                    {
                        line.VarianceValue = 0m;
                        continue;
                    }

                    var unitCost = delta > 0m
                        ? balance.AverageUnitCost > 0m ? balance.AverageUnitCost : line.ExpectedUnitCost
                        : 0m;
                    if (delta > 0m && unitCost <= 0m)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.ValuationRequired,
                            "The added stock has no approved cost basis.", "Receive or value one unit of this variant before approving a positive stock-count variance.");
                    var movement = await InventoryLedger.ApplyAsync(db, businessId, userId, session.LocationId, line.VariantId,
                        MovementType.StockAdjustment, delta, unitCost, "stock_count_line", line.Id,
                        $"Count {session.CountNumber} adjustment", now, cancellationToken).ConfigureAwait(false);
                    if (delta > 0m && movement.ValueDelta <= 0m)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.ValuationRequired,
                            "The added stock has no representable value.", "Increase the unit cost or count a larger quantity so the adjustment can be valued to four decimal places.");
                    line.VarianceValue = movement.ValueDelta;
                    if (delta > 0m)
                    {
                        movement.UnitCost = Math.Round(movement.ValueDelta / delta, 8, MidpointRounding.AwayFromZero);
                        increaseValue += movement.ValueDelta;
                    }
                    else
                    {
                        movement.UnitCost = Math.Round(-movement.ValueDelta / -delta, 8, MidpointRounding.AwayFromZero);
                        decreaseValue += -movement.ValueDelta;
                    }
                }

                Guid? journalId = null;
                var journalLines = new List<JournalLineRequest>();
                if (decreaseValue > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5200"].Id, decreaseValue, 0m, "Inventory shrinkage", session.LocationId));
                    journalLines.Add(new JournalLineRequest(accounts["1200"].Id, 0m, decreaseValue, "Stock-count decrease", session.LocationId));
                }
                if (increaseValue > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["1200"].Id, increaseValue, 0m, "Stock-count increase", session.LocationId));
                    journalLines.Add(new JournalLineRequest(accounts["5200"].Id, 0m, increaseValue, "Inventory adjustment", session.LocationId));
                }
                if (journalLines.Count > 0)
                {
                    var business = await db.Businesses.AsNoTracking().SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
                    var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, business.BaseCurrency,
                        LedgerWriter.LocalDate(business.TimeZone, now), $"Stock count {session.CountNumber}",
                        JournalSourceType.StockCount, session.Id, null, journalLines, cancellationToken).ConfigureAwait(false);
                    journalId = journal.Id;
                }

                // Persist line variances while the session is still Submitted; the database guard only
                // accepts this one-time valuation before the aggregate advances to Approved.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                session.Status = StockCountStatus.Approved;
                session.ApprovedAtUtc = now;
                session.ApprovedByUserId = userId;
                session.JournalEntryId = journalId;
                db.AuditEvents.Add(AuditEventFor(businessId, userId, session.Id, "approved", now, http.TraceIdentifier,
                    new { session.CountNumber, decreaseValue, increaseValue, journalEntryId = journalId }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFound() : Results.Ok(dto);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
        catch (DbUpdateConcurrencyException)
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure,
                "The stock count changed while it was being approved.", "Refresh the count before retrying.");
        }
    }

    private static async Task<IResult> CancelAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var session = await db.StockCountSessions
                .FromSql($"SELECT * FROM stock_count_sessions WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (session is null) return NotFound();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, session.LocationId, cancellationToken).ConfigureAwait(false))
                return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this stock-count location.");
            if (session.Status == StockCountStatus.Cancelled)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var replay = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                return replay is null ? NotFound() : Results.Ok(replay);
            }
            if (session.Status is StockCountStatus.Approved)
                return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "An approved count cannot be cancelled.");
            session.Status = StockCountStatus.Cancelled;
            db.AuditEvents.Add(AuditEventFor(businessId, userId, session.Id, "cancelled", DateTimeOffset.UtcNow, http.TraceIdentifier,
                new { session.CountNumber }));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFound() : Results.Ok(dto);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    private static async Task<StockCountDetailDto?> ToDetailAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        var header = await (
            from session in db.StockCountSessions.AsNoTracking()
            join location in db.BusinessLocations.AsNoTracking() on new { session.LocationId, session.BusinessId } equals new { LocationId = location.Id, location.BusinessId }
            where session.BusinessId == businessId && session.Id == id
            select new { session.Id, session.CountNumber, session.Status, session.LocationId, LocationName = location.Name,
                session.SnapshotAtUtc, session.SubmittedAtUtc, session.ApprovedAtUtc, session.JournalEntryId, session.Note })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return null;
        var revealExpected = header.Status is StockCountStatus.Approved or StockCountStatus.Cancelled;
        var lines = await (
            from line in db.StockCountLines.AsNoTracking()
            join variant in db.ProductVariants.AsNoTracking() on new { line.VariantId, line.BusinessId } equals new { VariantId = variant.Id, variant.BusinessId }
            where line.BusinessId == businessId && line.StockCountSessionId == id
            orderby variant.Name
            select new StockCountLineDto(line.Id, line.VariantId, variant.Name, variant.Sku,
                revealExpected ? line.ExpectedQuantity : null,
                revealExpected ? line.ExpectedUnitCost : null,
                line.CountedQuantity,
                revealExpected ? line.VarianceQuantity : null,
                revealExpected ? line.VarianceValue : null))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new StockCountDetailDto(header.Id, header.CountNumber, EnumBridge.To<StockCountState>(header.Status), header.LocationId,
            header.LocationName, header.SnapshotAtUtc, header.SubmittedAtUtc, header.ApprovedAtUtc, header.JournalEntryId, header.Note, lines);
    }

    private static Dictionary<string, string[]> ValidateCountLines(RecordStockCountLinesRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request?.Lines is null || request.Lines.Count is 0 or > MaxLinesPerUpdate)
            errors["lines"] = [$"Provide 1-{MaxLinesPerUpdate} physical quantities."];
        else
        {
            for (var index = 0; index < request.Lines.Count; index++)
            {
                var line = request.Lines[index];
                if (line.VariantId == Guid.Empty || line.CountedQuantity < 0m || decimal.Round(line.CountedQuantity, QuantityScale) != line.CountedQuantity)
                    errors[$"lines[{index}]"] = ["Each line requires a variant and a non-negative quantity with at most 6 decimal places."];
            }
            if (request.Lines.Select(line => line.VariantId).Distinct().Count() != request.Lines.Count)
                errors["lines"] = ["Each variant may appear once in a count update."];
        }
        return errors;
    }

    private static bool MatchesSubmitted(IReadOnlyCollection<StockCountLine> existing, IReadOnlyCollection<StockCountLineRequest> requested)
    {
        var byVariant = existing.ToDictionary(line => line.VariantId);
        return requested.Count > 0 && requested.All(line => byVariant.TryGetValue(line.VariantId, out var current)
            && current.CountedQuantity == line.CountedQuantity);
    }

    private static async Task<IResult> ReplayOrConflictAsync(
        BillingDbContext db, Guid businessId, StockCountSession existing, string fingerprint, CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.PayloadHash, fingerprint, StringComparison.Ordinal)) return SameKeyDifferentPayload().ToResult();
        var dto = await ToDetailAsync(db, businessId, existing.Id, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Results.Ok(dto);
    }

    private static DomainRuleException SameKeyDifferentPayload()
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload,
            "This idempotency key was used for a different count payload.", "Retry with the original location or start a count with a new key.");

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFound()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The stock count does not exist in this business.");

    private static DomainRuleException NotFoundException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The stock count does not exist in this business.",
            "Check the identifier: stock counts are scoped to the signed-in business.");

    private static AuditEvent AuditEventFor(Guid businessId, Guid userId, Guid sessionId, string action, DateTimeOffset now, string correlationId, object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = "stock_count_session",
            EntityId = sessionId,
            Action = action,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        };

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The stock-count write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The stock-count write violates an inventory integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                "The stock count references a record outside this business."),
            _ => ApiResults.Problem(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError,
                "The stock-count operation failed."),
        };
}
