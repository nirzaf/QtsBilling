using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

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

        return Results.Ok(new OfflineGrant.OfflineGrantIssueResponse(grant, keys.KeyId, keys.PublicKey, expiresAt));
    }

    private static IResult GetGrantKey(OfflineGrantKeyRing keys)
        => Results.Ok(new OfflineGrantKeyResponse(keys.KeyId, keys.PublicKey));

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

    public sealed record EnrolDeviceRequest(string Name, string Platform, Guid LocationId, string EnrolmentCode);

    public sealed record DeviceEnrolmentResponse(Guid DeviceId, DateTimeOffset EnrolledAtUtc);

    public sealed record OfflineGrantKeyResponse(string KeyId, string PublicKey);
}

/// <summary>
/// The RSA key that signs offline grants. In development/test an ephemeral key is generated per host;
/// every other environment fails closed unless the reviewed key file is configured.
/// </summary>
public sealed class OfflineGrantKeyRing : IDisposable
{
    private const string PrivateKeyVariable = "QTS_OFFLINE_GRANT_PRIVATE_KEY_PATH";

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
    /// its lifetime, so the key is loaded once and shared by every host instance in the process.
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

    public void Dispose() => PrivateKey.Dispose();
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
