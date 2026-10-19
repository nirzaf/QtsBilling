using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sales;
using QtsWebApp.Infrastructure;

// The persistence layer owns the stored outcome enum; the wire contract owns the enum the device reads.
using SyncOutcome = QtsBilling.Contracts.Sync.SyncOutcome;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// The offline sync-push handler (QB-20). A device replays the operations it captured into its local
/// queue; the server accepts each operation exactly once:
/// <list type="bullet">
///   <item>the operation id is unique per (business, device, operation) in <c>processed_operations</c>,</item>
///   <item>the receipt row is written in the same transaction as the business effect, so a crash after the
///   commit can never produce a second sale,</item>
///   <item>a replay returns the original outcome,</item>
///   <item>the same key with a different body is a stable conflict,</item>
///   <item>the sale itself is posted with the QB-09 rules - the device never decides price, tax or stock.</item>
/// </list>
/// </summary>
public static class SyncOperationEndpoints
{
    private const string SaleOperationType = SyncOperationTypes.PosSaleCaptureV1;
    private const int SalePayloadVersion = 1;

    public static IEndpointRouteBuilder MapSyncOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sync").WithTags("Sync");

        group.MapPost("/operations", PostOperationAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapPost("/operations/batch", PostBatchAsync).RequireAuthorization(Permissions.SalesPost);

        return app;
    }

    private static async Task<IResult> PostBatchAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IReadOnlyList<SyncOperationEnvelope> envelopes,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (envelopes is null || envelopes.Count == 0)
        {
            return ApiResults.ValidationProblem(
                "The sync batch is empty.",
                new Dictionary<string, string[]> { ["operations"] = ["Send at least one operation."] });
        }

        if (envelopes.Count > 50)
        {
            return ApiResults.ValidationProblem(
                "The sync batch is too large.",
                new Dictionary<string, string[]> { ["operations"] = ["Send at most 50 operations per request."] });
        }

        var receipts = new List<SyncOperationReceipt>(envelopes.Count);

        foreach (var envelope in envelopes)
        {
            var result = await ProcessAsync(principal, db, envelope, http, cancellationToken).ConfigureAwait(false);
            receipts.Add(result.Receipt);

            if (result.Result is not null)
            {
                // A transport-level failure stops the batch: the device keeps the remaining operations.
                return result.Result;
            }
        }

