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

/// <summary>Dispatch and receipt workflow for valued, inter-location inventory transfers.</summary>
internal static class StockTransferEndpoints
{
    private const int MaxLines = 100;
    private const int MaxNoteLength = 2_000;
    private const int QuantityScale = 6;
    private const string IdempotencyHeader = "Idempotency-Key";

    public static void MapStockTransferEndpoints(RouteGroupBuilder inventory)
    {
        var transfers = inventory.MapGroup("/transfers").WithTags("Inventory");
        transfers.MapGet("/", ListAsync).RequireAuthorization(Permissions.ProductsRead);
        transfers.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.ProductsRead);
        transfers.MapPost("/", CreateAsync).RequireAuthorization(Permissions.InventoryAdjust);
        transfers.MapPost("/{id:guid}/dispatch", DispatchAsync).RequireAuthorization(Permissions.InventoryAdjust);
        transfers.MapPost("/{id:guid}/receive", ReceiveAsync).RequireAuthorization(Permissions.InventoryAdjust);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();

        var rows = await (
            from transfer in db.StockTransfers.AsNoTracking()
            join source in db.BusinessLocations.AsNoTracking() on new { transfer.SourceLocationId, transfer.BusinessId } equals new { SourceLocationId = source.Id, source.BusinessId }
            join destination in db.BusinessLocations.AsNoTracking() on new { transfer.DestinationLocationId, transfer.BusinessId } equals new { DestinationLocationId = destination.Id, destination.BusinessId }
            where transfer.BusinessId == businessId
            orderby transfer.Id descending
            select new
            {
                transfer.Id,
                transfer.TransferNumber,
                transfer.Status,
                transfer.SourceLocationId,
                SourceLocationName = source.Name,
                transfer.DestinationLocationId,
                DestinationLocationName = destination.Name,
                transfer.Currency,
                transfer.CreatedAtUtc,
                transfer.DispatchedAtUtc,
                transfer.ReceivedAtUtc,
                TotalQuantity = db.StockTransferLines.Where(line => line.BusinessId == businessId && line.StockTransferId == transfer.Id).Sum(line => (decimal?)line.Quantity) ?? 0m,
                TotalValue = db.StockTransferLines.Where(line => line.BusinessId == businessId && line.StockTransferId == transfer.Id).Sum(line => line.ValueAtDispatch),
            })
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows.Select(row => new StockTransferSummaryDto(
            row.Id,
            row.TransferNumber,
            EnumBridge.To<StockTransferState>(row.Status),
            row.SourceLocationId,
            row.SourceLocationName,
            row.DestinationLocationId,
            row.DestinationLocationName,
            row.Currency,
            row.TotalQuantity,
            row.TotalValue,
            row.CreatedAtUtc,
            row.DispatchedAtUtc,
            row.ReceivedAtUtc)).ToList());
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateStockTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
            return BusinessScope.MissingMembership();

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var errors = ValidateRequest(request);
        if (errors.Count > 0) return ApiResults.ValidationProblem("The stock-transfer payload is invalid.", errors);

        var groupedLines = request.Lines
            .GroupBy(line => line.VariantId)
            .Select(group => new StockTransferLineRequest(group.Key, group.Sum(line => line.Quantity)))
            .OrderBy(line => line.VariantId)
            .ToList();
        var normalized = new CreateStockTransferRequest(
            request.SourceLocationId,
            request.DestinationLocationId,
            groupedLines,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());
        var payloadHash = RequestFingerprint.Compute(normalized);

        var existing = await db.StockTransfers.AsNoTracking()
            .FirstOrDefaultAsync(transfer => transfer.BusinessId == businessId && transfer.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return await ReplayOrConflictAsync(db, businessId, existing, payloadHash, cancellationToken).ConfigureAwait(false);

        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(location => location.BusinessId == businessId
                && (location.Id == normalized.SourceLocationId || location.Id == normalized.DestinationLocationId)
                && location.IsActive)
            .Select(location => location.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (locations.Count != 2)
        {
            return ApiResults.ValidationProblem("The stock-transfer payload is invalid.",
                new Dictionary<string, string[]> { ["locationId"] = ["Both locations must be active and belong to this business."] });
        }
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, normalized.SourceLocationId, cancellationToken).ConfigureAwait(false)
            || !await BusinessScope.CanAccessLocationAsync(db, userId, businessId, normalized.DestinationLocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access both transfer locations.");
        }

        var variantIds = groupedLines.Select(line => line.VariantId).ToArray();
        var stockVariantIds = await (
            from variant in db.ProductVariants.AsNoTracking()
            join product in db.Products.AsNoTracking() on new { variant.ProductId, variant.BusinessId } equals new { ProductId = product.Id, product.BusinessId }
            where variant.BusinessId == businessId && variantIds.Contains(variant.Id) && product.TrackStock
            select variant.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stockVariantIds.Count != variantIds.Length)
        {
            return ApiResults.ValidationProblem("The stock-transfer payload is invalid.",
                new Dictionary<string, string[]> { ["lines"] = ["Every variant must exist in this business and track stock."] });
        }

        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        if (business is null) return BusinessScope.MissingMembership();

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var transferId = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                var duplicate = await db.StockTransfers.AsNoTracking()
                    .FirstOrDefaultAsync(transfer => transfer.BusinessId == businessId && transfer.IdempotencyKey == idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                {
                    if (!string.Equals(duplicate.PayloadHash, payloadHash, StringComparison.Ordinal))
                        throw SameKeyDifferentPayload();
                    return duplicate.Id;
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var number = await LedgerWriter.AllocateNumberAsync(
                    db, businessId, "stock-transfer", "ST-", LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow).Year, cancellationToken).ConfigureAwait(false);
                var transfer = new StockTransfer
                {
                    BusinessId = businessId,
                    TransferNumber = number,
                    SourceLocationId = normalized.SourceLocationId,
                    DestinationLocationId = normalized.DestinationLocationId,
                    Status = StockTransferStatus.Draft,
                    Currency = business.BaseCurrency,
                    Note = normalized.Note,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };
                db.StockTransfers.Add(transfer);
                for (var index = 0; index < groupedLines.Count; index++)
                {
                    db.StockTransferLines.Add(new StockTransferLine
                    {
                        BusinessId = businessId,
                        StockTransferId = transfer.Id,
                        LineNo = index + 1,
                        VariantId = groupedLines[index].VariantId,
                        Quantity = groupedLines[index].Quantity,
                    });
                }
                db.AuditEvents.Add(AuditEventFor(businessId, userId, "stock_transfer", transfer.Id, "created", DateTimeOffset.UtcNow, http.TraceIdentifier,
                    new { transfer.TransferNumber, transfer.SourceLocationId, transfer.DestinationLocationId, lineCount = groupedLines.Count }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return transfer.Id;
            }).ConfigureAwait(false);

            var dto = await ToDetailAsync(db, businessId, transferId, cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/inventory/transfers/{transferId:D}", dto);
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName == "ux_stock_transfers_business_idempotency_key")
        {
            db.ChangeTracker.Clear();
            var winner = await db.StockTransfers.AsNoTracking()
                .FirstOrDefaultAsync(transfer => transfer.BusinessId == businessId && transfer.IdempotencyKey == idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            return winner is null
                ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.IdempotencyKeyReused, "A concurrent request with this key committed first.")
                : await ReplayOrConflictAsync(db, businessId, winner, payloadHash, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static Task<IResult> DispatchAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
        => TransitionAsync(principal, db, id, http, receive: false, cancellationToken);

    private static Task<IResult> ReceiveAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
        => TransitionAsync(principal, db, id, http, receive: true, cancellationToken);

    private static async Task<IResult> TransitionAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        bool receive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
            return BusinessScope.MissingMembership();

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var transfer = await db.StockTransfers
                    .FromSql($"SELECT * FROM stock_transfers WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (transfer is null) throw NotFoundException();

                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, transfer.SourceLocationId, cancellationToken).ConfigureAwait(false)
                    || !await BusinessScope.CanAccessLocationAsync(db, userId, businessId, transfer.DestinationLocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The membership cannot access both transfer locations.", "Grant access to both locations before posting this transfer.");
                }

                if ((!receive && transfer.Status is StockTransferStatus.Dispatched or StockTransferStatus.Received)
                    || (receive && transfer.Status == StockTransferStatus.Received))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if ((!receive && transfer.Status != StockTransferStatus.Draft)
                    || (receive && transfer.Status != StockTransferStatus.Dispatched))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        receive ? "Only a dispatched transfer can be received." : "Only a draft transfer can be dispatched.",
                        "Refresh the transfer and follow its current lifecycle state.");
                }

                var lines = await db.StockTransferLines
                    .Where(line => line.BusinessId == businessId && line.StockTransferId == id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (lines.Count == 0) throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                    "The transfer has no lines.", "A stock transfer must contain at least one line before posting.");

                var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
                if (business is null) throw NotFoundException();
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, ["1200", "1210"], cancellationToken).ConfigureAwait(false);
                if (!accounts.ContainsKey("1200") || !accounts.ContainsKey("1210"))
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Inventory transfer accounts are not configured.", "Configure account 1200 Inventory and 1210 Inventory In Transit before posting transfers.");

                var now = DateTimeOffset.UtcNow;
                var postingDate = LedgerWriter.LocalDate(business.TimeZone, now);
                decimal totalValue = 0m;
                if (!receive)
                {
                    foreach (var line in lines.OrderBy(line => line.VariantId))
                    {
                        var movement = await InventoryLedger.ApplyAsync(
                            db, businessId, userId, transfer.SourceLocationId, line.VariantId, MovementType.TransferOut,
                            -line.Quantity, 0m, "stock_transfer", transfer.Id,
                            $"Transfer {transfer.TransferNumber} dispatched", now, cancellationToken).ConfigureAwait(false);
                        var value = LedgerWriter.Round(-movement.ValueDelta);
                        if (value <= 0m)
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.ValuationRequired,
                                "The source stock has no transferable value.", "Value the stock at its source location before dispatching this transfer.");
                        var unitCost = Math.Round(value / line.Quantity, 8, MidpointRounding.AwayFromZero);
                        movement.UnitCost = unitCost;
                        line.UnitCostAtDispatch = unitCost;
                        line.ValueAtDispatch = value;
                        totalValue += value;
                    }

                    var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, transfer.Currency, postingDate,
                        $"Transfer {transfer.TransferNumber} dispatched", JournalSourceType.StockTransfer, transfer.Id, null,
                        [
                            new JournalLineRequest(accounts["1210"].Id, totalValue, 0m, "Inventory in transit", transfer.SourceLocationId),
                            new JournalLineRequest(accounts["1200"].Id, 0m, totalValue, "Inventory dispatched", transfer.SourceLocationId),
                        ], cancellationToken).ConfigureAwait(false);
                    transfer.Status = StockTransferStatus.Dispatched;
                    transfer.DispatchedAtUtc = now;
                    transfer.DispatchedByUserId = userId;
                    transfer.DispatchJournalEntryId = journal.Id;
                    db.AuditEvents.Add(AuditEventFor(businessId, userId, "stock_transfer", id, "dispatched", now, http.TraceIdentifier,
                        new { transfer.TransferNumber, totalValue, journalEntryId = journal.Id }));
                }
                else
                {
                    foreach (var line in lines.OrderBy(line => line.VariantId))
                    {
                        if (line.UnitCostAtDispatch is null || line.ValueAtDispatch is null)
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                                "The transfer is missing its dispatch valuation.", "Refresh the transfer or contact an administrator before receipt.");
                        var movement = await InventoryLedger.ApplyAsync(
                            db, businessId, userId, transfer.DestinationLocationId, line.VariantId, MovementType.TransferIn,
                            line.Quantity, line.UnitCostAtDispatch.Value, "stock_transfer", transfer.Id,
                            $"Transfer {transfer.TransferNumber} received", now, cancellationToken).ConfigureAwait(false);
                        if (LedgerWriter.Round(movement.ValueDelta) != line.ValueAtDispatch.Value)
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                                "The transfer valuation could not be preserved.", "The destination movement did not match the value recorded at dispatch.");
                        totalValue += line.ValueAtDispatch.Value;
                    }

                    var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, transfer.Currency, postingDate,
                        $"Transfer {transfer.TransferNumber} received", JournalSourceType.StockTransferReceipt, transfer.Id, null,
                        [
                            new JournalLineRequest(accounts["1200"].Id, totalValue, 0m, "Inventory received", transfer.DestinationLocationId),
                            new JournalLineRequest(accounts["1210"].Id, 0m, totalValue, "Clear inventory in transit", transfer.DestinationLocationId),
                        ], cancellationToken).ConfigureAwait(false);
                    transfer.Status = StockTransferStatus.Received;
                    transfer.ReceivedAtUtc = now;
                    transfer.ReceivedByUserId = userId;
                    transfer.ReceiveJournalEntryId = journal.Id;
                    db.AuditEvents.Add(AuditEventFor(businessId, userId, "stock_transfer", id, "received", now, http.TraceIdentifier,
                        new { transfer.TransferNumber, totalValue, journalEntryId = journal.Id }));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var dto = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFound() : Results.Ok(dto);
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure,
                "The stock transfer changed while it was being posted.", "Refresh the transfer before retrying.");
        }
    }

    private static async Task<StockTransferDetailDto?> ToDetailAsync(
        BillingDbContext db,
        Guid businessId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var header = await (
            from transfer in db.StockTransfers.AsNoTracking()
            join source in db.BusinessLocations.AsNoTracking() on new { transfer.SourceLocationId, transfer.BusinessId } equals new { SourceLocationId = source.Id, source.BusinessId }
            join destination in db.BusinessLocations.AsNoTracking() on new { transfer.DestinationLocationId, transfer.BusinessId } equals new { DestinationLocationId = destination.Id, destination.BusinessId }
            where transfer.BusinessId == businessId && transfer.Id == id
            select new
            {
                transfer.Id, transfer.TransferNumber, transfer.Status, transfer.SourceLocationId, SourceName = source.Name,
                transfer.DestinationLocationId, DestinationName = destination.Name, transfer.Currency, transfer.Note,
                transfer.CreatedAtUtc, transfer.DispatchedAtUtc, transfer.ReceivedAtUtc,
                transfer.DispatchJournalEntryId, transfer.ReceiveJournalEntryId,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (header is null) return null;

        var lines = await (
            from line in db.StockTransferLines.AsNoTracking()
            join variant in db.ProductVariants.AsNoTracking() on new { line.VariantId, line.BusinessId } equals new { VariantId = variant.Id, variant.BusinessId }
            where line.BusinessId == businessId && line.StockTransferId == id
            orderby line.LineNo
            select new StockTransferLineDto(line.Id, line.LineNo, line.VariantId, variant.Name, variant.Sku, line.Quantity,
                line.UnitCostAtDispatch, line.ValueAtDispatch))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new StockTransferDetailDto(
            header.Id, header.TransferNumber, EnumBridge.To<StockTransferState>(header.Status),
            header.SourceLocationId, header.SourceName, header.DestinationLocationId, header.DestinationName,
            header.Currency, header.Note, header.CreatedAtUtc, header.DispatchedAtUtc, header.ReceivedAtUtc,
            header.DispatchJournalEntryId, header.ReceiveJournalEntryId, lines, lines.Sum(line => line.Quantity),
            lines.Any(line => line.ValueAtDispatch is not null) ? lines.Sum(line => line.ValueAtDispatch ?? 0m) : null);
    }

    private static Dictionary<string, string[]> ValidateRequest(CreateStockTransferRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["request"] = ["A stock-transfer payload is required."];
            return errors;
        }
        if (request.SourceLocationId == Guid.Empty || request.DestinationLocationId == Guid.Empty
            || request.SourceLocationId == request.DestinationLocationId)
            errors["locationId"] = ["Choose two different source and destination locations."];
        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines)
            errors["lines"] = [$"A transfer requires 1-{MaxLines} lines."];
        else
        {
            for (var index = 0; index < request.Lines.Count; index++)
            {
                var line = request.Lines[index];
                if (line.VariantId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, QuantityScale) != line.Quantity)
                    errors[$"lines[{index}]"] = ["Each line requires a variant and positive quantity with at most 6 decimal places."];
            }
        }
        if (request.Note is { Length: > MaxNoteLength }) errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        return errors;
    }

    private static async Task<IResult> ReplayOrConflictAsync(
        BillingDbContext db,
        Guid businessId,
        StockTransfer existing,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)) return SameKeyDifferentPayload().ToResult();
        var dto = await ToDetailAsync(db, businessId, existing.Id, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Results.Ok(dto);
    }

    private static DomainRuleException SameKeyDifferentPayload()
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload,
            "This idempotency key was used for a different transfer payload.",
            "Retry with the original transfer details or start a new transfer with a new key.");

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFound()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The stock transfer does not exist in this business.");

    private static DomainRuleException NotFoundException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The stock transfer does not exist in this business.",
            "Check the identifier: stock transfers are scoped to the signed-in business.");

    private static AuditEvent AuditEventFor(Guid businessId, Guid userId, string entityType, Guid entityId, string action,
        DateTimeOffset occurredAtUtc, string correlationId, object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredAtUtc = occurredAtUtc,
            CorrelationId = correlationId,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        };

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The stock-transfer write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The stock-transfer write violates an inventory integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                "The stock transfer references a record outside this business."),
            _ => ApiResults.Problem(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError,
                "The stock-transfer operation failed."),
        };
}
