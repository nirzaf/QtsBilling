using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// Cash register sessions and drawer movements (QB-10). The drawer balance is always derived from the
/// append-only movements — opening float plus cash sale payments plus paid-in minus paid-out minus refunds —
/// and closing stores the counted amount next to the derived expected amount and the variance. Nothing is
/// silently overwritten; every movement carries its actor and reason (spec §3.6 cash controls).
/// </summary>
public static class CashRegisterEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxReasonLength = 500;
    private const int MoneyScale = 4;

    public static IEndpointRouteBuilder MapCashRegisterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pos/registers").WithTags("POS");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions", OpenAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapGet("/{id:guid}/sessions/current", GetCurrentAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}/sessions", ListSessionsAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapGet("/{id:guid}/sessions/{sessionId:guid}", GetSessionAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions/{sessionId:guid}/movements", AddMovementAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions/{sessionId:guid}/close", CloseAsync).RequireAuthorization(Permissions.RegistersManage);

        return app;
    }

    // ---------------------------------------------------------------- registers

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.CashRegisters.AsNoTracking().Where(register => register.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(register => register.IsActive);
        }

        var registers = await query
            .OrderBy(register => register.Name)
            .Select(register => new RegisterRow(register.Id, register.LocationId, register.Name, register.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var registerIds = registers.Select(register => register.Id).ToArray();

        var openSessions = await db.CashRegisterSessions.AsNoTracking()
            .Where(session => session.BusinessId == businessId
                && registerIds.Contains(session.CashRegisterId)
                && session.Status == CashRegisterSessionStatus.Open)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byRegister = openSessions.ToDictionary(session => session.CashRegisterId);

        var items = new List<CashRegisterDto>(registers.Count);

        foreach (var register in registers)
        {
            CashRegisterSessionDto? current = null;

            if (byRegister.TryGetValue(register.Id, out var session))
            {
                var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);
                current = ToDto(session) with { ExpectedCash = computed.Expected };
            }

            items.Add(new CashRegisterDto(register.Id, register.LocationId, register.Name, register.IsActive, current));
        }

        return Results.Ok(items);
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateCashRegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            return ApiResults.ValidationProblem(
                "The register payload is invalid.",
                new Dictionary<string, string[]> { ["name"] = [$"A register name of 1-{MaxNameLength} characters is required."] });
        }

        var locationExists = await db.BusinessLocations.AsNoTracking()
            .AnyAsync(location => location.BusinessId == businessId && location.Id == request.LocationId && location.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (!locationExists)
        {
            return ApiResults.ValidationProblem(
                "The register payload is invalid.",
                new Dictionary<string, string[]> { ["locationId"] = ["The location does not exist or is inactive in this business."] });
        }

        var register = new CashRegister
        {
            BusinessId = businessId,
            LocationId = request.LocationId,
            Name = name,
            IsActive = true,
        };

        db.CashRegisters.Add(register);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "A register with this name already exists at the location.");
        }

        return Results.Created(
            $"/api/v1/pos/registers/{register.Id}",
            new CashRegisterDto(register.Id, register.LocationId, register.Name, register.IsActive, null));
    }

    // ---------------------------------------------------------------- sessions

    private static async Task<IResult> OpenAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        OpenCashRegisterSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var openingFloat = LedgerWriter.Round(request.OpeningFloat);

        if (request.OpeningFloat < 0 || openingFloat != request.OpeningFloat)
        {
            return ApiResults.ValidationProblem(
                "The session payload is invalid.",
                new Dictionary<string, string[]> { ["openingFloat"] = [$"An opening float of at most {MoneyScale} decimal places and not negative is required."] });
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null || !register.IsActive)
        {
            return NotFoundRegister();
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var session = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var alreadyOpen = await db.CashRegisterSessions.AsNoTracking()
                    .AnyAsync(candidate => candidate.BusinessId == businessId
                        && candidate.CashRegisterId == id
                        && candidate.Status == CashRegisterSessionStatus.Open,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (alreadyOpen)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This register already has an open drawer session.",
                        "Close the current session before opening a new one.");
                }

                var session = new CashRegisterSession
                {
                    BusinessId = businessId,
                    CashRegisterId = id,
                    Currency = business.BaseCurrency,
                    Status = CashRegisterSessionStatus.Open,
                    OpeningFloat = openingFloat,
                    OpenedByUserId = userId,
                    OpenedAtUtc = now,
                };

                db.CashRegisterSessions.Add(session);

                if (openingFloat > 0)
                {
                    db.CashRegisterMovements.Add(new CashRegisterMovement
                    {
                        BusinessId = businessId,
                        CashRegisterId = id,
                        CashRegisterSessionId = session.Id,
                        MovementType = CashMovementType.Opening,
                        Amount = openingFloat,
                        Currency = business.BaseCurrency,
                        Reason = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                        CreatedByUserId = userId,
                        OccurredAtUtc = now,
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "cash_register_session",
                    EntityId = session.Id,
                    Action = "opened",
                    OccurredAtUtc = now,
                    CorrelationId = null,
                    Summary = $"{{\"registerId\":\"{id}\",\"openingFloat\":\"{openingFloat.ToString(CultureInfo.InvariantCulture)}\"}}",
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return session;
            }).ConfigureAwait(false);

            var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/pos/registers/{id}/sessions/{session.Id}",
                await BuildDetailAsync(db, businessId, register, session, computed, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> GetCurrentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null)
        {
            return NotFoundRegister();
        }

        var session = await db.CashRegisterSessions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId
                && candidate.CashRegisterId == id
                && candidate.Status == CashRegisterSessionStatus.Open,
                cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.CashRegisterNotOpen,
                "This register has no open drawer session.");
        }

        var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

        return Results.Ok(await BuildDetailAsync(db, businessId, register, session, computed, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ListSessionsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.CashRegisterSessions.AsNoTracking()
            .Where(session => session.BusinessId == businessId && session.CashRegisterId == id);

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(session => session.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(session => session.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<CashRegisterSessionDto>(rows.Select(ToDto).ToList(), nextCursor, size));
    }

    private static async Task<IResult> GetSessionAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null)
        {
            return NotFoundRegister();
        }

        var session = await db.CashRegisterSessions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId
                && candidate.CashRegisterId == id
                && candidate.Id == sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return NotFoundSession();
        }

        var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

        return Results.Ok(await BuildDetailAsync(db, businessId, register, session, computed, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> AddMovementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        Guid sessionId,
        CashMovementRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!Enum.TryParse<CashMovementType>(request.MovementType?.Trim(), ignoreCase: true, out var movementType)
            || movementType is not (CashMovementType.CashIn or CashMovementType.CashOut))
        {
            return ApiResults.ValidationProblem(
                "The movement payload is invalid.",
                new Dictionary<string, string[]> { ["movementType"] = ["Only 'cashIn' (paid in) and 'cashOut' (paid out) are accepted here; sales and refunds record their own movements."] });
        }

        var amount = LedgerWriter.Round(request.Amount);

        if (request.Amount <= 0 || amount != request.Amount)
        {
            return ApiResults.ValidationProblem(
                "The movement payload is invalid.",
                new Dictionary<string, string[]> { ["amount"] = [$"A positive amount of at most {MoneyScale} decimal places is required."] });
        }

        var reason = request.Reason?.Trim();

        if (reason is null or { Length: 0 } || reason.Length > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The movement payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A reason of 1-{MaxReasonLength} characters is required for drawer movements."] });
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null)
        {
            return NotFoundRegister();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var movement = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var session = await LoadSessionForUpdateAsync(db, businessId, id, sessionId, cancellationToken).ConfigureAwait(false);

                if (session is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The drawer session does not exist.",
                        "The session id may belong to another register or business.");
                }

                if (session.Status != CashRegisterSessionStatus.Open)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.CashRegisterNotOpen,
                        "The drawer session is closed.",
                        "Open a new session before recording drawer movements.");
                }

                var movement = new CashRegisterMovement
                {
                    BusinessId = businessId,
                    CashRegisterId = id,
                    CashRegisterSessionId = session.Id,
                    MovementType = movementType,
                    Amount = amount,
                    Currency = session.Currency,
                    Reason = reason,
                    CreatedByUserId = userId,
                    OccurredAtUtc = now,
                };

                db.CashRegisterMovements.Add(movement);

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "cash_register_movement",
                    EntityId = movement.Id,
                    Action = movementType == CashMovementType.CashIn ? "paid_in" : "paid_out",
                    OccurredAtUtc = now,
                    CorrelationId = null,
                    Summary = $"{{\"registerId\":\"{id}\",\"sessionId\":\"{session.Id}\",\"amount\":\"{amount.ToString(CultureInfo.InvariantCulture)}\"}}",
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return movement;
            }).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/pos/registers/{id}/sessions/{sessionId}",
                new CashMovementDto(
                    movement.Id,
                    EnumBridge.To<CashMovementKind>(movement.MovementType),
                    movement.Amount,
                    movement.Currency,
                    movement.Reason,
                    movement.SaleId,
                    movement.CreatedByUserId,
                    movement.OccurredAtUtc));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> CloseAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        Guid sessionId,
        CloseCashRegisterSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var counted = LedgerWriter.Round(request.CountedCash);

        if (request.CountedCash < 0 || counted != request.CountedCash)
        {
            return ApiResults.ValidationProblem(
                "The close payload is invalid.",
                new Dictionary<string, string[]> { ["countedCash"] = [$"A counted amount of at most {MoneyScale} decimal places and not negative is required."] });
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null)
        {
            return NotFoundRegister();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var session = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var session = await LoadSessionForUpdateAsync(db, businessId, id, sessionId, cancellationToken).ConfigureAwait(false);

                if (session is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The drawer session does not exist.",
                        "The session id may belong to another register or business.");
                }

                if (session.Status != CashRegisterSessionStatus.Open)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.CashRegisterNotOpen,
                        "This drawer session is already closed.",
                        "Open a new session to continue taking cash.");
                }

                var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

                session.Status = CashRegisterSessionStatus.Closed;
                session.ClosedByUserId = userId;
                session.ClosedAtUtc = now;
                session.CountedCash = counted;
                session.ExpectedCash = computed.Expected;
                session.Variance = LedgerWriter.Round(counted - computed.Expected);
                session.ClosingNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

                if (counted > 0)
                {
                    // The counted cash leaves the drawer at close; the row keeps the audit trail complete.
                    db.CashRegisterMovements.Add(new CashRegisterMovement
                    {
                        BusinessId = businessId,
                        CashRegisterId = id,
                        CashRegisterSessionId = session.Id,
                        MovementType = CashMovementType.Closing,
                        Amount = counted,
                        Currency = session.Currency,
                        Reason = "Session close count",
                        CreatedByUserId = userId,
                        OccurredAtUtc = now,
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "cash_register_session",
                    EntityId = session.Id,
                    Action = "closed",
                    OccurredAtUtc = now,
                    CorrelationId = null,
                    Summary = $"{{\"registerId\":\"{id}\",\"countedCash\":\"{counted.ToString(CultureInfo.InvariantCulture)}\",\"expectedCash\":\"{computed.Expected.ToString(CultureInfo.InvariantCulture)}\",\"variance\":\"{(counted - computed.Expected).ToString(CultureInfo.InvariantCulture)}\"}}",
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return session;
            }).ConfigureAwait(false);

            var detailComputed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

            return Results.Ok(await BuildDetailAsync(db, businessId, register, session, detailComputed, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private static async Task<CashRegisterSession?> LoadSessionForUpdateAsync(
        BillingDbContext db,
        Guid businessId,
        Guid registerId,
        Guid sessionId,
        CancellationToken cancellationToken)
        => await db.CashRegisterSessions
            .FromSql($"SELECT * FROM cash_register_sessions WHERE id = {sessionId} AND business_id = {businessId} AND cash_register_id = {registerId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Drawer arithmetic from append-only movements; expected = float + cash sales + in − out − refunds.</summary>
    private static async Task<ComputedSession> ComputeAsync(
        BillingDbContext db,
        Guid businessId,
        CashRegisterSession session,
        CancellationToken cancellationToken)
    {
        var sums = await db.CashRegisterMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId && movement.CashRegisterSessionId == session.Id)
            .GroupBy(movement => movement.MovementType)
            .Select(group => new { Type = group.Key, Amount = group.Sum(movement => movement.Amount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal Sum(CashMovementType type) => sums.Where(row => row.Type == type).Sum(row => row.Amount);

        var breakdown = new CashSessionBreakdownDto(
            session.OpeningFloat,
            Sum(CashMovementType.SalePayment),
            Sum(CashMovementType.CashIn),
            Sum(CashMovementType.CashOut),
            Sum(CashMovementType.Refund),
            Sum(CashMovementType.Closing));

        var expected = LedgerWriter.Round(breakdown.OpeningFloat + breakdown.SaleCash + breakdown.CashIn - breakdown.CashOut - breakdown.Refunds);

        // Offline provisional cash is never part of the expected authoritative drawer (QB-20); the field is
        // reported separately so a manager sees it without it ever counting as confirmed money.
        var provisional = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.CashRegisterSessionId == session.Id
                && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        return new ComputedSession(breakdown, expected, LedgerWriter.Round(provisional));
    }

    private static async Task<CashSessionDetailDto> BuildDetailAsync(
        BillingDbContext db,
        Guid businessId,
        CashRegister register,
        CashRegisterSession session,
        ComputedSession computed,
        CancellationToken cancellationToken)
    {
        var movementRows = await db.CashRegisterMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId && movement.CashRegisterSessionId == session.Id)
            .OrderBy(movement => movement.OccurredAtUtc)
            .ThenBy(movement => movement.Id)
            .Select(movement => new
            {
                movement.Id,
                movement.MovementType,
                movement.Amount,
                movement.Currency,
                movement.Reason,
                movement.SaleId,
                movement.CreatedByUserId,
                movement.OccurredAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Movement kinds are bridged in memory (D-31): stored text enums are never cast inside a projection.
        var movements = movementRows
            .Select(row => new CashMovementDto(
                row.Id,
                EnumBridge.To<CashMovementKind>(row.MovementType),
                row.Amount,
                row.Currency,
                row.Reason,
                row.SaleId,
                row.CreatedByUserId,
                row.OccurredAtUtc))
            .ToList();

        return new CashSessionDetailDto(
            ToDto(session) with { ExpectedCash = computed.Expected },
            register.Name,
            register.LocationId,
            computed.Expected,
            computed.Provisional,
            computed.Breakdown,
            movements);
    }

    private static CashRegisterSessionDto ToDto(CashRegisterSession session)
        => new(
            session.Id,
            session.CashRegisterId,
            session.Currency,
            EnumBridge.To<CashRegisterSessionState>(session.Status),
            session.OpeningFloat,
            session.OpenedAtUtc,
            session.OpenedByUserId,
            session.ClosedAtUtc,
            session.ClosedByUserId,
            session.CountedCash,
            session.ExpectedCash,
            session.Variance,
            session.ClosingNote);

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFoundRegister()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The cash register does not exist in this business.");

    private static IResult NotFoundSession()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The drawer session does not exist on this register.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_cash_register_sessions_open" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "This register already has an open drawer session."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The cash register write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The cash register write violates an integrity guard."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The cash register operation failed."),
        };

    // ---------------------------------------------------------------- local shapes

    private sealed record RegisterRow(Guid Id, Guid LocationId, string Name, bool IsActive);

    private sealed record ComputedSession(CashSessionBreakdownDto Breakdown, decimal Expected, decimal Provisional);
}
