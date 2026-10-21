using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;
using SyncOutcome = QtsBilling.Persistence.Postgres.SyncOutcome;

namespace QtsWebApp.Features.Devices;

/// <summary>
/// Device enrolment and the signed offline capture grant (specification task 23).
/// </summary>
/// <remarks>
/// A device is enrolled by an owner/manager for one location of the current business. The grant is
/// issued only to a signed-in member with the sales permission, for an active device of the same
/// business; it names the exact user/device/business/location/register session and expires. The private
/// key never leaves the server; the device pins the public key at enrolment and validates the grant
/// itself before it allows an offline capture.
/// </remarks>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/devices").RequireAuthorization();

        group.MapPost("/enrolment-challenges", CreateEnrolmentChallengeAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/", EnrolAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapGet("/", ListDevicesAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapGet("/{deviceId:guid}", GetDeviceAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/{deviceId:guid}/revoke", RevokeDeviceAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/{deviceId:guid}/reassignment-clearance", IssueClearanceAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/{deviceId:guid}/reassign", ReassignDeviceAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/{deviceId:guid}/retire", RetireDeviceAsync).RequireAuthorization(Permissions.DevicesManage);
        group.MapPost("/{deviceId:guid}/offline-grant", IssueOfflineGrantAsync)
            .RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/offline-grant-key", GetGrantKey);

        return app;
    }

    private static async Task<IResult> EnrolAsync(
        EnrolDeviceRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length is < 1 or > 200)
        {
            return Invalid("device_name");
        }

        if (string.IsNullOrWhiteSpace(request.Platform) || request.Platform.Trim().Length > 32)
        {
            return Invalid("device_platform");
        }

        var locationExists = await db.BusinessLocations
            .AsNoTracking()
            .AnyAsync(location => location.BusinessId == businessId && location.Id == request.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (!locationExists)
        {
            return Invalid("device_location");
        }

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return TypedErrors.Forbidden();
        }

        var challengeHash = HashEnrolmentCode(NormalizeEnrolmentCode(request.EnrolmentCode));
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var challenge = await db.DeviceEnrolmentChallenges
                .FromSqlInterpolated($"SELECT * FROM device_enrolment_challenges WHERE challenge_hash = {challengeHash} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (challenge is null
                || challenge.BusinessId != businessId
                || challenge.LocationId != request.LocationId
                || challenge.ConsumedAtUtc is not null
                || challenge.ExpiresAtUtc <= now)
            {
                return Results.Conflict(new { code = "DEVICE_ENROLMENT_CODE_INVALID", detail = "The enrolment code is invalid, expired, or already used." });
            }

            var device = new DeviceRegistration
            {
                BusinessId = businessId,
                LocationId = request.LocationId,
                Name = request.Name.Trim(),
                Platform = request.Platform.Trim(),
                AppVersion = string.IsNullOrWhiteSpace(request.AppVersion) ? null : request.AppVersion.Trim(),
                SchemaVersion = request.SchemaVersion ?? 0,
                Status = DeviceStatus.Active,
                EnrolledAtUtc = now,
            };

            db.DeviceRegistrations.Add(device);
            challenge.ConsumedAtUtc = now;
            challenge.ConsumedByDeviceId = device.Id;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/devices/{device.Id}",
                new DeviceEnrolmentResponse(device.Id, device.EnrolledAtUtc));
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> CreateEnrolmentChallengeAsync(
        CreateEnrolmentChallengeRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var locationExists = await db.BusinessLocations.AsNoTracking()
            .AnyAsync(location => location.BusinessId == businessId && location.Id == request.LocationId && location.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (!locationExists)
        {
            return Invalid("device_location");
        }

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return TypedErrors.Forbidden();
        }

        var code = CreateEnrolmentCode();
        var now = DateTimeOffset.UtcNow;
        db.DeviceEnrolmentChallenges.Add(new DeviceEnrolmentChallenge
        {
            BusinessId = businessId,
            LocationId = request.LocationId,
            ChallengeHash = HashEnrolmentCode(code),
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(15),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new CreateEnrolmentChallengeResponse(code, request.LocationId, now.AddMinutes(15)));
    }

    private static async Task<IResult> IssueOfflineGrantAsync(
        Guid deviceId,
        OfflineGrant.OfflineGrantIssueRequest request,
        HttpContext http,
        BillingDbContext db,
        OfflineGrantKeyRing keys,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var device = await db.DeviceRegistrations
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Results.NotFound(new { code = "device_not_enrolled", detail = "The device is not enrolled in this business." });
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            return Results.Conflict(new { code = "device_revoked", detail = "The device was revoked and can never receive another grant." });
        }

        var locationId = request.LocationId ?? device.LocationId;

        if (locationId is null || locationId != device.LocationId)
        {
            return Invalid("device_location");
        }

        var openSessions =
            from session in db.CashRegisterSessions.AsNoTracking()
            join register in db.CashRegisters.AsNoTracking()
                on new { session.BusinessId, CashRegisterId = session.CashRegisterId }
                equals new { register.BusinessId, CashRegisterId = register.Id }
            where session.BusinessId == businessId
                && register.LocationId == locationId.Value
                && session.Status == CashRegisterSessionStatus.Open
            select session;

        CashRegisterSession? openSession;
        if (request.RegisterSessionId is { } registerSessionId)
        {
            openSession = await openSessions
                .SingleOrDefaultAsync(session => session.Id == registerSessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var locationSessions = await openSessions.Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
            openSession = locationSessions.Count == 1 ? locationSessions[0] : null;
        }

        if (openSession is null)
        {
            return Results.Conflict(new
            {
                code = "REGISTER_SESSION_REQUIRED",
                detail = "An open cash drawer session for this device location is required to issue an offline grant.",
            });
        }

        var membership = await db.BusinessMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.BusinessId == businessId && row.UserId == userId && row.IsActive,
                cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return TypedErrors.Forbidden();
        }

        var business = await db.Businesses
            .AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt + OfflineGrant.Lifetime;

        var ticket = new OfflineGrant.OfflineGrantTicket(
            userId,
            device.Id,
            businessId,
            locationId.Value,
            openSession.Id,
            business.BaseCurrency,
            RolePermissions.For(membership.Role),
            membership.AuthorizationRevision,
            issuedAt,
            expiresAt);

        var grant = keys.Sign(ticket);

        // A grant request is contact: the owner list has to show when the terminal last spoke before anybody
        // revokes or retires it. The app and schema version travel with the enrolment request.
        device.LastSeenAtUtc = issuedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new OfflineGrant.OfflineGrantIssueResponse(grant, keys.KeyId, keys.PublicKey, expiresAt));
    }

    private static IResult GetGrantKey(OfflineGrantKeyRing keys)
        => Results.Ok(new OfflineGrantKeyResponse(keys.KeyId, keys.PublicKey));

    // ------------------------------------------------------------------ lifecycle (specification task 08)

    private static async Task<IResult> ListDevicesAsync(
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId))
        {
            return TypedErrors.Unauthenticated();
        }

        var devices = await db.DeviceRegistrations.AsNoTracking()
            .Where(device => device.BusinessId == businessId)
            .OrderBy(device => device.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(await ProjectAsync(db, businessId, devices, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetDeviceAsync(
        Guid deviceId,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId))
        {
            return TypedErrors.Unauthenticated();
        }

        var device = await db.DeviceRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        return device is null
            ? NotFoundDevice()
            : Results.Ok((await ProjectAsync(db, businessId, [device], cancellationToken).ConfigureAwait(false))[0]);
    }

    /// <summary>
    /// Adds what an operator needs to see before touching a terminal: where it belongs, when it last spoke,
    /// how much it still holds on record, and how many of its operations await review. The provisional cash
    /// figure is the most recent manager acknowledgement for that device, because the server cannot count
    /// money that has not been pushed yet (specification D07).
    /// </summary>
    private static async Task<List<DeviceInfoDto>> ProjectAsync(
        BillingDbContext db,
        Guid businessId,
        IReadOnlyList<DeviceRegistration> devices,
        CancellationToken cancellationToken)
    {
        var ids = devices.Select(device => device.Id).ToArray();

        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(location => location.BusinessId == businessId)
            .Select(location => new { location.Id, location.Name })
            .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken)
            .ConfigureAwait(false);

        var received = await db.ProcessedOperations.AsNoTracking()
            .Where(op => op.BusinessId == businessId && ids.Contains(op.DeviceId))
            .GroupBy(op => op.DeviceId)
            .Select(group => new { DeviceId = group.Key, Total = group.Count(), Review = group.Count(op => op.Outcome == SyncOutcome.NeedsReview) })
            .ToDictionaryAsync(row => row.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        var acknowledgements = await db.DeviceReassignmentClearances.AsNoTracking()
            .Where(clearance => clearance.BusinessId == businessId && ids.Contains(clearance.DeviceId))
            .OrderByDescending(clearance => clearance.OccurredAtUtc)
            .Select(clearance => new { clearance.DeviceId, clearance.AcknowledgedProvisionalCash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. devices.Select(device =>
        {
            received.TryGetValue(device.Id, out var counts);
            var acknowledged = acknowledgements
                .Where(clearance => clearance.DeviceId == device.Id)
                .Select(clearance => (decimal?)clearance.AcknowledgedProvisionalCash)
                .FirstOrDefault();

            return new DeviceInfoDto(
                device.Id,
                device.Name,
                device.Platform,
                device.Status.ToString(),
                device.LocationId,
                device.LocationId is { } locationId && locations.TryGetValue(locationId, out var locationName) ? locationName : null,
                device.AppVersion,
                device.SchemaVersion,
                device.EnrolledAtUtc,
                device.LastSeenAtUtc,
                device.RevokedAtUtc,
                device.RetiredAtUtc,
                null,
                counts?.Total ?? 0,
                counts?.Review ?? 0,
                acknowledged ?? 0m);
        })];
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        RevokeDeviceRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return Invalid("a revocation reason of 1-500 characters is required");
        }

        var device = await LoadDeviceAsync(db, businessId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NotFoundDevice();
        }

        if (request.ExpectedVersion != device.Version)
        {
            return StaleVersion(device.Version);
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            // Revoking twice is not an event; the first one already carries the reason.
            return Results.Ok(new { status = device.Status.ToString(), device.RevokedAtUtc });
        }

        var now = DateTimeOffset.UtcNow;
        device.Status = DeviceStatus.Revoked;
        device.RevokedAtUtc = now;

        db.AuditEvents.Add(DeviceAudit(businessId, userId, device.Id, "revoked", now, http.TraceIdentifier, new { reason }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Nothing stored is discarded: the terminal's receipts stay readable so its provisional cash can
        // still be reconciled, and its own queue survives for recovery (specification D07).
        return Results.Ok(new DeviceLifecycleResponse(device.Id, device.Status.ToString(), device.RevokedAtUtc, device.Version));
    }

    private static async Task<IResult> IssueClearanceAsync(
        Guid deviceId,
        IssueReassignmentClearanceRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return Invalid("state what was reconciled or what the terminal still holds (1-500 characters)");
        }

        if (request.AcknowledgedProvisionalCash < 0m || request.AcknowledgedOperations < 0)
        {
            return Invalid("the acknowledged amount and operation count cannot be negative");
        }

        var device = await LoadDeviceAsync(db, businessId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NotFoundDevice();
        }

        if (request.ExpectedVersion != device.Version)
        {
            return StaleVersion(device.Version);
        }

        var review = await db.ProcessedOperations.CountAsync(op => op.BusinessId == businessId
            && op.DeviceId == deviceId
            && op.Outcome == SyncOutcome.NeedsReview, cancellationToken).ConfigureAwait(false);

        if (request.AcknowledgedOperations < review)
        {
            return Results.Problem(
                title: $"This terminal has {review} operation(s) awaiting review.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ApiErrorCodes.ApprovalRequired,
                    ["detail"] = "Acknowledge at least the outstanding operations; an acknowledgement cannot understate what the server can see.",
                });
        }

        var now = DateTimeOffset.UtcNow;
        var clearance = new DeviceReassignmentClearance
        {
            BusinessId = businessId,
            DeviceId = deviceId,
            AcknowledgedProvisionalCash = request.AcknowledgedProvisionalCash,
            AcknowledgedOperations = request.AcknowledgedOperations,
            Reason = reason,
            ClearedByUserId = userId,
            OccurredAtUtc = now,
            ExpiresAtUtc = now.AddHours(24),
            Version = 1,
        };

        db.DeviceReassignmentClearances.Add(clearance);
        db.AuditEvents.Add(DeviceAudit(businessId, userId, deviceId, "reassignment_cleared", now, http.TraceIdentifier, new
        {
            clearanceId = clearance.Id,
            cash = clearance.AcknowledgedProvisionalCash,
            operations = clearance.AcknowledgedOperations,
            reason,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/v1/devices/{deviceId}/reassignment-clearance", new DeviceReassignmentClearanceDto(
            clearance.Id,
            clearance.DeviceId,
            clearance.AcknowledgedProvisionalCash,
            clearance.AcknowledgedOperations,
            clearance.OccurredAtUtc,
            clearance.ExpiresAtUtc));
    }

    private static async Task<IResult> ReassignDeviceAsync(
        Guid deviceId,
        ReassignDeviceRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return Invalid("a reassignment reason of 1-500 characters is required");
        }

        var device = await LoadDeviceAsync(db, businessId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NotFoundDevice();
        }

        if (request.ExpectedVersion != device.Version)
        {
            return StaleVersion(device.Version);
        }

        if (device.Status != DeviceStatus.Active || device.RetiredAtUtc is not null)
        {
            return Results.Problem(
                title: "Only an active terminal can be reassigned.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.Conflict });
        }

        var target = await db.BusinessLocations.AsNoTracking()
            .SingleOrDefaultAsync(location => location.BusinessId == businessId && location.Id == request.LocationId && location.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return Invalid("choose an active branch of this business");
        }

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return TypedErrors.Forbidden();
        }

        var review = await db.ProcessedOperations.CountAsync(op => op.BusinessId == businessId
            && op.DeviceId == deviceId
            && op.Outcome == SyncOutcome.NeedsReview, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // A clearance is mandatory whenever the terminal still owes money or has operations outstanding, and it
        // is spent atomically so one acknowledgement cannot authorise two moves.
        if (review > 0 || device.LocationId is not null)
        {
            var consumed = await db.DeviceReassignmentClearances
                .Where(clearance => clearance.Id == request.ClearanceId
                    && clearance.BusinessId == businessId
                    && clearance.DeviceId == deviceId
                    && clearance.ConsumedAtUtc == null
                    && clearance.ExpiresAtUtc > now
                    && clearance.AcknowledgedOperations >= review)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(clearance => clearance.ConsumedAtUtc, now)
                    .SetProperty(clearance => clearance.ConsumedByReassignmentDeviceId, deviceId)
                    .SetProperty(clearance => clearance.Version, clearance => clearance.Version + 1)
                    .SetProperty(clearance => clearance.UpdatedAtUtc, now))
                .ConfigureAwait(false);

            if (consumed != 1)
            {
                return Results.Problem(
                    title: "The reassignment needs a current custody resolution for this terminal.",
                    detail: review > 0
                        ? $"A manager must acknowledge at least {review} outstanding operation(s) and any provisional cash before the terminal can move."
                        : "Record a reassignment clearance first; it must be unconsumed and unexpired.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.ApprovalRequired });
            }
        }

        var previous = device.LocationId;
        device.LocationId = request.LocationId;

        db.AuditEvents.Add(DeviceAudit(businessId, userId, deviceId, "reassigned", now, http.TraceIdentifier, new
        {
            fromLocationId = previous,
            toLocationId = request.LocationId,
            clearanceId = request.ClearanceId,
            reason,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Stored operations keep the location they were captured under: reassigning a terminal never
        // reinterprets history (specification D06/D07).
        return Results.Ok(new DeviceLifecycleResponse(device.Id, device.Status.ToString(), device.RevokedAtUtc, device.Version));
    }

    private static async Task<IResult> RetireDeviceAsync(
        Guid deviceId,
        RetireDeviceRequest request,
        HttpContext http,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!http.User.TryGetBusinessId(out var businessId) || !TryGetUserId(http, out var userId))
        {
            return TypedErrors.Unauthenticated();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
        {
            return Invalid("a retirement reason of 1-500 characters is required");
        }

        var device = await LoadDeviceAsync(db, businessId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NotFoundDevice();
        }

        if (request.ExpectedVersion != device.Version)
        {
            return StaleVersion(device.Version);
        }

        if (device.RetiredAtUtc is not null)
        {
            return Results.Ok(new DeviceLifecycleResponse(device.Id, device.Status.ToString(), device.RevokedAtUtc, device.Version));
        }

        var outstanding = await db.ProcessedOperations.CountAsync(op => op.BusinessId == businessId
            && op.DeviceId == deviceId
            && op.Outcome == SyncOutcome.NeedsReview, cancellationToken).ConfigureAwait(false);

        if (outstanding > 0)
        {
            return Results.Problem(
                title: $"{outstanding} operation(s) on this terminal still await review.",
                detail: "Retirement waits for reconciliation; a terminal is never retired to make outstanding money disappear.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.ApprovalRequired });
        }

        var now = DateTimeOffset.UtcNow;
        device.RetiredAtUtc = now;
        device.Status = DeviceStatus.Revoked;
        device.RevokedAtUtc ??= now;

        db.AuditEvents.Add(DeviceAudit(businessId, userId, deviceId, "retired", now, http.TraceIdentifier, new { reason }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The identity and its records are retained. Wiping the terminal's local key and data is a separate,
        // approved on-device action after export, and is proven natively at task 38 - not a side effect of this call.
        return Results.Ok(new DeviceLifecycleResponse(device.Id, device.Status.ToString(), device.RevokedAtUtc, device.Version));
    }

    private static async Task<DeviceRegistration?> LoadDeviceAsync(
        BillingDbContext db,
        Guid businessId,
        Guid deviceId,
        CancellationToken cancellationToken)
        => await db.DeviceRegistrations
            .SingleOrDefaultAsync(device => device.BusinessId == businessId && device.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

    private static AuditEvent DeviceAudit(
        Guid businessId,
        Guid userId,
        Guid deviceId,
        string action,
        DateTimeOffset now,
        string correlationId,
        object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            DeviceId = deviceId,
            EntityType = "device_registration",
            EntityId = deviceId,
            Action = action,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        };

    private static IResult NotFoundDevice() => Results.Problem(
        title: "This device is not registered in the current business.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.NotFound });

    private static IResult StaleVersion(long storedVersion) => Results.Problem(
        title: "Someone changed this device record first.",
        detail: $"The stored version is {storedVersion}. Reload the device list and repeat the change you still intend.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.OptimisticConcurrencyFailure });

    public sealed record DeviceLifecycleResponse(Guid DeviceId, string Status, DateTimeOffset? RevokedAtUtc, long Version);

    private static IResult Invalid(string detail)
        => Results.BadRequest(new { code = "invalid_request", detail });

    private static bool TryGetUserId(HttpContext http, out Guid userId)
        => Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static string NormalizeEnrolmentCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string HashEnrolmentCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string CreateEnrolmentCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = new char[12];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = alphabet[bytes[index] & 31];
        }

        var token = new string(chars);
        return $"QTS-{token[..4]}-{token[4..8]}-{token[8..12]}";
    }

    public sealed record CreateEnrolmentChallengeRequest(Guid LocationId);

    public sealed record CreateEnrolmentChallengeResponse(string EnrolmentCode, Guid LocationId, DateTimeOffset ExpiresAtUtc);

    public sealed record EnrolDeviceRequest(string Name, string Platform, Guid LocationId, string EnrolmentCode, string? AppVersion = null, int? SchemaVersion = null);

    public sealed record DeviceEnrolmentResponse(Guid DeviceId, DateTimeOffset EnrolledAtUtc);

    public sealed record OfflineGrantKeyResponse(string KeyId, string PublicKey);
}

/// <summary>
/// The RSA key that signs offline grants. In development/test an ephemeral key is generated per host;
/// every other environment fails closed unless the reviewed key file is configured.
/// </summary>
public sealed class OfflineGrantKeyRing
{
    /// <summary>Configuration key naming the reviewed offline-grant signing key; the startup policy checks it.</summary>
    public const string PrivateKeyVariable = "QTS_OFFLINE_GRANT_PRIVATE_KEY_PATH";

    private static readonly object CreationGate = new();
    private static OfflineGrantKeyRing? _shared;

    private OfflineGrantKeyRing(RSA privateKey)
    {
        PrivateKey = privateKey;
        KeyId = OfflineGrant.ComputeKeyId(privateKey);
        PublicKey = OfflineGrant.ExportPublicKey(privateKey);
    }

    /// <summary>
    /// One signing key per process. A host restart must never invalidate a grant that is still inside
    /// its lifetime, so the key is loaded once and shared by every host instance in the process. The type is
    /// deliberately not <see cref="IDisposable"/>: a container that disposes one host would otherwise destroy the
    /// key the next host, or a grant still being verified, depends on (task 09 review).
    /// </summary>
    public static OfflineGrantKeyRing Shared(IConfiguration configuration, IWebHostEnvironment environment)
    {
        lock (CreationGate)
        {
            return _shared ??= Load(configuration, environment);
        }
    }

    public RSA PrivateKey { get; }

    public string KeyId { get; }

    public string PublicKey { get; }

    private readonly object _signingGate = new();

    /// <summary>RSA instances are not safe for concurrent use; the host is.</summary>
    public string Sign(OfflineGrant.OfflineGrantTicket ticket)
    {
        lock (_signingGate)
        {
            return OfflineGrant.Sign(ticket, PrivateKey, KeyId);
        }
    }

    public static OfflineGrantKeyRing Load(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = configuration[PrivateKeyVariable];

        if (!string.IsNullOrWhiteSpace(path))
        {
            var key = RSA.Create();
            key.ImportFromPem(File.ReadAllText(path));
            return new OfflineGrantKeyRing(key);
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return new OfflineGrantKeyRing(RSA.Create(2048));
        }

        throw new InvalidOperationException(
            $"{PrivateKeyVariable} must name the reviewed offline-grant private key outside development.");
    }
}

/// <summary>The typed errors the endpoints return without touching the exception middleware.</summary>
internal static class TypedErrors
{
    public static IResult Unauthenticated()
        => Results.Problem(
            title: "Authentication is required.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["code"] = "unauthenticated" });

    public static IResult Forbidden()
        => Results.Problem(
            title: "The signed-in user may not perform this action.",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = "forbidden" });
}