        return Results.Ok(receipts);
    }

    private static async Task<IResult> PostOperationAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SyncOperationEnvelope envelope,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (envelope is null)
        {
            return ApiResults.ValidationProblem(
                "The sync operation is empty.",
                new Dictionary<string, string[]> { ["operation"] = ["Send the operation envelope."] });
        }

        var outcome = await ProcessAsync(principal, db, envelope, http, cancellationToken).ConfigureAwait(false);

        if (outcome.Result is not null)
        {
            return outcome.Result;
        }

        return Results.Ok(outcome.Receipt);
    }

    private static async Task<(SyncOperationReceipt Receipt, IResult? Result)> ProcessAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SyncOperationEnvelope envelope,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)
            || !Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NotAuthorized, ApiErrorCodes.Forbidden),
                BusinessScope.MissingMembership());
        }

        if (envelope.OperationId == Guid.Empty)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NotAuthorized, ApiErrorCodes.ValidationFailed),
                ApiResults.ValidationProblem(
                    "The sync operation is invalid.",
                    new Dictionary<string, string[]> { ["operationId"] = ["The operation id is required."] }));
        }

        if (envelope.BusinessId != businessId)
        {
            // The operation belongs to another business: it is never processed here.
            return (Rejected(envelope.OperationId, SyncOutcome.NotAuthorized, ApiErrorCodes.Forbidden),
                ApiResults.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.Forbidden,
                    "The operation belongs to another business."));
        }

        if (!string.Equals(envelope.Type, SaleOperationType, StringComparison.Ordinal))
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NeedsReview, "UNSUPPORTED_OPERATION_TYPE"),
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    "UNSUPPORTED_OPERATION_TYPE",
                    "The operation type is not supported.",
                    $"Only '{SaleOperationType}' can be pushed in this cutover."));
        }

        if (envelope.PayloadVersion != SalePayloadVersion)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.UnsupportedPayloadVersion, ApiErrorCodes.UnsupportedPayloadVersion),
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.UnsupportedPayloadVersion,
                    "The payload version is not supported.",
                    $"This server understands payload version {SalePayloadVersion}."));
        }

        var device = await db.DeviceRegistrations.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.BusinessId == businessId && candidate.Id == envelope.DeviceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NotAuthorized, ApiErrorCodes.DeviceNotEnrolled),
                ApiResults.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.DeviceNotEnrolled,
                    "The device is not enrolled in this business."));
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.DeviceRevoked, ApiErrorCodes.DeviceRevoked),
                ApiResults.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.DeviceRevoked,
                    "The device was revoked."));
        }

        // The server recomputes the fingerprint with the shared canonical rules; the device's hash is only
        // a cross-check that the body was not mangled on the way.
        var fingerprint = CanonicalJson.ComputeFingerprint(envelope.Payload);

        if (!string.IsNullOrWhiteSpace(envelope.PayloadHash)
            && !string.Equals(envelope.PayloadHash, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NeedsReview, "PAYLOAD_HASH_MISMATCH"),
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    "PAYLOAD_HASH_MISMATCH",
                    "The payload does not match its fingerprint."));
        }

        var processed = await db.ProcessedOperations.AsNoTracking()
            .FirstOrDefaultAsync(
                operation => operation.BusinessId == businessId
                    && operation.DeviceId == envelope.DeviceId
                    && operation.OperationId == envelope.OperationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (processed is not null)
        {
            if (!string.Equals(processed.PayloadHash, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                // Same key, different body: a stable conflict, and the original operation is untouched.
                return (new SyncOperationReceipt(
                        envelope.OperationId,
                        SyncOutcome.SameKeyDifferentPayload,
                        ApiErrorCodes.SameKeyDifferentPayload,
                        "The operation id was already used with a different payload.",
                        processed.ServerSaleId,
                        null,
                        processed.ServerRevision,
                        processed.ProcessedAtUtc),
                    ApiResults.Problem(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.SameKeyDifferentPayload,
                        "This operation id was already used with a different payload."));
            }

            // A replay returns the original committed outcome - never a second business effect.
            return (new SyncOperationReceipt(
                    envelope.OperationId,
                    ToWire(processed.Outcome),
                    processed.ErrorCode,
                    null,
                    processed.ServerSaleId,
                    null,
                    processed.ServerRevision,
                    processed.ProcessedAtUtc),
                null);
        }

        if (envelope.Type != SaleOperationType)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NeedsReview, "UNSUPPORTED_OPERATION_TYPE"), null);
        }

        return await PostSaleAsync(
            principal,
            db,
            envelope,
            businessId,
            userId,
            fingerprint,
            http,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(SyncOperationReceipt Receipt, IResult? Result)> PostSaleAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SyncOperationEnvelope envelope,
        Guid businessId,
        Guid userId,
        string fingerprint,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!TryReadSale(envelope.Payload, out var request, out var payloadError))
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NeedsReview, ApiErrorCodes.ValidationFailed),
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The offline payload is invalid.",
                    payloadError));
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return (Rejected(envelope.OperationId, SyncOutcome.NotAuthorized, ApiErrorCodes.Forbidden),
                BusinessScope.MissingMembership());
        }

        var lines = request.Lines ?? [];

        var context = await SalePosting.ResolveContextAsync(db, businessId, request, lines, cancellationToken)
            .ConfigureAwait(false);

        if (context.Error is not null)
        {
            return await NeedsReviewAsync(envelope.OperationId, context.Error, http, cancellationToken).ConfigureAwait(false);
        }

        var pricing = SalePosting.PriceLines(lines, context.Variants, context.Products, context.TaxRates);

        if (pricing.Error is not null)
        {
            return await NeedsReviewAsync(envelope.OperationId, pricing.Error, http, cancellationToken).ConfigureAwait(false);
        }

        var payment = SalePosting.ValidatePayment(request, pricing);

        if (payment.Error is not null)
        {
            return await NeedsReviewAsync(envelope.OperationId, payment.Error, http, cancellationToken).ConfigureAwait(false);
        }

        var register = await SalePosting.ResolveRegisterAsync(db, businessId, request, cancellationToken)
            .ConfigureAwait(false);

        if (register.Error is not null)
        {
            return await NeedsReviewAsync(envelope.OperationId, register.Error, http, cancellationToken).ConfigureAwait(false);
        }

        var processed = new ProcessedOperation
        {
            BusinessId = businessId,
            DeviceId = envelope.DeviceId,
            OperationId = envelope.OperationId,
            OperationType = envelope.Type,
            PayloadHash = fingerprint,
            Outcome = QtsBilling.Persistence.Postgres.SyncOutcome.Posted,
            ProcessedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            // The sale and the receipt row are committed by one transaction: a crash after the commit is
            // safe, because the retry finds the receipt and returns the original outcome.
            var sale = await SalePosting.PostSaleAsync(
                db,
                business,
                userId,
                request,
                pricing.Lines,
                payment,
                register.Session,
                $"sync-{envelope.OperationId:N}",
                fingerprint,
                http.TraceIdentifier,
                cancellationToken,
                processed,
                beforeCommit: posted => processed.ServerSaleId = posted.Id).ConfigureAwait(false);

            return (new SyncOperationReceipt(
                envelope.OperationId,
                SyncOutcome.Posted,
                null,
                null,
                sale.Id,
                sale.SaleNumber,
                null,
                processed.ProcessedAtUtc), null);
        }
        catch (DomainRuleException exception)
        {
            // A business rule refused the sale (stock, price, register): the decision is remembered, so a
            // retry of the same operation returns the same outcome and a different body is a conflict.
            db.ChangeTracker.Clear();

            processed.Outcome = QtsBilling.Persistence.Postgres.SyncOutcome.NeedsReview;
            processed.ErrorCode = exception.Code;

            // The sale that was rolled back never existed: its id must not be reported.
            processed.ServerSaleId = null;

            await db.ProcessedOperations.AddAsync(processed, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return (new SyncOperationReceipt(
                envelope.OperationId,
                SyncOutcome.NeedsReview,
                exception.Code,
                exception.Title,
                null,
                null,
                null,
                DateTimeOffset.UtcNow), null);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            // A concurrent push of the same operation won the race: its outcome is the original one.
            db.ChangeTracker.Clear();

            var winner = await db.ProcessedOperations.AsNoTracking()
                .FirstOrDefaultAsync(
                    operation => operation.BusinessId == businessId
                        && operation.DeviceId == envelope.DeviceId
                        && operation.OperationId == envelope.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (winner is not null)
            {
                return (new SyncOperationReceipt(
                    envelope.OperationId,
                    ToWire(winner.Outcome),
                    winner.ErrorCode,
                    null,
                    winner.ServerSaleId,
                    null,
                    winner.ServerRevision,
                    winner.ProcessedAtUtc), null);
            }

            return (Rejected(envelope.OperationId, SyncOutcome.TransientRetry, ApiErrorCodes.TransientRetry), null);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException)
        {
            // A transient failure: the device keeps the operation in its queue and retries it.
            return (Rejected(envelope.OperationId, SyncOutcome.TransientRetry, ApiErrorCodes.TransientRetry), null);
        }
    }

    /// <summary>Maps a sale error result to the "needs review" outcome, keeping the stable error code.</summary>
    private static async Task<(SyncOperationReceipt Receipt, IResult? Result)> NeedsReviewAsync(
        Guid operationId,
        IResult error,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var (status, code, detail) = await DescribeAsync(error, http, cancellationToken).ConfigureAwait(false);

        return (new SyncOperationReceipt(
            operationId,
            SyncOutcome.NeedsReview,
            code,
            detail,
            null,
            null,
            null,
            DateTimeOffset.UtcNow), null);
    }

    /// <summary>Reads the status and the stable error code out of an <see cref="IResult"/>.</summary>
    private static async Task<(int Status, string Code, string? Detail)> DescribeAsync(
        IResult result,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        // The result is executed against a throwaway context so the real response is untouched; the
        // request services are reused because a ProblemDetails result resolves them while writing.
        var context = new DefaultHttpContext
        {
            RequestServices = http.RequestServices,
        };
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context).ConfigureAwait(false);

        var status = context.Response.StatusCode;
        var code = ApiErrorCodes.ValidationFailed;
        string? detail = null;

        if (context.Response.ContentType?.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            body.Position = 0;

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.GetString() is { } parsed)
                {
                    code = parsed;
                }

                if (document.RootElement.TryGetProperty("detail", out var detailElement))
                {
                    detail = detailElement.GetString();
                }
            }
            catch (JsonException)
            {
                // A body without a readable code still yields a review outcome.
            }
        }

        return (status, code, detail);
    }

    /// <summary>Reads the offline sale payload. The device sends the facts; the server re-prices them.</summary>
    private static bool TryReadSale(JsonElement payload, out SaleCaptureRequest request, out string? error)
    {
        request = null!;
        error = null;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The payload is not an object.";
            return false;
        }

        if (!payload.TryGetProperty("locationId", out var locationElement)
            || !locationElement.TryGetGuid(out var locationId))
        {
            error = "The payload has no location.";
            return false;
        }

        if (!payload.TryGetProperty("registerId", out var registerElement)
            || !registerElement.TryGetGuid(out var registerId))
        {
            error = "The payload has no cash register.";
            return false;
        }

        if (!payload.TryGetProperty("total", out var totalElement) || !totalElement.TryGetDecimal(out var total))
        {
            error = "The payload has no total.";
            return false;
        }

        if (!payload.TryGetProperty("cashReceived", out var cashElement) || !cashElement.TryGetDecimal(out var cashReceived))
        {
            error = "The payload has no cash received.";
            return false;
        }

        if (!payload.TryGetProperty("lines", out var linesElement) || linesElement.ValueKind != JsonValueKind.Array)
        {
            error = "The payload has no lines.";
            return false;
        }

        var lines = new List<SaleLineRequest>();

        foreach (var line in linesElement.EnumerateArray())
        {
            if (!line.TryGetProperty("variantId", out var variantElement) || !variantElement.TryGetGuid(out var variantId)
                || !line.TryGetProperty("quantity", out var quantityElement) || !quantityElement.TryGetDecimal(out var quantity)
                || !line.TryGetProperty("unitPrice", out var priceElement) || !priceElement.TryGetDecimal(out var unitPrice))
            {
                error = "A line is incomplete.";
                return false;
            }

            var taxRate = 0m;
            if (line.TryGetProperty("taxRate", out var taxElement) && taxElement.TryGetDecimal(out var parsedTax))
            {
                taxRate = parsedTax;
            }

            var discount = 0m;
            if (line.TryGetProperty("discount", out var discountElement) && discountElement.TryGetDecimal(out var parsedDiscount))
            {
                discount = parsedDiscount;
            }

            long revision = 0;
            if (line.TryGetProperty("expectedCatalogRevision", out var revisionElement)
                && revisionElement.TryGetInt64(out var parsedRevision))
            {
                revision = parsedRevision;
            }

            lines.Add(new SaleLineRequest(
                variantId,
                quantity,
                unitPrice,
                discount,
                taxRate,
                revision == 0 ? null : revision.ToString(CultureInfo.InvariantCulture)));
        }

        if (lines.Count == 0)
        {
            error = "The payload has no lines.";
            return false;
        }

        string? note = null;
        if (payload.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String)
        {
            note = noteElement.GetString();
        }

        request = new SaleCaptureRequest(
            locationId,
            null,
            string.Empty,
            lines,
            new PaymentCaptureRequest("cash", total, cashReceived, null),
            note,
            registerId);

        return true;
    }

    /// <summary>Maps the stored outcome to the wire outcome the device reads.</summary>
    private static QtsBilling.Contracts.Sync.SyncOutcome ToWire(QtsBilling.Persistence.Postgres.SyncOutcome outcome)
        => outcome switch
        {
            QtsBilling.Persistence.Postgres.SyncOutcome.Posted => SyncOutcome.Posted,
            QtsBilling.Persistence.Postgres.SyncOutcome.AlreadyPosted => SyncOutcome.AlreadyPosted,
            QtsBilling.Persistence.Postgres.SyncOutcome.NeedsReview => SyncOutcome.NeedsReview,
            QtsBilling.Persistence.Postgres.SyncOutcome.NotAuthorized => SyncOutcome.NotAuthorized,
            QtsBilling.Persistence.Postgres.SyncOutcome.DeviceRevoked => SyncOutcome.DeviceRevoked,
            QtsBilling.Persistence.Postgres.SyncOutcome.UnsupportedPayloadVersion => SyncOutcome.UnsupportedPayloadVersion,
            QtsBilling.Persistence.Postgres.SyncOutcome.TransientRetry => SyncOutcome.TransientRetry,
            QtsBilling.Persistence.Postgres.SyncOutcome.SameKeyDifferentPayload => SyncOutcome.SameKeyDifferentPayload,
            _ => SyncOutcome.NeedsReview,
        };

    private static SyncOperationReceipt Rejected(
        Guid operationId,
        SyncOutcome outcome,
        string errorCode)
        => new(operationId, outcome, errorCode, null, null, null, null, DateTimeOffset.UtcNow);
}
