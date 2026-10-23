using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Audit;
using QtsBilling.Contracts.Diagnostics;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Health;

/// <summary>
/// Local operator diagnostics (specification task 34). The anonymous <c>/api/v1/health</c> route answers the probe
/// question - is the store reachable - and nothing else; everything an owner uses to diagnose the instance lives here,
/// behind the audit authority, and is assembled only from evidence this instance already holds: its own store, its own
/// volume, the backup manifests on its own disk, its own terminals, its own change feed and the configured signing
/// certificate. A support bundle is written here, on request, with a reason, and never sent anywhere.
/// </summary>
/// <remarks>
/// Nothing in this type edits or deletes an audit row, a log, or a backup: a diagnostic warning is answered by a
/// person, not by clearing the fact that produced it (task 34, definition of done and recovery note).
/// </remarks>
public static class DiagnosticsEndpoints
{
    /// <summary>Where the newest backup manifest is looked for. Empty means the instance says it is not configured.</summary>
    public const string BackupDirectoryVariable = "QTS_DIAGNOSTICS_BACKUP_DIRECTORY";

    /// <summary>The volume whose free space is reported. Falls back to this process's own directory.</summary>
    public const string CapacityPathVariable = "QTS_DIAGNOSTICS_CAPACITY_PATH";

    /// <summary>How long a terminal may be silent before the owner is told.</summary>
    public const string DeviceSilenceVariable = "QTS_DIAGNOSTICS_DEVICE_SILENT_HOURS";

    /// <summary>How old a backup may be before the freshness warning fires.</summary>
    public const string BackupAgeVariable = "QTS_DIAGNOSTICS_BACKUP_STALE_AFTER_HOURS";

    /// <summary>Where a requested support bundle is written. Defaults inside the content root.</summary>
    public const string BundleDirectoryVariable = "QTS_DIAGNOSTICS_BUNDLE_DIRECTORY";

    /// <summary>Bundles kept on disk. The newest is the one a person carries out; older ones age out.</summary>
    public const int BundleRetention = 10;

    private const int WarnAtUsedPercent = 90;
    private const int WarnBeforeCertificateDays = 30;
    private const int MaxReasonLength = 500;

    private static readonly string[] ExcludedCategories =
    [
        "connection strings and any password inside them",
        "signing keys, certificates and their private material",
        "bearer tokens, refresh tokens and enrolment codes",
        "raw customer documents, invoice payloads or contact details",
        "anything a log sink could not write - never replaced by a claim of success",
    ];

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var diagnostics = app.MapGroup("/api/v1/diagnostics").WithTags("Diagnostics");
        diagnostics.MapGet("/", ReadAsync).RequireAuthorization(Permissions.AuditView);
        diagnostics.MapPost("/support-bundle", CreateSupportBundleAsync).RequireAuthorization(Permissions.AuditView);

