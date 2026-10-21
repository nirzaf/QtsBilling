using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;
using SyncOutcome = QtsBilling.Persistence.Postgres.SyncOutcome;

namespace TestQtsBilling.Integration;

/// <summary>
/// The device lifecycle an owner actually operates (specification task 08, T08.01 - T08.03): inspect, revoke,
/// reassign only against a recorded custody resolution, and retire only once nothing is outstanding. Revocation
/// is never money deletion - what the terminal already submitted stays readable for recovery.
/// </summary>
public sealed class DeviceLifecycleTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public DeviceLifecycleTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T08_01_a_revoked_terminal_is_refused_a_grant_but_its_stored_operations_survive()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var deviceId = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till revoke-survival");
        var operationId = Guid.NewGuid();
        await AddReceiptAsync(deviceId, operationId, "cash_sale", SyncOutcome.Posted);

        var listed = await client.GetFromJsonAsync<List<DeviceInfoDto>>("/api/v1/devices");
        var seen = listed!.Single(row => row.DeviceId == deviceId);
        Assert.Equal("Active", seen.Status);
        Assert.Equal(1, seen.ReceivedOperations);
        Assert.NotNull(seen.LocationName);

        var version = await DeviceVersionAsync(deviceId);
        var revoke = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/revoke",
            new RevokeDeviceRequest(version, "terminal retired from the front counter"));
        var revokeBody = await revoke.Content.ReadAsStringAsync();
        Assert.True(revoke.StatusCode == HttpStatusCode.OK, $"revoke failed: {(int)revoke.StatusCode} {revokeBody}");

        // Grant renewal refuses it from now on.
        var grant = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/offline-grant",
            new { locationId = _fixture.BusinessA.LocationId, registerSessionId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Conflict, grant.StatusCode);

        var detail = await client.GetFromJsonAsync<DeviceInfoDto>($"/api/v1/devices/{deviceId}");
        Assert.Equal("Revoked", detail!.Status);
        Assert.NotNull(detail.RevokedAtUtc);

        // Nothing stored was deleted or rewritten: the receipt is still readable for reconciliation.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var receipt = await db.ProcessedOperations.AsNoTracking().SingleAsync(row => row.DeviceId == deviceId
                && row.OperationId == operationId);
            Assert.Equal(SyncOutcome.Posted, receipt.Outcome);

            Assert.True(await db.AuditEvents.AnyAsync(entry => entry.EntityType == "device_registration"
                && entry.EntityId == deviceId
                && entry.Action == "revoked"));
        });
    }

    [Fact]
    public async Task T08_02_reassignment_is_blocked_until_a_custody_resolution_exists()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var deviceId = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till reassign-blocked");
        var otherLocation = await CreateLocationAsync(client, "Warehouse North", "NORTH");
        await AddReceiptAsync(deviceId, Guid.NewGuid(), "cash_sale", SyncOutcome.NeedsReview);

        var version = await DeviceVersionAsync(deviceId);

        var withoutClearance = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/reassign",
            new ReassignDeviceRequest(version, otherLocation, Guid.NewGuid(), "move it"));
        Assert.Equal(HttpStatusCode.Conflict, withoutClearance.StatusCode);
        Assert.Equal(ApiErrorCodes.ApprovalRequired, CodeOf(await withoutClearance.Content.ReadAsStringAsync()));

        // An acknowledgement may not understate what the server can see.
        var understated = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/reassignment-clearance",
            new IssueReassignmentClearanceRequest(version, AcknowledgedProvisionalCash: 0m, AcknowledgedOperations: 0, "nothing to reconcile"));
        Assert.Equal(HttpStatusCode.Conflict, understated.StatusCode);

        var clearance = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/reassignment-clearance",
            new IssueReassignmentClearanceRequest(version, AcknowledgedProvisionalCash: 45.5m, AcknowledgedOperations: 1, "cash drawer carried forward by manager"));
        var clearanceBody = await clearance.Content.ReadAsStringAsync();
        Assert.True(clearance.StatusCode == HttpStatusCode.Created, $"clearance failed: {(int)clearance.StatusCode} {clearanceBody}");
        var granted = await clearance.Content.ReadFromJsonAsync<DeviceReassignmentClearanceDto>();

        var reassigned = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/reassign",
            new ReassignDeviceRequest(await DeviceVersionAsync(deviceId), otherLocation, granted!.ClearanceId, "moved after reconciliation"));
        var reassignedBody = await reassigned.Content.ReadAsStringAsync();
        Assert.True(reassigned.StatusCode == HttpStatusCode.OK, $"reassign failed: {(int)reassigned.StatusCode} {reassignedBody}");

        var detail = await client.GetFromJsonAsync<DeviceInfoDto>($"/api/v1/devices/{deviceId}");
        Assert.Equal(otherLocation, detail!.LocationId);
        Assert.Equal(45.5m, detail.DeviceHeldProvisionalCash);

        // The clearance is spent: the same acknowledgement cannot authorise a second move.
        var reuse = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/reassign",
            new ReassignDeviceRequest(await DeviceVersionAsync(deviceId), _fixture.BusinessA.LocationId, granted.ClearanceId, "try the same paper twice"));
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            // Stored operations keep the scope they arrived under; reassignment never reinterprets history.
            var receipt = await db.ProcessedOperations.AsNoTracking()
                .SingleAsync(row => row.DeviceId == deviceId && row.Outcome == SyncOutcome.NeedsReview);
            Assert.Equal(_fixture.BusinessA.BusinessId, receipt.BusinessId);

            var consumed = await db.DeviceReassignmentClearances.AsNoTracking().SingleAsync(row => row.Id == granted.ClearanceId);
            Assert.NotNull(consumed.ConsumedAtUtc);
        });
    }

    [Fact]
    public async Task T08_03_a_spent_challenge_or_a_foreign_location_creates_no_device()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        using var otherBusiness = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);

        var code = await CreateChallengeAsync(client, _fixture.BusinessA.LocationId);
        var first = await EnrolWithCodeAsync(client, _fixture.BusinessA.LocationId, "Till single-use", code);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var replay = await EnrolWithCodeAsync(client, _fixture.BusinessA.LocationId, "Till replay", code);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        // A location of another business is invisible and unusable.
        var foreignChallenge = await CreateChallengeAsync(otherBusiness, _fixture.BusinessB.LocationId);
        var crossTenant = await EnrolWithCodeAsync(otherBusiness, _fixture.BusinessA.LocationId, "Till cross tenant", foreignChallenge);
        Assert.True(crossTenant.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden,
            $"a cross-tenant enrolment was not refused: {(int)crossTenant.StatusCode}");

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(1, await db.DeviceRegistrations.CountAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.Name == "Till single-use"));
            Assert.Equal(0, await db.DeviceRegistrations.CountAsync(row => row.Name == "Till replay" || row.Name == "Till cross tenant"));
        });
    }

    [Fact]
    public async Task T08_04_retirement_waits_for_review_and_a_stale_version_is_refused()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var deviceId = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till retire");
        await AddReceiptAsync(deviceId, Guid.NewGuid(), "cash_sale", SyncOutcome.NeedsReview);

        var version = await DeviceVersionAsync(deviceId);

        var stale = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/revoke",
            new RevokeDeviceRequest(version + 7, "stale attempt"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, CodeOf(await stale.Content.ReadAsStringAsync()));

        var retired = await client.PostAsJsonAsync($"/api/v1/devices/{deviceId}/retire",
            new RetireDeviceRequest(version, "hardware replaced"));
        Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);
        Assert.Contains("await review", await retired.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var device = await db.DeviceRegistrations.AsNoTracking().SingleAsync(row => row.Id == deviceId);
            Assert.Null(device.RetiredAtUtc);
            Assert.Equal(DeviceStatus.Active, device.Status);
        });
    }

    // ------------------------------------------------------------------ helpers

    private async Task<long> DeviceVersionAsync(Guid deviceId)
        => await _fixture.WithFreshContextAsync(db => db.DeviceRegistrations.AsNoTracking()
            .Where(row => row.Id == deviceId)
            .Select(row => row.Version)
            .SingleAsync());

    private async Task AddReceiptAsync(Guid deviceId, Guid operationId, string operationType, SyncOutcome outcome)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            db.ProcessedOperations.Add(new ProcessedOperation
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                DeviceId = deviceId,
                OperationId = operationId,
                OperationType = operationType,
                PayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(operationId.ToString("D")))).ToLowerInvariant(),
                Outcome = outcome,
                ProcessedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        });

    private async Task<string> CreateChallengeAsync(HttpClient client, Guid locationId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/devices/enrolment-challenges", new { locationId });
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("enrolmentCode").GetString()!;
    }

    private async Task<HttpResponseMessage> EnrolWithCodeAsync(HttpClient client, Guid locationId, string name, string code)
        => await client.PostAsJsonAsync("/api/v1/devices", new
        {
            name,
            platform = "windows",
            locationId,
            enrolmentCode = code,
            appVersion = "1.0.0",
            schemaVersion = 1,
        });

    private async Task<Guid> EnrolAsync(HttpClient client, Guid locationId, string name)
    {
        var code = await CreateChallengeAsync(client, locationId);
        var response = await EnrolWithCodeAsync(client, locationId, name, code);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"enrolment failed: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("deviceId").GetGuid();
    }

    private async Task<Guid> CreateLocationAsync(HttpClient client, string name, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/locations", new
        {
            name,
            code,
            address = (string?)null,
            timeZone = (string?)null,
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"location creation failed: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static string? CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
