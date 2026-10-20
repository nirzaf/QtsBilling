using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace QtsBilling.Contracts.Identity;

/// <summary>
/// The signed offline capture grant (specification task 23, finding F10): the only authority a device
/// may use while it cannot reach the server. It names one user, device, business, location, register
/// session and currency, carries the permissions frozen at issuance and expires.
/// </summary>
public static class OfflineGrant
{
    public const string Issuer = "qtsbilling";

    public const string Audience = "qtsbilling-offline-capture";

    public const int Version = 1;

    /// <summary>Eight hours, as recommended by the specification.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>The approved upper bound; a request above it is refused, never clamped silently.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    private const string DeviceClaim = "qts:device_id";
    private const string BusinessClaim = "qts:business_id";
    private const string LocationClaim = "qts:location_id";
    private const string RegisterSessionClaim = "qts:register_session_id";
    private const string CurrencyClaim = "qts:currency";
    private const string PermissionsClaim = "qts:permissions";
    private const string AuthorizationRevisionClaim = "qts:auth_revision";

    /// <summary>Signs the grant with the server's private key. The server owns the only private key.</summary>
    public static string Sign(OfflineGrantTicket ticket, RSA privateKey, string keyId)
    {
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = ticket.UserId.ToString(),
            [DeviceClaim] = ticket.DeviceId.ToString(),
            [BusinessClaim] = ticket.BusinessId.ToString(),
            [LocationClaim] = ticket.LocationId.ToString(),
            [CurrencyClaim] = ticket.Currency,
            [PermissionsClaim] = string.Join(' ', ticket.Permissions),
            [AuthorizationRevisionClaim] = ticket.AuthorizationRevision,
            ["ver"] = Version,
        };

        if (ticket.RegisterSessionId is { } registerSessionId)
        {
            claims[RegisterSessionClaim] = registerSessionId.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = ticket.IssuedAt.UtcDateTime,
            NotBefore = ticket.IssuedAt.UtcDateTime,
            Expires = ticket.ExpiresAt.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(privateKey) { KeyId = keyId },
                SecurityAlgorithms.RsaSha256),
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["ver"] = Version,
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// Validates the signature, the issuer/audience/version, the complete device binding, the expiry
    /// and the monotonic clock before the device is allowed to capture anything.
    /// </summary>
    public static OfflineGrantValidation Validate(
        string compact,
        RSA publicKey,
        string expectedKeyId,
        OfflineGrantExpectation expectation,
        DateTimeOffset now,
        DateTimeOffset? lastSeenAt)
    {
        if (lastSeenAt is { } previous && now < previous)
        {
            return OfflineGrantValidation.Refused("clock_rollback");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = new RsaSecurityKey(publicKey) { KeyId = expectedKeyId },
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
                expires is { } expiry
                && now.UtcDateTime < expiry
                && (notBefore is null || now.UtcDateTime >= notBefore.Value),
        };

        JsonWebToken token;

        try
        {
            var result = new JsonWebTokenHandler().ValidateTokenAsync(compact, parameters).GetAwaiter().GetResult();

            if (!result.IsValid)
            {
                return OfflineGrantValidation.Refused("signature_or_lifetime");
            }

            token = (JsonWebToken)result.SecurityToken!;
        }
        catch (SecurityTokenException)
        {
            return OfflineGrantValidation.Refused("signature_or_lifetime");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return OfflineGrantValidation.Refused("malformed");
        }

        if (token.TryGetPayloadValue<int>("ver", out var version) && version != Version)
        {
            return OfflineGrantValidation.Refused("version");
        }

        if (!Guid.TryParse(token.Subject, out var userId)
            || !TryGetGuid(token, DeviceClaim, out var deviceId)
            || !TryGetGuid(token, BusinessClaim, out var businessId)
            || !TryGetGuid(token, LocationClaim, out var locationId))
        {
            return OfflineGrantValidation.Refused("malformed");
        }

        Guid? registerSessionId = null;

        if (token.TryGetPayloadValue<string>(RegisterSessionClaim, out var registerValue))
        {
            if (!Guid.TryParse(registerValue, out var parsed))
            {
                return OfflineGrantValidation.Refused("malformed");
            }

            registerSessionId = parsed;
        }

        if (deviceId != expectation.DeviceId
            || businessId != expectation.BusinessId
            || locationId != expectation.LocationId
            || (expectation.RegisterSessionId is { } expectedRegister && registerSessionId != expectedRegister)
            || (expectation.UserId is { } expectedUser && userId != expectedUser))
        {
            return OfflineGrantValidation.Refused("binding");
        }

        var permissions = token.TryGetPayloadValue<string>(PermissionsClaim, out var raw)
            ? raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        if (expectation.RequiredPermission is { } required
            && !permissions.Contains(required, StringComparer.Ordinal))
        {
            return OfflineGrantValidation.Refused("permission");
        }

        var currency = token.TryGetPayloadValue<string>(CurrencyClaim, out var value) ? value : string.Empty;

        if (expectation.Currency is { } expectedCurrency
            && !string.Equals(currency, expectedCurrency, StringComparison.Ordinal))
        {
            return OfflineGrantValidation.Refused("currency");
        }

        if (!token.TryGetPayloadValue<long>(AuthorizationRevisionClaim, out var revision))
        {
            return OfflineGrantValidation.Refused("malformed");
        }

        return OfflineGrantValidation.Allowed(new OfflineGrantClaims(
            userId,
            deviceId,
            businessId,
            locationId,
            registerSessionId,
            currency,
            permissions,
            revision,
            token.ValidFrom,
            token.ValidTo,
            expectedKeyId));
    }