        return app;
    }

    private static async Task<IResult> ReadAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var store = await ProbeStoreAsync(db, cancellationToken).ConfigureAwait(false);
        var capacity = MeasureCapacity(configuration, environment);
        var backup = ReadBackupEvidence(configuration, environment);
        var devices = await ReadDevicesAsync(db, businessId, configuration, cancellationToken).ConfigureAwait(false);
        var sync = await ReadSyncAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var key = ReadKeyExpiry(configuration);
        var auditCount = await db.AuditEvents.AsNoTracking()
            .CountAsync(entry => entry.BusinessId == businessId
                && entry.OccurredAtUtc >= DateTimeOffset.UtcNow.AddHours(-24), cancellationToken)
            .ConfigureAwait(false);
        var newestCorrelation = await db.AuditEvents.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.CorrelationId != null)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Select(entry => entry.CorrelationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var alerts = BuildAlerts(store, capacity, backup, devices, sync, key, newestCorrelation);

        return Results.Ok(new DiagnosticsDto(store, capacity, backup, devices, sync, key, auditCount, alerts));
    }

    private static async Task<IResult> CreateSupportBundleAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        HttpContext http,
        CreateSupportBundleRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request?.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem("The bundle needs a reason.", new Dictionary<string, string[]>
            {
                ["reason"] = [$"Say why the bundle is being taken out, in 5 to {MaxReasonLength} characters. It is written into the file and into the audit."],
            });
        }

        var directory = ResolveDirectory(configuration, environment, BundleDirectoryVariable, "support-bundles");
        var generatedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The sink failed: say so. A bundle that was not written is never reported as written.
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The support bundle could not be written.",
                $"This instance could not open its bundle directory ({exception.GetType().Name}). Nothing was claimed, and nothing was uploaded.");
        }

        var diagnostics = await BuildDiagnosticsAsync(db, businessId, configuration, environment, cancellationToken).ConfigureAwait(false);
        var recentAudit = await db.AuditEvents.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(200)
            .Select(entry => new AuditEventDto(
                entry.Id, entry.BusinessId, entry.UserId, entry.DeviceId, entry.EntityType, entry.EntityId,
                entry.Action, entry.OccurredAtUtc, entry.CorrelationId, entry.Summary))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var document = new Dictionary<string, object?>
        {
            ["generatedAtUtc"] = generatedAtUtc,
            ["reason"] = reason,
            ["requestedByUserId"] = userId,
            ["correlationId"] = http.TraceIdentifier,
            ["whatThisBundleIs"] = "A local diagnostic export. Nothing in it left this instance, and no service collects it.",
            ["excludedCategories"] = ExcludedCategories,
            ["diagnostics"] = diagnostics,
            ["recentAudit"] = recentAudit.Select(entry => entry with
            {
                // The summary is a bounded, non-sensitive field already, but a support bundle is read by someone
                // who does not hold the audit authority, so anything shaped like a secret is scrubbed again here.
                Summary = Scrub(entry.Summary),
            }).ToList(),
        };

        var json = Serialize(document);
        var fileName = $"qts-support-bundle-{generatedAtUtc:yyyyMMdd-HHmmss}-{http.TraceIdentifier[..Math.Min(8, http.TraceIdentifier.Length)]}.json";
        var path = Path.Combine(directory, fileName);

        try
        {
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            TrimBundles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The support bundle could not be written.",
                $"The directory refused the write ({exception.GetType().Name}); no file is claimed.");
        }

        db.AuditEvents.Add(new AuditEvent
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = "support_bundle",
            EntityId = Guid.NewGuid(),
            Action = "exported",
            OccurredAtUtc = generatedAtUtc,
            CorrelationId = http.TraceIdentifier,
            Summary = JsonSerializer.Serialize(new { fileName, bytes = new FileInfo(path).Length, reason }),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var bytes = new FileInfo(path).Length;
        return Results.Ok(new SupportBundleDto(
            fileName, bytes, generatedAtUtc, reason,
            ["diagnostics", "recent audit entries (summaries scrubbed)", "the reason and correlation of this request"],
            ExcludedCategories));
    }

    // ------------------------------------------------------------------ evidence

    private static async Task<DiagnosticsDto> BuildDiagnosticsAsync(
        BillingDbContext db,
        Guid businessId,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var store = await ProbeStoreAsync(db, cancellationToken).ConfigureAwait(false);
        var capacity = MeasureCapacity(configuration, environment);
        var backup = ReadBackupEvidence(configuration, environment);
        var devices = await ReadDevicesAsync(db, businessId, configuration, cancellationToken).ConfigureAwait(false);
        var sync = await ReadSyncAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var key = ReadKeyExpiry(configuration);
        var newestCorrelation = await db.AuditEvents.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.CorrelationId != null)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Select(entry => entry.CorrelationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var auditCount = await db.AuditEvents.AsNoTracking()
            .CountAsync(entry => entry.BusinessId == businessId
                && entry.OccurredAtUtc >= DateTimeOffset.UtcNow.AddHours(-24), cancellationToken)
            .ConfigureAwait(false);

        return new DiagnosticsDto(
            store, capacity, backup, devices, sync, key, auditCount,
            BuildAlerts(store, capacity, backup, devices, sync, key, newestCorrelation));
    }

    private static async Task<StoreDiagnosticsDto> ProbeStoreAsync(BillingDbContext db, CancellationToken cancellationToken)
    {
        var probe = Stopwatch.GetTimestamp();
        var reachable = false;

        // Reachability is its own fact. A metadata read the database role is not allowed to make must never be
        // reported as an unreachable store, because that sends an operator hunting the wrong thing.
        try
        {
            reachable = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbException)
        {
            reachable = false;
        }

        var databaseVersion = "not read";
        var deployed = DeployedSchemaVersion(db);
        var schemaVersion = deployed;
        var applied = DeployedMigrationCount(db);

        if (reachable)
        {
            try
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select version();";
                    databaseVersion = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? "not read";
                }

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select coalesce(max(migration_id), 'none'), count(*) from \"__EFMigrationsHistory\";";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        schemaVersion = Convert.ToString(reader.GetValue(0)) ?? "none";
                        applied = Convert.ToInt32(reader.GetValue(1));
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or DbException)
            {
                // The applied history is a migrator-role fact. The instance still knows which schema it was built
                // with, so that is what is reported - named as the deployed schema rather than passed off as the
                // applied one.
                databaseVersion = "the store answered, but this role may not read its version";
                schemaVersion = $"{deployed} (deployed; the applied history is unreadable by this role)";
            }
        }
        else
        {
            databaseVersion = "not read: the store did not answer";
            schemaVersion = $"{deployed} (deployed; the store did not answer)";
        }

        return new StoreDiagnosticsDto(
            reachable, (long)Stopwatch.GetElapsedTime(probe).TotalMilliseconds,
            databaseVersion, schemaVersion, applied, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The migrations this deployed assembly carries, read from the code rather than the database: the applied
    /// history is a migrator-role fact and the runtime role legitimately cannot see it.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> DeployedMigrations = new(() =>
    {
        var ids = new List<string>();
        foreach (var type in typeof(BillingDbContext).Assembly.GetTypes())
        {
            if (type.BaseType?.Name != "Migration")
            {
                continue;
            }

            // The migration id is the [Migration("...")] argument, not the class name: a hand-named migration class
            // still carries its timestamped id, and the id is what an operator reads back from the history table.
            var attribute = type.GetCustomAttributesData()
                .FirstOrDefault(data => data.AttributeType.FullName == "Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute");
            if (attribute?.ConstructorArguments.Count > 0 && attribute.ConstructorArguments[0].Value is string id && id.Length > 0)
            {
                ids.Add(id);
            }
        }

        ids.Sort(StringComparer.Ordinal);
        ids.Reverse();
        return ids;
    });

    private static string DeployedSchemaVersion(BillingDbContext db)
        => DeployedMigrations.Value.Count == 0 ? "none" : DeployedMigrations.Value[0];

    private static int DeployedMigrationCount(BillingDbContext db) => DeployedMigrations.Value.Count;

    private static CapacityDiagnosticsDto MeasureCapacity(IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration[CapacityPathVariable];
        if (string.IsNullOrWhiteSpace(path))
        {
            path = environment.ContentRootPath;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            var drive = new DriveInfo(string.IsNullOrWhiteSpace(root) ? path : root);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            var percent = drive.TotalSize > 0 ? used * 100d / drive.TotalSize : 0d;
            return new CapacityDiagnosticsDto(
                path, drive.TotalSize, drive.TotalFreeSpace, Math.Round(percent, 2), WarnAtUsedPercent,
                percent >= WarnAtUsedPercent, true);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The volume could not be measured. The screen says so rather than showing a healthy-looking zero.
            return new CapacityDiagnosticsDto(path, 0, 0, 0, WarnAtUsedPercent, false, false);
        }
    }

    private static BackupEvidenceDto ReadBackupEvidence(IConfiguration configuration, IHostEnvironment environment)
    {
        var staleAfter = ReadInteger(configuration, BackupAgeVariable, 26);
        var directory = ResolveDirectory(configuration, environment, BackupDirectoryVariable, "backups");
        var restoreNote = "no restore verification record was found in this directory";

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new BackupEvidenceDto(false, directory, null, null, null, null, null, staleAfter, false,
                "the backup directory is not configured or not present, so no freshness can be proved");
        }

        var restoreMarker = Directory.GetFiles(directory, "*.restore-verified.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        var newest = Directory.GetFiles(directory, "*.dump.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

        if (newest is null)
        {
            return new BackupEvidenceDto(true, directory, null, null, null, null, null, staleAfter, true,
                restoreMarker is null ? restoreNote : $"restored and verified {Path.GetFileName(restoreMarker)}");
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(newest));
            var root = manifest.RootElement;
            var createdAt = root.TryGetProperty("createdAtUtc", out var stamp) && DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            var size = root.TryGetProperty("sizeBytes", out var bytes) && bytes.TryGetInt64(out var length) ? length : (long?)null;
            var hash = root.TryGetProperty("sha256", out var digest) ? digest.GetString() : null;
            var age = createdAt is { } at ? Math.Round((DateTimeOffset.UtcNow - at).TotalHours, 1) : (double?)null;
            var stale = age is { } hours && hours > staleAfter;

            if (restoreMarker is not null)
            {
                restoreNote = $"restored and verified by {Path.GetFileName(restoreMarker)}";
            }

            return new BackupEvidenceDto(
                true, directory, Path.GetFileName(newest), createdAt, size, hash, age, staleAfter, stale, restoreNote);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new BackupEvidenceDto(true, directory, Path.GetFileName(newest), null, null, null, null, staleAfter, true,
                $"the newest manifest could not be read ({exception.GetType().Name})");
        }
    }

    private static async Task<DeviceFleetDiagnosticsDto> ReadDevicesAsync(
        BillingDbContext db, Guid businessId, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var silentAfter = ReadInteger(configuration, DeviceSilenceVariable, 24);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-silentAfter);
        var devices = await db.DeviceRegistrations.AsNoTracking()
            .Where(device => device.BusinessId == businessId)
            .Select(device => new
            {
                device.Name,
                device.Status,
                device.RetiredAtUtc,
                device.LastSeenAtUtc,
                device.SchemaVersion,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var active = devices.Where(device => device.Status == DeviceStatus.Active && device.RetiredAtUtc is null).ToList();
        var newestSchema = devices.Count == 0 ? 0 : devices.Max(device => device.SchemaVersion);
        var silent = active.Where(device => device.LastSeenAtUtc is null || device.LastSeenAtUtc < cutoff).ToList();

        return new DeviceFleetDiagnosticsDto(
            active.Count,
            devices.Count(device => device.Status == DeviceStatus.Revoked),
            devices.Count(device => device.RetiredAtUtc is not null),
            silent.Count,
            silentAfter,
            [.. silent.Select(device => device.Name).OrderBy(name => name, StringComparer.Ordinal).Take(10)],
            devices.Count(device => device.Status == DeviceStatus.Active && device.SchemaVersion < newestSchema),
            newestSchema);
    }

    private static async Task<SyncDiagnosticsDto> ReadSyncAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
    {
        var revision = await db.SyncRevisions.AsNoTracking()
            .Where(row => row.BusinessId == businessId)
            .Select(row => (long?)row.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        var changes = await db.SyncChanges.AsNoTracking()
            .CountAsync(change => change.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);
        var outcomes = await db.ProcessedOperations.AsNoTracking()
            .Where(operation => operation.BusinessId == businessId)
            .GroupBy(operation => operation.Outcome)
            .Select(group => new { Outcome = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var newestRejection = await db.ProcessedOperations.AsNoTracking()
            .Where(operation => operation.BusinessId == businessId
                && operation.Outcome != SyncOutcome.Posted
                && operation.Outcome != SyncOutcome.AlreadyPosted)
            .OrderByDescending(operation => operation.ProcessedAtUtc)
            .Select(operation => new { operation.ErrorCode, operation.ProcessedAtUtc, operation.OperationId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var rejectedLastDay = await db.ProcessedOperations.AsNoTracking()
            .CountAsync(operation => operation.BusinessId == businessId
                && operation.Outcome != SyncOutcome.Posted
                && operation.Outcome != SyncOutcome.AlreadyPosted
                && operation.ProcessedAtUtc >= DateTimeOffset.UtcNow.AddHours(-24), cancellationToken)
            .ConfigureAwait(false);

        int Count(SyncOutcome outcome) => outcomes.Where(row => row.Outcome == outcome).Sum(row => row.Count);

        return new SyncDiagnosticsDto(
            revision, changes,
            Count(SyncOutcome.Posted),
            Count(SyncOutcome.AlreadyPosted),
            Count(SyncOutcome.NeedsReview) + Count(SyncOutcome.NotAuthorized) + Count(SyncOutcome.DeviceRevoked)
                + Count(SyncOutcome.UnsupportedPayloadVersion),
            rejectedLastDay,
            newestRejection?.ErrorCode,
            newestRejection?.ProcessedAtUtc,
            newestRejection is null ? null : newestRejection.OperationId.ToString("D"));
    }

    private static KeyExpiryDiagnosticsDto ReadKeyExpiry(IConfiguration configuration)
    {
        var path = configuration["QTS_OPENIDDICT_CERTIFICATE_PATH"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new KeyExpiryDiagnosticsDto(false, null, null, null, WarnBeforeCertificateDays, true,
                "no signing certificate is configured on this instance, so token signing cannot be verified");
        }

        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(path);
            var remaining = (int)Math.Round((certificate.NotAfter - DateTime.UtcNow).TotalDays, MidpointRounding.AwayFromZero);
            return new KeyExpiryDiagnosticsDto(
                true, certificate.Subject, new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                remaining, WarnBeforeCertificateDays, remaining <= WarnBeforeCertificateDays,
                remaining <= WarnBeforeCertificateDays
                    ? $"the signing certificate expires in {remaining} day(s)"
                    : "the signing certificate has months left");
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            // The path and the password are the instance's own; the operator only needs to know it cannot be opened.
            return new KeyExpiryDiagnosticsDto(true, null, null, null, WarnBeforeCertificateDays, true,
                $"the configured certificate could not be opened ({exception.GetType().Name})");
        }
    }

    private static IReadOnlyList<DiagnosticsAlertDto> BuildAlerts(
        StoreDiagnosticsDto store,
        CapacityDiagnosticsDto capacity,
        BackupEvidenceDto backup,
        DeviceFleetDiagnosticsDto devices,
        SyncDiagnosticsDto sync,
        KeyExpiryDiagnosticsDto key,
        string? newestCorrelation)
    {
        var alerts = new List<DiagnosticsAlertDto>();

        if (!store.Reachable)
        {
            alerts.Add(new DiagnosticsAlertDto("store-unreachable", "critical", "the store probe",
                "The authoritative PostgreSQL store did not answer, so nothing on this page can be trusted as current.",
                "Check the database service and the connection this instance was given.", null));
        }

        if (!capacity.Measured)
        {
            alerts.Add(new DiagnosticsAlertDto("capacity-unmeasured", "warning", capacity.Path,
                "This instance could not measure the volume it writes to, so a full disk would arrive unannounced.",
                $"Point {CapacityPathVariable} at a path on the volume that holds the data.", null));
        }
        else if (capacity.Warning)
        {
            alerts.Add(new DiagnosticsAlertDto("capacity-nearly-full", "critical",
                $"{capacity.UsedPercent:F1}% of {capacity.Path} is used, {FormatBytes(capacity.FreeBytes)} free",
                $"The volume this instance writes to has passed {capacity.WarnAtPercent}% used. A backup or a posting can fail on a full disk.",
                "Free space or move the data directory before the next posting window.", newestCorrelation));
        }

        if (!backup.Configured)
        {
            alerts.Add(new DiagnosticsAlertDto("backup-unconfigured", "warning", backup.Directory ?? "not set",
                "No backup directory is configured, so this instance cannot prove it has a restore point at all.",
                $"Set {BackupDirectoryVariable} to the folder scripts/backup-postgres.ps1 writes into.", null));
        }
        else if (backup.Stale)
        {
            alerts.Add(new DiagnosticsAlertDto(backup.FileName is null ? "backup-missing" : "backup-stale", "critical",
                backup.FileName is null ? "no backup manifest in the configured directory" : $"{backup.FileName} is {backup.AgeHours} hour(s) old",
                "The newest backup on this instance is older than the freshness window, or cannot be read.",
                "Run the backup script now, and read the restore drill before trusting the newest file.", newestCorrelation));
        }

        if (devices.Silent > 0)
        {
            alerts.Add(new DiagnosticsAlertDto("devices-silent", "warning",
                $"{devices.Silent} active terminal(s) have not reported in {devices.SilentAfterHours} hour(s)",
                string.Join(", ", devices.SilentDeviceNames),
                "A silent terminal still holds its queue and possibly its drawer. Check the device before its shift starts.",
                newestCorrelation));
        }

        if (devices.BehindOnSchema > 0)
        {
            alerts.Add(new DiagnosticsAlertDto("devices-behind-schema", "warning",
                $"{devices.BehindOnSchema} terminal(s) report a schema older than {devices.NewestSchemaVersion}",
                "An offline terminal that updates into a newer schema has to be reconciled before it posts again.",
                "Update those devices while they are still off the counter.", newestCorrelation));
        }

        if (sync.RejectedLast24Hours > 0)
        {
            alerts.Add(new DiagnosticsAlertDto("operations-rejected", "critical",
                $"{sync.RejectedLast24Hours} captured operation(s) were refused in the last 24 hours"
                    + (sync.NewestRejectionCode is { Length: > 0 } code ? $", newest code {code}" : string.Empty),
                "A refused capture means money or stock a terminal took that the ledger has not accepted.",
                "Open the audit feed, find the operation, and resolve it with the terminal's operator.",
                sync.RejectionReference ?? newestCorrelation));
        }

        if (key.Warning)
        {
            alerts.Add(new DiagnosticsAlertDto("signing-key", "warning", key.Note,
                "Token signing depends on this material, so an expiry here locks the terminals out.",
                "Replace the certificate and re-enrol only what cannot ride through the change.", null));
        }

        return alerts;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Anything shaped like a credential, a token or a connection string is replaced before it can reach a bundle.
    /// The audit summaries are already bounded and masked, and this is the second gate rather than a substitute - it
    /// is public so the redaction contract is testable without reaching into the server's file system.
    /// </summary>
    public static string? Scrub(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        // Connection or key material, whatever the field is called: the line is a credential, not a fact.
        if (value.Contains("-----BEGIN", StringComparison.Ordinal)
            || value.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted: connection or key material]";
        }

        var scrubbed = value;

        // A bearer value or a JWT-shaped run has no "=" to hang a marker on and is a credential by shape alone, so
        // the line ends where the credential starts.
        foreach (var prefix in new[] { "bearer", "authorization", "eyJ" })
        {
            var at = scrubbed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                scrubbed = $"{scrubbed[..at].TrimEnd(' ', ':', '=')} [redacted]";
            }
        }

        // A key=value pair whose key says it is secret, with the value - and nothing after it on that pair - gone.
        foreach (var marker in new[] { "password", "pwd", "secret", "token", "private key" })
        {
            scrubbed = ReplaceAfter(scrubbed, marker);
        }

        return scrubbed.Length > 400 ? scrubbed[..400] : scrubbed;
    }

    private static string ReplaceAfter(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return value;
        }

        var separator = value.IndexOfAny(['=', ':'], index);
        if (separator < 0)
        {
            // The word itself is the secret's start; nothing after it is kept.
            return string.Concat(value.AsSpan(0, index).ToString().TrimEnd(' ', ':', '='), " [redacted]");
        }

        var end = value.IndexOf(' ', separator);
        if (end < 0)
        {
            end = value.Length;
        }

        return string.Concat(value.AsSpan(0, separator + 1), "[redacted]", value.AsSpan(end));
    }

    private static string Serialize(object document)
        => JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private static string ResolveDirectory(IConfiguration configuration, IHostEnvironment environment, string variable, string defaultFolder)
    {
        var configured = configuration[variable];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        return Path.Combine(environment.ContentRootPath, defaultFolder);
    }

    private static int ReadInteger(IConfiguration configuration, string variable, int fallback)
        => int.TryParse(configuration[variable], out var value) && value > 0 ? value : fallback;

    private static string FormatBytes(long bytes)
        => $"{bytes / 1_073_741_824d:F1} GB";

    private static void TrimBundles(string directory)
    {
        try
        {
            var stale = Directory.GetFiles(directory, "qts-support-bundle-*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(BundleRetention)
                .ToArray();
            foreach (var path in stale)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention is a courtesy; a bundle that could not be trimmed is not worth failing the request for.
        }
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
}
