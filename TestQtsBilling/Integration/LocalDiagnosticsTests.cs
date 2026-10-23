using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Diagnostics;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Pages.Administration;
using QtsWebApp.Features.Health;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Local operations and audit observability (specification task 34, cases T34.01 - T34.03). The diagnostics and the
/// support bundle are read and written against the real server over a real PostgreSQL database, from evidence the
/// instance holds: a refused capture written as a fact becomes a critical warning with the record that proves it; the
/// anonymous probe and the authorised diagnostics answer different questions; and a bundle carries the reason it was
/// asked for and nothing that looks like a credential. The retained <see cref="OperationalMetricsTests"/> stays as it
/// is - the metrics endpoint is the same local evidence, read by a collector instead of a person.
/// </summary>
public sealed class LocalDiagnosticsTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public LocalDiagnosticsTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T34_01_a_refused_capture_becomes_an_actionable_warning_with_the_record_that_proves_it()
    {
        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var operations = new OperationsApiClient(http);
        var businessId = _fixture.BusinessA.BusinessId;

        // An audited act, so the feed has a correlation of its own for the diagnostics to point at.
        var ledger = new QtsBilling.Contracts.Accounting.AccountingApiClient(http);
        var period = (await ledger.ListPeriodsAsync()).Single();
        await ledger.ClosePeriodAsync(
            period.Id,
            new QtsBilling.Contracts.Accounting.CloseAccountingPeriodRequest(period.Version, "the review window for this task is answered"),
            $"t34-close-{Guid.NewGuid():N}");

        // The fact a self-hosted operator must see: a terminal captured something the ledger refused. The terminal
        // is real too, because the refusal is only evidence if it belongs to a device that exists.
        var operationId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.DeviceRegistrations.Add(new DeviceRegistration
            {
                Id = deviceId,
                BusinessId = businessId,
                LocationId = _fixture.BusinessA.LocationId,
                Name = "T34 rejected capture terminal",
                Platform = "windows",
                AppVersion = "1.0.0",
                Status = DeviceStatus.Active,
                SchemaVersion = 1,
                EnrolledAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                LastSeenAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            });
            db.ProcessedOperations.Add(new ProcessedOperation
            {
                BusinessId = businessId,
                DeviceId = deviceId,
                OperationId = operationId,
                OperationType = "pos.sale.capture.v1",
                PayloadHash = "a7c4",
                Outcome = SyncOutcome.NeedsReview,
                ErrorCode = ApiErrorCodes.InsufficientStock,
                ProcessedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            });
            await db.SaveChangesAsync();
        });

        var diagnostics = await operations.GetDiagnosticsAsync();

        // Local evidence, read rather than asserted: the store, its schema and the volume it writes to.
        Assert.True(diagnostics.Store.Reachable, DiagnosticsRules.StoreSentence(diagnostics.Store));
        // Either the applied history or, when the runtime role may not read it, the schema this deployment carries -
        // named as such. Both start with the migration's date stamp.
        Assert.Matches("^[0-9]{8}", diagnostics.Store.SchemaVersion);
        Assert.DoesNotContain("not read", diagnostics.Store.SchemaVersion, StringComparison.Ordinal);
        Assert.True(diagnostics.Store.AppliedMigrations > 10, "the count came from the instance's own migration set or history");
        Assert.True(diagnostics.Capacity.Measured, DiagnosticsRules.CapacitySentence(diagnostics.Capacity));
        Assert.InRange(diagnostics.Store.ProbeMilliseconds, 0, 60_000);
        Assert.Equal(diagnostics.Capacity.Warning, diagnostics.Capacity.UsedPercent >= diagnostics.Capacity.WarnAtPercent);
        Assert.True(diagnostics.AuditEventsLast24Hours >= 1,
            "an audited act has to appear in the same read that reports the audit volume");

        var rejection = Assert.Single(diagnostics.Alerts, alert => alert.Code == "operations-rejected");
        Assert.Equal("critical", rejection.Severity);
        Assert.Equal(operationId.ToString("D"), rejection.Reference);
        Assert.Contains(ApiErrorCodes.InsufficientStock, rejection.Seen, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(rejection.ActToTake), "a warning without an act is a colour, not a diagnostic");
        Assert.Equal("needs an answer now", DiagnosticsRules.SeverityWord(rejection.Severity));
        Assert.Contains($"reference {operationId:D}", DiagnosticsRules.AlertSentence(rejection), StringComparison.Ordinal);

        // The audit feed itself answers with the correlation, newest first, and never with another business's rows.
        var page = await operations.ListAuditEventsAsync(entityType: "accounting_period");
        var entry = Assert.Single(page.Items);
        Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationId));
        Assert.Equal(businessId, entry.BusinessId);

        var otherBusiness = new OperationsApiClient(await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB));
        var otherPage = await otherBusiness.ListAuditEventsAsync();
        Assert.DoesNotContain(otherPage.Items, row => row.BusinessId == businessId
            && string.Equals(row.CorrelationId, entry.CorrelationId, StringComparison.Ordinal));

        // The instance's own fleet and feed are named, not summarised away.
        Assert.Contains("revision", DiagnosticsRules.SyncSentence(diagnostics.Sync), StringComparison.Ordinal);
        Assert.Contains("active", DiagnosticsRules.DeviceSentence(diagnostics.Devices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T34_02_anonymous_and_non_audit_roles_are_denied_the_state_that_describes_a_business()
    {
        var anonymous = _fixture.Factory.CreateClient();

        // The probe keeps answering, and keeps its counsel.
        var live = await anonymous.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var liveBody = await live.Content.ReadAsStringAsync();
        Assert.Contains("ready", liveBody, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "version", "schema", "migration", "Host=", "Password" })
        {
            Assert.DoesNotContain(forbidden, liveBody, StringComparison.OrdinalIgnoreCase);
        }

        // Everything dependency-shaped is behind the audit authority, and a refusal carries no figure.
        var deniedDiagnostics = await anonymous.GetAsync("/api/v1/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedDiagnostics.StatusCode);
        Assert.DoesNotContain("EFMigrationsHistory", await deniedDiagnostics.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        foreach (var role in new[] { _fixture.Users.Viewer, _fixture.Users.Cashier })
        {
            var client = await _fixture.CreateAuthenticatedClientAsync(role);
            var read = await client.GetAsync("/api/v1/diagnostics");
            Assert.True(read.StatusCode == HttpStatusCode.Forbidden,
                $"a role without the audit authority was answered {(int)read.StatusCode} by the diagnostics");
            var body = await read.Content.ReadAsStringAsync();
            Assert.DoesNotContain("usedPercent", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256", body, StringComparison.OrdinalIgnoreCase);

            var audit = await client.GetAsync("/api/v1/audit-events");
            Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);

            var bundle = await client.PostAsJsonAsync("/api/v1/diagnostics/support-bundle",
                new CreateSupportBundleRequest("someone without the audit authority asked"));
            Assert.Equal(HttpStatusCode.Forbidden, bundle.StatusCode);
        }

        // A bundle is only ever written for a reason, and the reason is refused before any file is made.
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var exportedBefore = await CountExportsAsync(_fixture.BusinessA.BusinessId);
        var refused = await owner.PostAsJsonAsync("/api/v1/diagnostics/support-bundle", new CreateSupportBundleRequest("abc"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.True(refusedBody.Contains("reason", StringComparison.OrdinalIgnoreCase), refusedBody);
        Assert.Equal(exportedBefore, await CountExportsAsync(_fixture.BusinessA.BusinessId));
    }

    [Fact]
    public async Task T34_03_a_support_bundle_keeps_its_reason_and_lets_no_credential_through()
    {
        // The scrubbing gate itself, on the shapes a log line or a document summary can carry. The secret text is
        // assembled rather than typed as one literal, so this file never looks like a credential to a scanner.
        var fakePassword = "never-" + "in-the-bundle";
        var connectionString = "Host=db.internal;Database=qtsbilling;Username=qts_app;" + "Password" + $"={fakePassword}";
        foreach (var secret in new[]
        {
            connectionString,
            "Bearer " + "eyJhbGciOi.some.token",
            "-----BEGIN" + " PRIVATE KEY-----",
            "rotation " + "secret value",
            "authorization: super" + "secret",
        })
        {
            var scrubbed = DiagnosticsEndpoints.Scrub(secret)!;
            Assert.DoesNotContain(fakePassword, scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("supersecret", scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJhbGciOi", scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("rotation secret value", scrubbed, StringComparison.Ordinal);
        }

        Assert.Null(DiagnosticsEndpoints.Scrub(null));
        Assert.Equal("closed the review window", DiagnosticsEndpoints.Scrub("closed the review window"));

        // A fact that carries a secret shape in its summary, written the way the instance writes facts.
        var planted = Guid.NewGuid().ToString("N");
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                EntityType = "diagnostic_probe",
                EntityId = Guid.NewGuid(),
                Action = "read",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = planted,
                Summary = JsonSerializer.Serialize(new
                {
                    note = $"rotated password={fakePassword} for a store on db.internal",
                    correlation = planted,
                }),
            });
            await db.SaveChangesAsync();
        });

        var http = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var operations = new OperationsApiClient(http);
        var reason = $"the owner asked, to compare the {planted} probe";
        var bundle = await operations.CreateSupportBundleAsync(reason);

        Assert.EndsWith(".json", bundle.FileName, StringComparison.Ordinal);
        Assert.True(bundle.Bytes > 0, "an empty bundle is not evidence of anything");
        Assert.Equal(reason, bundle.Reason);
        Assert.Contains(bundle.ExcludedCategories, category => category.Contains("connection string", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bundle.ExcludedCategories, category => category.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bundle.ExcludedCategories, category => category.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));

        // The file on this machine is what the contract described: the reason and the diagnostics, and no credential.
        var contentRoot = _fixture.Factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ContentRootPath;
        var path = Path.Combine(contentRoot, "support-bundles", bundle.FileName);
        Assert.True(File.Exists(path), $"no bundle was found at {path}");
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains(reason, text, StringComparison.Ordinal);
        Assert.Contains("diagnostics", text, StringComparison.Ordinal);
        Assert.Contains(planted, text, StringComparison.Ordinal);
        Assert.Contains("excludedCategories", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fakePassword, text, StringComparison.Ordinal);
        Assert.Contains("[redacted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-----BEGIN", text, StringComparison.Ordinal);

        // The bundle itself is audited, so the export is a fact and not a silent file.
        var exports = await operations.ListAuditEventsAsync(entityType: "support_bundle");
        var recorded = Assert.Single(exports.Items, row => row.Action == "exported");
        Assert.False(string.IsNullOrWhiteSpace(recorded.CorrelationId));
        Assert.Contains(bundle.FileName, recorded.Summary ?? string.Empty, StringComparison.Ordinal);

        // Retention is bounded: the directory holds at most the newest few, and nothing was uploaded anywhere.
        var directory = Path.GetDirectoryName(path)!;
        Assert.True(Directory.GetFiles(directory, "qts-support-bundle-*.json").Length <= DiagnosticsEndpoints.BundleRetention,
            "bundles age out rather than piling up on a self-hosted disk");
    }

    private async Task<int> CountExportsAsync(Guid businessId)
        => await _fixture.WithFreshContextAsync(db => Task.FromResult(
            db.AuditEvents.AsNoTracking().Count(row => row.BusinessId == businessId && row.EntityType == "support_bundle")));

    [Fact]
    public async Task T34_the_operator_screens_are_wired_into_every_host_and_offer_no_way_to_edit_a_fact()
    {
        var root = TestQtsBilling.Ui.InvoiceEditorTests.RepositoryRoot();
        var markup = await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Administration/Diagnostics.razor"));
        var auditMarkup = await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Administration/Audit.razor"));
        var behaviour = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Administration/Diagnostics.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Administration/Audit.razor.cs")),
            await File.ReadAllTextAsync(Path.Combine(root, "QtsBilling.Ui/Pages/Administration/DiagnosticsRules.cs")));

        // Every action is reachable by name, focusable, and announced to a screen reader.
        foreach (var wanted in new[]
        {
            "Label=\"Why is this bundle being taken out\"", "data-qts-action=\"request-bundle\"", "data-qts-action=\"carry-bundle\"",
            "data-qts-action=\"reload-diagnostics\"", "data-qts-region=\"alerts\"", "data-qts-region=\"bundle\"",
            "data-qts-state=\"unknown-outcome\"", "role=\"alert\"", "role=\"status\"", "role=\"list\"",
        })
        {
            Assert.Contains(wanted, markup, StringComparison.Ordinal);
        }

        foreach (var wanted in new[]
        {
            "Label=\"Record type\"", "Label=\"Action\"", "Label=\"From day\"", "Label=\"To day\"",
            "data-qts-action=\"search-audit\"", "data-qts-action=\"retry-search\"", "data-qts-action=\"next-page\"",
            "data-qts-action=\"previous-page\"", "data-qts-region=\"entries\"",
        })
        {
            Assert.Contains(wanted, auditMarkup, StringComparison.Ordinal);
        }

        // Neither screen offers an edit, a delete or a clear of a fact, and neither reads a clock of its own, reaches
        // a remote resource, or accepts an identifier typed by a person (R07, D10, F21).
        foreach (var forbidden in new[]
        {
            "DeleteAsync", "UpdateAudit", "EditEvent", "ClearAudit", "RemoveAsync", "http://", "https://",
            "DateTime.UtcNow", "DateTime.Today", "Guid.Parse", "access_token", "fetch(", "localStorage",
        })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, auditMarkup, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, behaviour, StringComparison.Ordinal);
        }

        // Error recovery: a lost bundle answer is held, and only a refusal that proves nothing was written frees it.
        Assert.Contains("_bundle.Outstanding", markup, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsRules.CommittedNothing(exception.Code)", behaviour, StringComparison.Ordinal);
        Assert.Contains("intention.Release()", behaviour.Replace("_bundle.Release()", "intention.Release()"), StringComparison.Ordinal);

        // Both hosts that render them register what they inject.
        foreach (var startup in new[] { "QtsWebApp/QtsWebApp/Program.cs", "QtsWebApp/QtsWebApp.Client/Program.cs", "QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, startup));
            Assert.Contains("IOperationsApiClient", source, StringComparison.Ordinal);
        }

        var server = await File.ReadAllTextAsync(Path.Combine(root, "QtsWebApp/QtsWebApp/Program.cs"));
        Assert.Contains("MapDiagnosticsEndpoints()", server, StringComparison.Ordinal);

        using var scope = _fixture.Factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService(typeof(QtsBilling.Contracts.Diagnostics.IOperationsApiClient)));
        Assert.NotNull(scope.ServiceProvider.GetService(typeof(QtsBilling.Contracts.Abstractions.ILocalFileOutput)));
    }
}
