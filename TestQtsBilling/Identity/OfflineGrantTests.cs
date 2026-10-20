using System.Security.Cryptography;
using QtsBilling.Contracts.Identity;
using Xunit;

namespace TestQtsBilling.Identity;

/// <summary>The signed offline grant round-trips and refuses every altered copy (task 23).</summary>
public sealed class OfflineGrantTests
{
    [Fact]
    public void A_signed_grant_validates_and_every_altered_copy_is_refused()
    {
        using var signingKey = RSA.Create(2048);
        using var validationKey = RSA.Create();
        validationKey.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(OfflineGrant.ExportPublicKey(signingKey)),
            out _);

        var keyId = OfflineGrant.ComputeKeyId(signingKey);
        var issuedAt = DateTimeOffset.UtcNow;
        var ticket = new OfflineGrant.OfflineGrantTicket(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "QAR",
            [Permissions.SalesPost],
            7,
            issuedAt,
            issuedAt + OfflineGrant.Lifetime);

        var grant = OfflineGrant.Sign(ticket, signingKey, keyId);
        var expectation = new OfflineGrant.OfflineGrantExpectation(
            ticket.DeviceId,
            ticket.BusinessId,
            ticket.LocationId,
            ticket.RegisterSessionId,
            ticket.UserId,
            "QAR",
            Permissions.SalesPost);

        var valid = OfflineGrant.Validate(grant, validationKey, keyId, expectation, issuedAt.AddMinutes(1), null);
        Assert.True(valid.IsAllowed, valid.Reason);
        Assert.Equal(ticket.UserId, valid.Claims!.UserId);
        Assert.Equal(ticket.RegisterSessionId, valid.Claims.RegisterSessionId);

        Assert.False(OfflineGrant.Validate(
            grant,
            validationKey,
            keyId,
            expectation with { DeviceId = Guid.CreateVersion7() },
            issuedAt.AddMinutes(1),
            null).IsAllowed);

        Assert.False(OfflineGrant.Validate(
            grant,
            validationKey,
            keyId,
            expectation,
            issuedAt.AddHours(9),
            null).IsAllowed);

        Assert.False(OfflineGrant.Validate(
            grant,
            validationKey,
            keyId,
            expectation,
            issuedAt.AddMinutes(1),
            issuedAt.AddMinutes(2)).IsAllowed);

        var forged = grant[..^2] + (grant[^2] == 'A' ? 'B' : 'A') + grant[^1];
        Assert.False(OfflineGrant.Validate(
            forged,
            validationKey,
            keyId,
            expectation,
            issuedAt.AddMinutes(1),
            null).IsAllowed);
    }
}