    /// <summary>The request the device sends to obtain or renew its grant.</summary>
    public sealed record OfflineGrantIssueRequest(Guid? LocationId, Guid? RegisterSessionId);

    /// <summary>The signed grant and the pinned verification key.</summary>
    public sealed record OfflineGrantIssueResponse(
        string Grant,
        string KeyId,
        string PublicKey,
        DateTimeOffset ExpiresAt);

    /// <summary>The server-side shape of one grant; never stored on the device in this form.</summary>
    public sealed record OfflineGrantTicket(
        Guid UserId,
        Guid DeviceId,
        Guid BusinessId,
        Guid LocationId,
        Guid? RegisterSessionId,
        string Currency,
        IReadOnlyList<string> Permissions,
        long AuthorizationRevision,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);

    /// <summary>What the device expects the grant to name, before any capture is allowed.</summary>
    public sealed record OfflineGrantExpectation(
        Guid DeviceId,
        Guid BusinessId,
        Guid LocationId,
        Guid? RegisterSessionId,
        Guid? UserId = null,
        string? Currency = null,
        string? RequiredPermission = null);

    /// <summary>The validated grant the capture gate may use.</summary>
    public sealed record OfflineGrantClaims(
        Guid UserId,
        Guid DeviceId,
        Guid BusinessId,
        Guid LocationId,
        Guid? RegisterSessionId,
        string Currency,
        IReadOnlyList<string> Permissions,
        long AuthorizationRevision,
        DateTime ValidFrom,
        DateTime ValidTo,
        string KeyId);

    /// <summary>An allowed or refused decision with a machine-readable reason.</summary>
    public sealed record OfflineGrantValidation(bool IsAllowed, string? Reason, OfflineGrantClaims? Claims)
    {
        public static OfflineGrantValidation Allowed(OfflineGrantClaims claims) => new(true, null, claims);

        public static OfflineGrantValidation Refused(string reason) => new(false, reason, null);
    }

    private static bool TryGetGuid(JsonWebToken token, string claim, out Guid value)
        => Guid.TryParse(token.TryGetPayloadValue<string>(claim, out var raw) ? raw : null, out value);

    /// <summary>The public key an enrolled device pins at enrolment; the private key never leaves the server.</summary>
    public static string ExportPublicKey(RSA publicKey)
        => Convert.ToBase64String(publicKey.ExportSubjectPublicKeyInfo());

    /// <summary>Stable key identifier: a short SHA-256 thumbprint of the public key.</summary>
    public static string ComputeKeyId(RSA publicKey)
    {
        var hash = SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo());
        return Base64Url(hash.AsSpan(0, 16).ToArray());
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
