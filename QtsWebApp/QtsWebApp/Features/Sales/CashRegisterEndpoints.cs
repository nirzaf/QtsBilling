using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
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
/// Closing a drawer is also a custody act (specification D07, task 19): money that could not be settled has to
/// be named and handed to a person before the drawer may go, and a counted discrepancy is posted to the ledger
/// rather than absorbed.
/// </summary>
public static class CashRegisterEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxReasonLength = 500;
    private const int MoneyScale = 4;
    private const int MaxCarryForwardItems = 200;
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>The account a counted cash difference is posted against: the business's configured cash-over/short account.</summary>
    private const string CashOverShortAccountCode = "5300";

    private const string CashAccountCode = "1000";

    /// <summary>The per-business counter the carry-forward documents number themselves from.</summary>
    private const string CashCustodySequenceName = "cash_custody_carry_forward";

    private const string CarryForwardPrefix = "CF-";

    public static IEndpointRouteBuilder MapCashRegisterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pos/registers").WithTags("POS");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.SalesPost);

        // Who may receive carried cash. A manager owns the drawer but does not administer accounts, and naming a
        // custodian by typed identifier is forbidden (requirements R07 and D07), so the people a carry-forward may
        // name are offered here.
        group.MapGet("/custodians", ListCustodiansAsync).RequireAuthorization(Permissions.RegistersManage);

        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions", OpenAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapGet("/{id:guid}/sessions/current", GetCurrentAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}/sessions", ListSessionsAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapGet("/{id:guid}/sessions/{sessionId:guid}", GetSessionAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions/{sessionId:guid}/movements", AddMovementAsync).RequireAuthorization(Permissions.RegistersManage);

        // What is still unresolved on this drawer, and the only document that may hand it to somebody else.
        group.MapGet("/{id:guid}/sessions/{sessionId:guid}/custody", GetCustodyAsync).RequireAuthorization(Permissions.RegistersManage);
        group.MapPost("/{id:guid}/sessions/{sessionId:guid}/custody-transfers", CreateCustodyTransferAsync).RequireAuthorization(Permissions.RegistersManage);

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
        HttpContext http,
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

        if (!TryGetUserId(principal, out var userId)
            || !await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this register location.");
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "cash_register.create",
                http.Request.Headers["Idempotency-Key"].ToString(),
                request,
                async () =>
                {
                    var register = new CashRegister
                    {
                        BusinessId = businessId,
                        LocationId = request.LocationId,
                        Name = name,
                        IsActive = true,
                    };
                    db.CashRegisters.Add(register);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return (StatusCodes.Status201Created, new CashRegisterDto(register.Id, register.LocationId, register.Name, register.IsActive, null));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/pos/registers/{execution.Body.Id}", execution.Body);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "A register with this name already exists at the location.");
        }

    }

    // ---------------------------------------------------------------- sessions

    private static async Task<IResult> OpenAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        OpenCashRegisterSessionRequest request,
        HttpContext http,
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

        // Opening a drawer is a command: the caller keeps one key for one intention, so a retry that lost the
        // answer cannot open a second drawer over the same float (specification D06, task 19).
        if (!TryReadIdempotencyKey(http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem!;
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

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, register.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this register location.");
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "cash_register.open",
                idempotencyKey,
                request,
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;

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

                    var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);
                    var detail = await BuildDetailAsync(db, businessId, register, session, computed, cancellationToken).ConfigureAwait(false);

                    return (StatusCodes.Status201Created, detail);
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/pos/registers/{id}/sessions/{execution.Body.Session.Id}",
                execution.Body);
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

        if (!TryGetUserId(principal, out var userId))
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

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, register.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this register location.");
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
        HttpContext http,
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

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, register.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this register location.");
        }

        if (!TryReadIdempotencyKey(http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem!;
        }

        try
        {
            // A paid-in or paid-out is one command: the same key may be retried after a lost answer, and the
            // drawer must not move twice. The session row is locked before anything is read, in the same order
            // the sale and refund paths use (specification D06).
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "cash_register.movement",
                idempotencyKey,
                request,
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;

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

                    return (StatusCodes.Status201Created, new CashMovementDto(
                        movement.Id,
                        EnumBridge.To<CashMovementKind>(movement.MovementType),
                        movement.Amount,
                        movement.Currency,
                        movement.Reason,
                        movement.SaleId,
                        movement.CreatedByUserId,
                        movement.OccurredAtUtc));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/pos/registers/{id}/sessions/{sessionId}", execution.Body);
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
        HttpContext http,
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

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, register.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this register location.");
        }

        if (!TryReadIdempotencyKey(http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem!;
        }

        // The close is dated on the business's own calendar, not the server's machine clock, because the period
        // gate and the journal must agree with the branch's trading day (specification D05, finding F21).
        var businessTimeZone = await db.Businesses.AsNoTracking()
            .Where(candidate => candidate.Id == businessId)
            .Select(candidate => candidate.TimeZone)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Closing is one command and one unit of work: the drawer state, the custody check, the counted
            // difference and its journal commit together, under the session lock the sale and refund paths also
            // take (specification D06/D07, task 19).
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "cash_register.close",
                idempotencyKey,
                request,
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;

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

                    // Nothing that has not been settled, and no terminal whose money nobody has accounted for, may
                    // be closed over. The only way past this is a carry-forward naming each item (D07).
                    var custody = await BuildCustodyAsync(db, businessId, register, session, cancellationToken).ConfigureAwait(false);

                    if (!custody.Closable)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.ApprovalRequired,
                            custody.BlockReason ?? "Unresolved cash must be carried forward before this drawer can close.",
                            "Record an authorised carry-forward naming each unresolved operation, its amount and the receiving custodian, or wait for the terminal to report.");
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

                    // A counted difference is posted, never absorbed. Only a real discrepancy produces a journal,
                    // and the business's own configured cash and cash-over/short accounts are used in the period
                    // the close falls in (specification D05, task 19, recipe step 4).
                    string? overShortEntry = null;

                    if (session.Variance is { } variance && variance != 0m)
                    {
                        var accounts = await LedgerWriter.ResolveAccountsAsync(
                            db, businessId, [CashAccountCode, CashOverShortAccountCode], cancellationToken).ConfigureAwait(false);

                        if (!accounts.ContainsKey(CashAccountCode) || !accounts.ContainsKey(CashOverShortAccountCode))
                        {
                            throw new DomainRuleException(
                                StatusCodes.Status409Conflict,
                                ApiErrorCodes.Conflict,
                                "The cash accounts needed for a counted difference are not configured.",
                                "Map the business's cash account and cash-over/short account before closing a drawer that does not count to the movements.");
                        }

                        var absolute = LedgerWriter.Round(Math.Abs(variance));
                        var amountText = absolute.ToString(CultureInfo.InvariantCulture);

                        var lines = variance > 0m
                            ? new[]
                            {
                                new JournalLineRequest(accounts[CashAccountCode].Id, absolute, 0m, $"Cash over at close of drawer {session.Id}", register.LocationId),
                                new JournalLineRequest(accounts[CashOverShortAccountCode].Id, 0m, absolute, "Cash over", register.LocationId),
                            }
                            : new[]
                            {
                                new JournalLineRequest(accounts[CashOverShortAccountCode].Id, absolute, 0m, "Cash short", register.LocationId),
                                new JournalLineRequest(accounts[CashAccountCode].Id, 0m, absolute, $"Cash short at close of drawer {session.Id}", register.LocationId),
                            };

                        var journal = await LedgerWriter.PostWithinTransactionAsync(
                            db,
                            businessId,
                            userId,
                            session.Currency,
                            LedgerWriter.LocalDate(businessTimeZone, now),
                            $"Cash over/short for register {register.Name}: counted {counted.ToString(CultureInfo.InvariantCulture)} against expected {computed.Expected.ToString(CultureInfo.InvariantCulture)}, difference {variance.ToString(CultureInfo.InvariantCulture)}",
                            JournalSourceType.CashOverShort,
                            session.Id,
                            null,
                            lines,
                            cancellationToken).ConfigureAwait(false);

                        overShortEntry = journal.EntryNumber;
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
                        Summary = $"{{\"registerId\":\"{id}\",\"countedCash\":\"{counted.ToString(CultureInfo.InvariantCulture)}\",\"expectedCash\":\"{computed.Expected.ToString(CultureInfo.InvariantCulture)}\",\"variance\":\"{(counted - computed.Expected).ToString(CultureInfo.InvariantCulture)}\",\"overShortEntry\":{(overShortEntry is null ? "null" : $"\"{overShortEntry}\"")},\"carriedForward\":\"{custody.CarryForwards.Count}\"}}",
                    });

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    var detailComputed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);
                    var detail = await BuildDetailAsync(db, businessId, register, session, detailComputed, cancellationToken).ConfigureAwait(false);

                    return (StatusCodes.Status200OK, detail);
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(execution.Body);
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

    // ---------------------------------------------------------------- custody (D07)

    /// <summary>The active accounts of this business a drawer's unresolved cash may be carried to.</summary>
    private static async Task<IResult> ListCustodiansAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rows = await db.BusinessMemberships.AsNoTracking()
            .Where(membership => membership.BusinessId == businessId && membership.IsActive)
            .OrderBy(membership => membership.UserId)
            .Select(membership => new
            {
                membership.UserId,
                membership.Role,
                Email = db.Users.Where(user => user.Id == membership.UserId).Select(user => user.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows
            .Select(row => new CashCustodianDto(row.UserId, row.Email ?? "an account without an address", row.Role.ToString()))
            .ToList());
    }

    private static async Task<IResult> GetCustodyAsync(
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

        return Results.Ok(await BuildCustodyAsync(db, businessId, register, session, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateCustodyTransferAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        Guid sessionId,
        CreateCustodyTransferRequest request,
        HttpContext http,
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

        var reason = request.Reason?.Trim() ?? string.Empty;
        var items = request.Items ?? [];
        var errors = new Dictionary<string, string[]>();

        if (reason is { Length: 0 } || reason.Length > MaxReasonLength)
        {
            errors["reason"] = [$"A reason of 1-{MaxReasonLength} characters is required: an unnamed handover of cash responsibility is not an audit record."];
        }

        if (items.Count is 0)
        {
            errors["items"] = ["Name at least one unresolved item. A carry-forward that names nothing closes nothing over."];
        }
        else if (items.Count > MaxCarryForwardItems)
        {
            errors["items"] = [$"One carry-forward names at most {MaxCarryForwardItems} items."];
        }
        else if (items.Any(item => item.Amount < 0m))
        {
            errors["items"] = ["A carried amount may not be negative: it would reduce what the receiving custodian answers for."];
        }
        else if (items.Select(item => (item.Kind, item.SourceId)).Distinct().Count() != items.Count)
        {
            errors["items"] = ["Each unresolved item is named once."];
        }

        if (request.ReceivingCustodianUserId == userId && !errors.ContainsKey("receivingCustodianUserId"))
        {
            errors["receivingCustodianUserId"] = ["The receiving custodian must be somebody else: a manager cannot hand unresolved cash to themselves and call it resolved."];
        }

        if (!await db.BusinessMemberships.AsNoTracking()
            .AnyAsync(membership => membership.BusinessId == businessId
                && membership.UserId == request.ReceivingCustodianUserId
                && membership.IsActive,
                cancellationToken)
            .ConfigureAwait(false))
        {
            errors["receivingCustodianUserId"] = ["Choose an active account in this business; money cannot be carried to somebody who does not hold a position here."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The carry-forward is incomplete.", errors);
        }

        if (!TryReadIdempotencyKey(http, out var idempotencyKey, out var keyProblem))
        {
            return keyProblem!;
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null)
        {
            return NotFoundRegister();
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "cash_register.custody_transfer",
                idempotencyKey,
                request,
                async () =>
                {
                    var now = DateTimeOffset.UtcNow;

                    // The session is locked first, in the same order the drawer commands use, so a transfer and a
                    // close cannot both read the same unresolved position (specification D06).
                    var session = await LoadSessionForUpdateAsync(db, businessId, id, sessionId, cancellationToken).ConfigureAwait(false);

                    if (session is null)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status404NotFound,
                            ApiErrorCodes.NotFound,
                            "The drawer session does not exist.",
                            "The session id may belong to another register or business.");
                    }

                    var custody = await BuildCustodyAsync(db, businessId, register, session, cancellationToken).ConfigureAwait(false);
                    var open = custody.Unresolved.ToDictionary(item => (item.Kind, item.SourceId));
                    var alreadyCarried = custody.CarryForwards
                        .Select(transfer => (transfer.Kind, transfer.SourceId))
                        .ToHashSet();

                    foreach (var item in items)
                    {
                        var key = (item.Kind, item.SourceId);

                        if (!open.TryGetValue(key, out var unresolved))
                        {
                            throw new DomainRuleException(
                                StatusCodes.Status400BadRequest,
                                ApiErrorCodes.ValidationFailed,
                                "This item is not an unresolved obligation of this drawer.",
                                "Carry forward only what the custody position names as unresolved; anything already settled or already carried has its own record.");
                        }

                        if (alreadyCarried.Contains(key))
                        {
                            throw new DomainRuleException(
                                StatusCodes.Status409Conflict,
                                ApiErrorCodes.IdempotencyKeyReused,
                                "This item has already been carried forward.",
                                $"{unresolved.Reference} is already named by a carry-forward on this session.");
                        }

                        // Understating a hand-over is how money disappears: the declared amount must at least cover
                        // what the server can see for this item.
                        if (item.Amount < unresolved.Amount)
                        {
                            throw new DomainRuleException(
                                StatusCodes.Status409Conflict,
                                ApiErrorCodes.ApprovalRequired,
                                "The carried amount understates what the server holds for this item.",
                                $"{unresolved.Reference} is {LedgerWriter.Round(unresolved.Amount).ToString(CultureInfo.InvariantCulture)} {unresolved.Currency} on this server; carry at least that, and state in the reason whatever the server cannot see.");
                        }
                    }

                    var number = await LedgerWriter.AllocateNumberAsync(
                        db,
                        businessId,
                        CashCustodySequenceName,
                        CarryForwardPrefix,
                        LedgerWriter.LocalDate(null, now).Year,
                        cancellationToken).ConfigureAwait(false);

                    var names = await db.Users.AsNoTracking()
                        .Where(user => request.ReceivingCustodianUserId == user.Id || user.Id == userId)
                        .ToDictionaryAsync(user => user.Id, user => user.Email ?? user.Id.ToString("N"), cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var item in items)
                    {
                        var unresolved = open[(item.Kind, item.SourceId)];

                        db.CashCustodyResolutions.Add(new CashCustodyResolution
                        {
                            BusinessId = businessId,
                            CashRegisterId = id,
                            CashRegisterSessionId = session.Id,
                            DeviceId = unresolved.DeviceId,
                            CarryForwardNumber = number,
                            SourceKind = ToKind(item.Kind),
                            SourceId = item.SourceId,
                            SourceReference = unresolved.Reference[..Math.Min(unresolved.Reference.Length, 160)],
                            Amount = LedgerWriter.Round(item.Amount),
                            Currency = session.Currency,
                            Reason = reason,
                            ReceivingCustodianUserId = request.ReceivingCustodianUserId,
                            AuthorisedByUserId = userId,
                            OccurredAtUtc = now,
                        });
                    }

                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "cash_custody_transfer",
                        EntityId = Guid.NewGuid(),
                        Action = "carried_forward",
                        OccurredAtUtc = now,
                        CorrelationId = null,
                        Summary = $"{{\"registerId\":\"{id}\",\"sessionId\":\"{session.Id}\",\"carryForward\":\"{number}\",\"items\":\"{items.Count}\",\"amount\":\"{LedgerWriter.Round(items.Sum(item => item.Amount)).ToString(CultureInfo.InvariantCulture)}\",\"custodian\":\"{names.GetValueOrDefault(request.ReceivingCustodianUserId)}\"}}",
                    });

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    var refreshed = await BuildCustodyAsync(db, businessId, register, session, cancellationToken).ConfigureAwait(false);

                    return (StatusCodes.Status201Created, refreshed);
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/pos/registers/{id}/sessions/{sessionId}/custody", execution.Body);
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

    /// <summary>
    /// The drawer's custody position: what the movements say the cash should be, what is still unresolved, what has
    /// already been carried forward, and whether the session may be closed at all. Unresolved money is never merged
    /// into the expected cash and never dropped from it (specification D07, task 19).
    /// </summary>
    private static async Task<DrawerCustodyDto> BuildCustodyAsync(
        BillingDbContext db,
        Guid businessId,
        CashRegister register,
        CashRegisterSession session,
        CancellationToken cancellationToken)
    {
        var computed = await ComputeAsync(db, businessId, session, cancellationToken).ConfigureAwait(false);

        // 1. Cash this drawer took that was never confirmed. An unsettled cheque or transfer is a different kind
        //    of promise - the Z report shows those as unsettled tenders - and does not hold a drawer open; cash that
        //    was taken for a sale the server never confirmed is exactly the money D07 refuses to let disappear.
        var pendingRows = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.CashRegisterSessionId == session.Id
                && payment.Method == PaymentMethod.Cash
                && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending)
            .Select(payment => new
            {
                payment.Id,
                payment.Amount,
                payment.Currency,
                payment.SaleId,
                SaleNumber = payment.SaleId == null
                    ? null
                    : db.Sales.Where(sale => sale.Id == payment.SaleId).Select(sale => sale.SaleNumber).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var unresolved = new List<UnresolvedCashItemDto>();

        foreach (var payment in pendingRows.OrderBy(row => row.Id))
        {
            unresolved.Add(new UnresolvedCashItemDto(
                CashCustodySourceState.ProvisionalPayment,
                payment.Id,
                payment.SaleId is null ? $"payment {payment.Id:N}" : $"payment against sale {payment.SaleNumber}",
                payment.Amount,
                payment.Currency,
                null,
                null,
                null,
                "Recorded against this drawer as cash and never confirmed: it is not in the expected cash, and it is not settled money either."));
        }

        // 2. Terminals of this register's branch. The server cannot count money a terminal has not pushed, so an
        // absent last contact is reported as an evidence gap, never as zero (specification D07).
        var devices = await db.DeviceRegistrations.AsNoTracking()
            .Where(device => device.BusinessId == businessId
                && device.LocationId == register.LocationId
                && device.RetiredAtUtc == null)
            .OrderBy(device => device.Name)
            .Select(device => new { device.Id, device.Name, device.Status, device.LastSeenAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var deviceIds = devices.Select(device => device.Id).ToArray();

        var reviewRows = await db.ProcessedOperations.AsNoTracking()
            .Where(operation => operation.BusinessId == businessId
                && deviceIds.Contains(operation.DeviceId)
                && operation.Outcome == SyncOutcome.NeedsReview)
            .OrderBy(operation => operation.ProcessedAtUtc)
            .ThenBy(operation => operation.Id)
            .Select(operation => new
            {
                operation.Id,
                operation.DeviceId,
                operation.OperationId,
                operation.ServerSaleId,
                SaleNumber = operation.ServerSaleId == null
                    ? null
                    : db.Sales.Where(sale => sale.Id == operation.ServerSaleId).Select(sale => sale.SaleNumber).FirstOrDefault(),
                SaleTotal = operation.ServerSaleId == null
                    ? null
                    : db.Sales.Where(sale => sale.Id == operation.ServerSaleId).Select(sale => (decimal?)sale.TotalAmount).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in reviewRows)
        {
            var deviceName = devices.FirstOrDefault(device => device.Id == operation.DeviceId)?.Name ?? "a terminal of this branch";

            unresolved.Add(new UnresolvedCashItemDto(
                CashCustodySourceState.DeviceOperation,
                operation.Id,
                operation.ServerSaleId is null
                    ? $"operation {operation.OperationId:N} from {deviceName}"
                    : $"operation {operation.OperationId:N} from {deviceName}, posted as sale {operation.SaleNumber}",
                operation.SaleTotal ?? 0m,
                session.Currency,
                operation.DeviceId,
                deviceName,
                devices.FirstOrDefault(device => device.Id == operation.DeviceId)?.LastSeenAtUtc,
                operation.ServerSaleId is null
                    ? "The server holds this operation for review and has never posted a sale for it, so the amount it represents is only known to the terminal - a manager must state it."
                    : "The server posted this operation and left it awaiting review, so the money on it is not yet the business's to count."));
        }

        var evidence = new List<DeviceCustodyEvidenceDto>();

        foreach (var device in devices)
        {
            var outstanding = reviewRows.Count(row => row.DeviceId == device.Id);
            var outstandingValue = reviewRows.Where(row => row.DeviceId == device.Id).Sum(row => row.SaleTotal ?? 0m);
            var contacted = device.LastSeenAtUtc is { } seen && seen >= session.OpenedAtUtc;

            var explanation = device.LastSeenAtUtc is null
                ? "This terminal has never reported to the server. Nothing it holds can be counted from here, and that is not proof of zero."
                : contacted
                    ? outstanding == 0
                        ? "It has reported since this drawer opened and nothing it pushed awaits review."
                        : $"It has reported since this drawer opened, and {outstanding} operation(s) it pushed still await review."
                    : "Its last contact predates this drawer session, so any cash it took after that is invisible here until it reports.";

            evidence.Add(new DeviceCustodyEvidenceDto(
                device.Id,
                device.Name,
                device.Status.ToString(),
                device.LastSeenAtUtc,
                contacted,
                outstanding,
                LedgerWriter.Round(outstandingValue),
                explanation));

            if (!contacted)
            {
                unresolved.Add(new UnresolvedCashItemDto(
                    CashCustodySourceState.DeviceCustody,
                    device.Id,
                    $"terminal {device.Name}",
                    0m,
                    session.Currency,
                    device.Id,
                    device.Name,
                    device.LastSeenAtUtc,
                    explanation));
            }
        }

        // 3. What has already been handed over, with the people named.
        var transferRows = await db.CashCustodyResolutions.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.CashRegisterSessionId == session.Id)
            .OrderBy(row => row.CreatedAtUtc)
            .ThenBy(row => row.CarryForwardNumber)
            .ThenBy(row => row.SourceReference)
            .Select(row => new
            {
                row.Id,
                row.CarryForwardNumber,
                row.SourceKind,
                row.SourceId,
                row.SourceReference,
                row.Amount,
                row.Currency,
                row.Reason,
                row.ReceivingCustodianUserId,
                row.AuthorisedByUserId,
                row.CreatedAtUtc,
                Custodian = db.Users.Where(user => user.Id == row.ReceivingCustodianUserId).Select(user => user.Email).FirstOrDefault(),
                Authoriser = db.Users.Where(user => user.Id == row.AuthorisedByUserId).Select(user => user.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var carryForwards = transferRows
            .Select(row => new CashCustodyTransferDto(
                row.CarryForwardNumber,
                FromKind(row.SourceKind),
                row.SourceId,
                row.SourceReference,
                row.Amount,
                row.Currency,
                row.Reason,
                row.ReceivingCustodianUserId,
                row.Custodian ?? "an account no longer listed",
                row.AuthorisedByUserId,
                row.Authoriser ?? "an account no longer listed",
                row.CreatedAtUtc))
            .ToList();

        var covered = carryForwards
            .Select(transfer => (transfer.Kind, transfer.SourceId))
            .ToHashSet();

        var uncovered = unresolved.Where(item => !covered.Contains((item.Kind, item.SourceId))).ToArray();
        var uncoveredAmount = LedgerWriter.Round(uncovered.Sum(item => item.Amount));
        var closable = uncovered.Length == 0;

        var block = closable
            ? null
            : $"{uncovered.Length} unresolved item(s) on this drawer, totalling {uncoveredAmount.ToString(CultureInfo.InvariantCulture)} {session.Currency} of visible money, have not been carried forward to a named custodian. Closing now would leave that cash with nobody answering for it.";

        return new DrawerCustodyDto(
            session.Id,
            register.Id,
            session.Currency,
            computed.Expected,
            computed.Provisional,
            unresolved,
            evidence,
            carryForwards,
            uncoveredAmount,
            closable,
            block);
    }

    // ---------------------------------------------------------------- shared helpers

    /// <summary>
    /// Reads the command key. A drawer command without one is refused rather than executed unguarded, because the
    /// only safe retry of a lost answer is the same key (specification D06, task 19).
    /// </summary>
    private static bool TryReadIdempotencyKey(HttpContext http, out string key, out IResult? problem)
    {
        key = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (key.Length is > 0 and <= 64)
        {
            problem = null;
            return true;
        }

        problem = ApiResults.ValidationProblem(
            "The cash command is missing its idempotency key.",
            new Dictionary<string, string[]>
            {
                [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header; the same intention must be retried with the same key."],
            });

        return false;
    }

    private static CashCustodySourceKind ToKind(CashCustodySourceState state)
        => Enum.Parse<CashCustodySourceKind>(state.ToString());

    private static CashCustodySourceState FromKind(CashCustodySourceKind kind)
        => EnumBridge.To<CashCustodySourceState>(kind);

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
