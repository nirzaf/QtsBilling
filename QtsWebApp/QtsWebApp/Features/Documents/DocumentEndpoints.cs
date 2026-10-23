using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Documents;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Documents;

/// <summary>
/// Local document evidence (specification task 39): a PDF, JPEG or PNG that belongs to a document this instance
/// already authorises, kept on this machine's own disk in a directory no browser can reach, with the metadata, the
/// digest and the version history in the database.
/// </summary>
/// <remarks>
/// The shape of every act is the same. The caller supplies bytes and nothing else that matters: no file name, no
/// path, no URL, no storage key - a name supplied by a caller is a place on somebody's disk, and a URL supplied by a
/// caller is a fetch this instance would be making on somebody else's behalf. The type is negotiated from the leading
/// bytes, so a claim and the bytes are compared rather than believed, and anything that could run or render is
/// refused before a write is attempted. The bytes are written and read back and hashed before any metadata exists;
/// the metadata and its audit entry then commit in one short transaction, and no file I/O is held inside a financial
/// one. A download re-checks the current authority of the caller and re-hashes what is on the disk: evidence whose
/// bytes no longer match its digest is not served, because a document that silently changed under an operator is
/// worse than a missing one.
/// </remarks>
public static class DocumentEndpoints
{
    /// <summary>Where this instance keeps evidence. Unset means a directory of its own, outside webroot.</summary>
    public const string DirectoryVariable = "QTS_DOCUMENT_EVIDENCE_DIRECTORY";

    /// <summary>How long evidence is undertaken to be kept, counted from the moment it was attached.</summary>
    public const int RetentionDays = 2555;

    /// <summary>
    /// The size bound, named on the server as well as in the contract so a screen's sentence and the database's check
    /// constraint cannot quietly become two different figures.
    /// </summary>
    public const int MaxHeldBytes = QtsBilling.Contracts.Documents.DocumentEvidenceRules.MaxFileBytes;

    /// <summary>How many unlinked files one cleanup pass may look at, so a disk full of orphans is not a hang.</summary>
    public const int MaxCleanupExamination = 500;

    private static readonly IReadOnlyDictionary<string, string> AggregatePermission =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DocumentAggregates.Invoice] = Permissions.InvoicesManage,
            [DocumentAggregates.Expense] = Permissions.ExpensesManage,
            [DocumentAggregates.PurchaseOrder] = Permissions.PurchasingManage,
            [DocumentAggregates.PurchaseReceipt] = Permissions.PurchasingManage,
        };

    private static readonly IReadOnlyDictionary<string, string> ExtensionForType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = "pdf",
            ["image/jpeg"] = "jpg",
            ["image/png"] = "png",
        };

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents").WithTags("Documents").RequireAuthorization();

        group.MapGet("/evidence", ListAsync);
        group.MapPost("/evidence", AttachAsync);
        group.MapPost("/evidence/{evidenceId:guid}/replace", ReplaceAsync);
        group.MapGet("/evidence/{evidenceId:guid}/content", DownloadAsync);

        // The inventory and the cleanup are operator acts over every aggregate at once, so they sit behind the audit
        // authority rather than behind any single document's permission.
        group.MapGet("/evidence/inventory", InventoryAsync).RequireAuthorization(Permissions.AuditView);
        group.MapPost("/evidence/orphans/cleanup", CleanupAsync).RequireAuthorization(Permissions.AuditView);

        return app;
    }

    // ------------------------------------------------------------------ the reads

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        string aggregateType,
        Guid aggregateId,
        bool? includeHistory,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var normalized = NormalizeAggregate(aggregateType);

        if (normalized is null)
        {
            return UnknownAggregate();
        }

        var aggregate = await ResolveAggregateAsync(db, businessId, normalized, aggregateId, cancellationToken).ConfigureAwait(false);

        if (!aggregate.Exists)
        {
            return NotFoundAggregate(normalized);
        }

        if (await AuthorityRefusalAsync(db, userId, businessId, normalized, aggregate.LocationId, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var query = db.DocumentEvidence.AsNoTracking()
            .Where(evidence => evidence.BusinessId == businessId
                && evidence.AggregateType == normalized
                && evidence.AggregateId == aggregateId);

        if (includeHistory == false)
        {
            query = query.Where(evidence => evidence.ReplacedById == null);
        }

        var rows = await query
            .OrderByDescending(evidence => evidence.VersionNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows.Select(ToDto).ToArray());
    }

    private static async Task<IResult> InventoryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rows = await db.DocumentEvidence.AsNoTracking()
            .Where(evidence => evidence.BusinessId == businessId)
            .OrderByDescending(evidence => evidence.UploadedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var measuredAtUtc = DateTimeOffset.UtcNow;
        var directory = EvidenceDirectory(environment, configuration);

        // The path itself is only named to someone who may read the diagnostics, which is where a path is already
        // written down. Everyone else gets the same figures without the address.
        var maySeePath = principal.HasPermission(Permissions.AuditView);

        return Results.Ok(new DocumentEvidenceInventoryDto(
            measuredAtUtc,
            rows.Count,
            rows.Count(row => row.ReplacedById == null),
            rows.Sum(row => row.SizeBytes),
            maySeePath ? directory : "this instance's private evidence directory",
            rows.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> DownloadAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var evidence = await db.DocumentEvidence.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == evidenceId && row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (evidence is null)
        {
            return NotFoundEvidence();
        }

        var aggregate = await ResolveAggregateAsync(db, businessId, evidence.AggregateType, evidence.AggregateId, cancellationToken)
            .ConfigureAwait(false);

        if (!aggregate.Exists)
        {
            return NotFoundAggregate(evidence.AggregateType);
        }

        if (await AuthorityRefusalAsync(db, userId, businessId, evidence.AggregateType, aggregate.LocationId, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (!TryPrivateDirectory(environment, configuration, out var directory, out var refusalSentence))
        {
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence directory is refused.",
                refusalSentence);
        }

        var path = Path.Combine(directory, Path.GetFileName(evidence.StorageKey));
        byte[] bytes;

        try
        {
            if (!File.Exists(path))
            {
                return ApiResults.Problem(
                    StatusCodes.Status410Gone,
                    ApiErrorCodes.StoreUnavailable,
                    "The bytes this record describes are not on this machine.",
                    $"Evidence version {evidence.VersionNumber} was linked to a file this instance can no longer find. "
                    + "The metadata and the audit entry are intact; the file is what is missing.");
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence file could not be read.",
                $"This instance could not open the file ({exception.GetType().Name}); nothing is claimed about it.");
        }

        // The digest is recomputed on the way out. Evidence that changed under the record is refused rather than
        // served, because the whole point of a hash is to be able to say which of the two is lying.
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != evidence.Sha256
            || (long)bytes.Length != evidence.SizeBytes)
        {
            return ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.StoreUnavailable,
                "The evidence did not match its own digest.",
                $"Version {evidence.VersionNumber} of this {evidence.AggregateType} evidence was recorded as "
                + $"{evidence.SizeBytes} bytes and read back as {bytes.Length}. It is not served in that state.");
        }

        var fileName = DocumentEvidenceRules.ComposeFileName(evidence.Title, evidence.VersionNumber, evidence.FileExtension);

        // Attached, never inline, and never sniffed: an evidence file is a thing to keep, not a thing to run in the
        // page that asked for it.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "sandbox";
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

        return Results.File(bytes, evidence.ContentType, fileName);
    }

    // ------------------------------------------------------------------ the writes

    private static async Task<IResult> AttachAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        AttachDocumentEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var (refusal, created) = await AttachCoreAsync(
            principal, db, http, environment, configuration, request, cancellationToken).ConfigureAwait(false);

        return refusal ?? Results.Ok(created);
    }

    /// <summary>
    /// The whole attach act, returning either the refusal that stopped it or the version that is now held. It is
    /// separate from the route because replacing evidence is the same act with one more step after it, and two
    /// copies of the guards would be two sets of guards that can drift apart.
    /// </summary>
    private static async Task<(IResult? Refusal, DocumentEvidenceDto? Created)> AttachCoreAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        AttachDocumentEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return (BusinessScope.MissingMembership(), null);
        }

        var normalized = NormalizeAggregate(request.AggregateType);

        if (normalized is null)
        {
            return (UnknownAggregate(), null);
        }

        var title = request.Title?.Trim() ?? string.Empty;

        if (title.Length is 0 or > DocumentEvidenceRules.MaxTitleLength)
        {
            return (ApiResults.ValidationProblem(
                "The evidence payload is invalid.",
                new Dictionary<string, string[]>
                {
                    ["title"] = [$"Evidence is named in 1-{DocumentEvidenceRules.MaxTitleLength} characters."],
                }), null);
        }

        var bytes = request.Content ?? [];

        if (bytes.Length == 0)
        {
            return (ApiResults.ValidationProblem(
                "The evidence payload is invalid.",
                new Dictionary<string, string[]> { ["content"] = ["Evidence with no bytes is not evidence."] }), null);
        }

        if (bytes.Length > DocumentEvidenceRules.MaxFileBytes)
        {
            return (ApiResults.ValidationProblem(
                "The evidence is larger than this instance will hold.",
                new Dictionary<string, string[]> { ["content"] = [DocumentEvidenceRules.SizeSentence(bytes.LongLength)] }), null);
        }

        var aggregate = await ResolveAggregateAsync(db, businessId, normalized, request.AggregateId, cancellationToken)
            .ConfigureAwait(false);

        if (!aggregate.Exists)
        {
            return (NotFoundAggregate(normalized), null);
        }

        if (await AuthorityRefusalAsync(db, userId, businessId, normalized, aggregate.LocationId, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return (refusal, null);
        }

        if (NegotiateContentType(bytes) is not { } contentType)
        {
            return (RefusedType(bytes, request.ContentType), null);
        }

        if (request.ContentType is { Length: > 0 } claimed
            && !string.Equals(claimed.Trim(), contentType, StringComparison.OrdinalIgnoreCase))
        {
            return (ApiResults.Problem(
                StatusCodes.Status415UnsupportedMediaType,
                ApiErrorCodes.ValidationFailed,
                "The bytes are not what the request claimed.",
                $"These bytes are {contentType} by their own leading characters, not {claimed}. Evidence is typed by "
                + "what it is, because a claimed type is the oldest way a file lies about itself."), null);
        }

        if (!TryPrivateDirectory(environment, configuration, out var directory, out var refusalSentence))
        {
            return (ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence directory is refused.",
                refusalSentence), null);
        }

        var staged = await StageAsync(directory, bytes, cancellationToken).ConfigureAwait(false);

        if (staged.refusal is not null)
        {
            return (staged.refusal, null);
        }

        var (path, key, sha256) = staged.written;

        // The unit of retry this instance uses for every user-initiated transaction: the execution strategy owns the
        // transaction, so a transient fault re-runs the whole link rather than leaving a written file with no record.
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            // Named so the execution strategy binds its generic overload: the link's answer is either a refusal or
            // the version now held, and an inline lambda loses that to the Task-only signature.
            async Task<(IResult? Refusal, DocumentEvidenceDto? Created)> LinkAsync()
            {
                db.ChangeTracker.Clear();

                // A duplicate of bytes this aggregate already holds is the same evidence, not a second copy of it:
                // the staged file is discarded and the version already linked is the answer.
                var existing = await db.DocumentEvidence.AsNoTracking()
                    .FirstOrDefaultAsync(evidence => evidence.BusinessId == businessId
                        && evidence.AggregateType == normalized
                        && evidence.AggregateId == request.AggregateId
                        && evidence.Sha256 == sha256
                        && evidence.ReplacedById == null, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    TryDelete(path);

                    return (null, ToDto(existing));
                }

                var now = DateTimeOffset.UtcNow;
                var evidence = new DocumentEvidence
                {
                    BusinessId = businessId,
                    LocationId = aggregate.LocationId,
                    AggregateType = normalized,
                    AggregateId = request.AggregateId,
                    Title = title,
                    ContentType = contentType,
                    FileExtension = ExtensionForType[contentType],
                    SizeBytes = bytes.LongLength,
                    Sha256 = sha256,
                    StorageKey = key,
                    Status = DocumentEvidenceStatus.Linked,
                    UploadedAtUtc = now,
                    UploadedByUserId = userId,
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : Trim(request.Note, 500),
                    RetainUntilUtc = now.AddDays(RetentionDays),
                };

                // The version number is allocated inside the same short transaction that writes the audit entry, and
                // the unique index over (business, aggregate, version) is what makes two concurrent attaches of the
                // same document settle on two versions rather than one row overwriting the other.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                evidence.VersionNumber = await NextVersionAsync(db, businessId, normalized, request.AggregateId, cancellationToken)
                    .ConfigureAwait(false);

                if (evidence.VersionNumber > DocumentEvidenceRules.MaxVersionsPerAggregate)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    TryDelete(path);

                    return (ApiResults.Problem(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This document already holds the evidence this instance will keep.",
                        $"An aggregate keeps at most {DocumentEvidenceRules.MaxVersionsPerAggregate} versions of its "
                        + "evidence. The bytes were not written into the history."), null);
                }

                db.DocumentEvidence.Add(evidence);
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "document_evidence",
                    EntityId = evidence.Id,
                    Action = "attached",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        aggregateType = normalized,
                        aggregateId = request.AggregateId,
                        version = evidence.VersionNumber,
                        contentType,
                        bytes = evidence.SizeBytes,
                        sha256,
                        title,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return (null, ToDto(evidence));
            }

            return await strategy.ExecuteAsync(LinkAsync).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            // The metadata never committed, so the file is an orphan by definition and is left for the bounded
            // cleanup rather than deleted in a panic: a race that produced two rows for one key must not throw bytes
            // away that a committed row may be pointing at.
            TryDelete(path);

            return (ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence could not be linked to its document.",
                $"This instance wrote the file but could not commit its record ({exception.GetType().Name}: "
                + $"{exception.Message}). Nothing was claimed as attached, and no document changed."), null);
        }
    }

    private static async Task<IResult> ReplaceAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        Guid evidenceId,
        ReplaceDocumentEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var superseded = await db.DocumentEvidence.AsNoTracking()
            .FirstOrDefaultAsync(evidence => evidence.Id == evidenceId && evidence.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (superseded is null)
        {
            return NotFoundEvidence();
        }

        if (superseded.ReplacedById is not null)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "That version has already been replaced.",
                $"Version {superseded.VersionNumber} of this evidence stopped being current earlier; the history "
                + "already says which version took its place.");
        }

        // Replacing is the attach act on the same aggregate, followed by one link: the version that stops being
        // current keeps its row, its bytes and its audit entry, and names the version that took its place.
        var (attachRefusal, created) = await AttachCoreAsync(
            principal,
            db,
            http,
            environment,
            configuration,
            new AttachDocumentEvidenceRequest(
                superseded.AggregateType,
                superseded.AggregateId,
                request.Title,
                request.ContentType,
                request.Content,
                request.Note),
            cancellationToken).ConfigureAwait(false);

        if (attachRefusal is not null)
        {
            return attachRefusal;
        }

        if (created is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The replacement was written but did not come back.",
                "The new version of this evidence exists; the instance could not read its own answer back.");
        }

        // The link that makes the new version the current one, in the same retry unit the rest of this instance's
        // user-initiated transactions use.
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            async Task<IResult> MarkSupersededAsync()
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var current = await db.DocumentEvidence
                    .FirstOrDefaultAsync(evidence => evidence.Id == evidenceId && evidence.BusinessId == businessId, cancellationToken)
                    .ConfigureAwait(false);

                if (current is null || current.ReplacedById is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return ApiResults.Problem(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "That version was replaced while this request was running.",
                        "Read the evidence list again: the newest version is the one now held.");
                }

                var now = DateTimeOffset.UtcNow;
                current.ReplacedById = created.Id;
                current.Status = DocumentEvidenceStatus.Superseded;

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "document_evidence",
                    EntityId = current.Id,
                    Action = "replaced",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        aggregateType = current.AggregateType,
                        aggregateId = current.AggregateId,
                        supersededVersion = current.VersionNumber,
                        newVersion = created.Version,
                        newSha256 = created.Sha256,
                        reason = Trim(request.Note ?? string.Empty, 500),
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Results.Ok(created);
            }

            return await strategy.ExecuteAsync(MarkSupersededAsync).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            // Said plainly, because it is the state an operator has to know about: the new version is held and the
            // old one was not marked as giving way. Nothing was lost, and nothing was deleted to hide it.
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The new version is held, but the one it replaced was not marked.",
                $"This instance could not commit the link ({exception.GetType().Name}: {exception.Message}). Both "
                + "versions are on the disk and readable; read the evidence list and replace again if the older one "
                + "should still give way.");
        }
    }

    private static async Task<IResult> CleanupAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryPrivateDirectory(environment, configuration, out var directory, out var refusalSentence))
        {
            return ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence directory is refused.",
                refusalSentence);
        }

        var outcome = await CleanupOrphansAsync(db, directory, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        if (outcome.RemovedKeys.Count > 0)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = businessId,
                UserId = userId,
                EntityType = "document_evidence",
                EntityId = Guid.NewGuid(),
                Action = "orphans_removed",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = http.TraceIdentifier,
                Summary = JsonSerializer.Serialize(new
                {
                    examined = outcome.Examined,
                    removed = outcome.RemovedKeys.Count,
                    referencedKept = outcome.ReferencedKept,
                    tooYoungToJudge = outcome.TooYoungToJudge,
                }),
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(outcome);
    }

    // ------------------------------------------------------------------ the machinery

    /// <summary>
    /// Removes files that carry no metadata row and are older than the grace, and only those. A file that any
    /// business's record points at is kept, whichever business wrote it, because a cleanup that guesses about
    /// ownership is a way of destroying another tenant's evidence (case T39.03).
    /// </summary>
    public static async Task<DocumentEvidenceCleanupDto> CleanupOrphansAsync(
        BillingDbContext db,
        string directory,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        int limit = MaxCleanupExamination)
    {
        var removed = new List<string>();
        var examined = 0;
        var kept = 0;
        var tooYoung = 0;

        if (!Directory.Exists(directory))
        {
            return new DocumentEvidenceCleanupDto(0, removed, 0, 0, nowUtc);
        }

        var referenced = await db.DocumentEvidence.AsNoTracking()
            .Select(evidence => evidence.StorageKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var known = new HashSet<string>(referenced, StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory).Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = Path.GetFileName(path);

            if (key.Length == 0)
            {
                continue;
            }

            examined++;

            if (known.Contains(key))
            {
                kept++;
                continue;
            }

            DateTime writtenUtc;

            try
            {
                writtenUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (nowUtc.UtcDateTime - writtenUtc < DocumentEvidenceRules.OrphanGrace)
            {
                tooYoung++;
                continue;
            }

            if (TryDelete(path))
            {
                removed.Add(key);
            }
        }

        return new DocumentEvidenceCleanupDto(examined, removed, kept, tooYoung, nowUtc);
    }

    /// <summary>
    /// Writes the bytes, then reads them back and hashes what is on the disk. Only a file that survived the round
    /// trip is offered for linking; anything else is removed and refused, because a metadata row describing bytes
    /// this instance cannot produce is the exact thing evidence is supposed to prevent.
    /// </summary>
    private static async Task<(IResult? refusal, (string path, string key, string sha256) written)> StageAsync(
        string directory,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var key = Guid.CreateVersion7().ToString("N");
        var path = Path.Combine(directory, key);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(path);

            if (info.Length != bytes.LongLength)
            {
                TryDelete(path);

                return (ApiResults.Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    ApiErrorCodes.StoreUnavailable,
                    "The evidence did not survive the write.",
                    $"{bytes.LongLength} bytes were offered and {info.Length} are on the disk. Nothing was linked to that file."),
                    default);
            }

            var readBack = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(readBack)).ToLowerInvariant();

            if (actual != expected)
            {
                TryDelete(path);

                return (ApiResults.Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    ApiErrorCodes.StoreUnavailable,
                    "The evidence did not hash back the same.",
                    "The file on disk is not the bytes that were sent, so it is not offered as evidence of anything."),
                    default);
            }

            return (null, (path, key, expected));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);

            return (ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.StoreUnavailable,
                "The evidence directory refused the write.",
                $"This instance could not place the file ({exception.GetType().Name}). Nothing was claimed."),
                default);
        }
    }

    private static IResult RefusedType(byte[] bytes, string? claimed)
    {
        var lead = bytes.Length > 0 ? (char)bytes[0] : ' ';
        var looksActive = lead is '<'
            || (bytes.Length >= 2 && bytes[0] == 0x4D && bytes[1] == 0x5A)
            || (bytes.Length >= 5 && bytes[0] == 0x3C && bytes[1] == 0x3F)
            || (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == 0x45 && bytes[2] == 0x4C && bytes[3] == 0x46);

        return ApiResults.Problem(
            StatusCodes.Status415UnsupportedMediaType,
            ApiErrorCodes.ValidationFailed,
            looksActive
                ? "That is not evidence this instance will hold."
                : "The bytes are not a PDF, JPEG or PNG.",
            looksActive
                ? "Evidence is a picture of a document, not a thing that can run or render: a script, an HTML or SVG "
                  + "document and an executable are refused, and nothing was written."
                : $"Only {string.Join(", ", DocumentEvidenceRules.AcceptedContentTypes)} are accepted, recognised from "
                  + $"the leading bytes rather than from a claim{(claimed is null ? "." : $" (this request claimed {claimed}).")}");
    }

    /// <summary>
    /// The type the bytes declare themselves to be, from their own leading characters. A claim in the request is
    /// never enough, and nothing that is not on the accepted list is honoured - including a file whose name said PDF.
    /// </summary>
    public static string? NegotiateContentType(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            return "application/pdf";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        return null;
    }

    /// <summary>
    /// The directory this instance keeps evidence in, and the refusal when it is not a private one. Evidence under
    /// webroot would be readable by URL without an authority check, so it is refused rather than created.
    /// </summary>
    public static bool TryPrivateDirectory(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        out string directory,
        out string sentence)
    {
        var root = EvidenceDirectory(environment, configuration);
        var files = Path.Combine(root, "files");
        var webRoot = environment.WebRootPath;

        // The one shape of evidence storage that would defeat every authority check above it: a directory a browser
        // can already reach by URL.
        var underWebRoot = !string.IsNullOrWhiteSpace(webRoot)
            && files.StartsWith(Path.GetFullPath(webRoot), StringComparison.OrdinalIgnoreCase);

        if (underWebRoot)
        {
            directory = string.Empty;
            sentence = "Evidence is kept in a private directory of this instance, outside the part a browser can "
                + "reach. The configured location sits inside it, so nothing was written.";
            return false;
        }

        directory = files;
        sentence = string.Empty;
        return true;
    }

    public static string EvidenceDirectory(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration[DirectoryVariable];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "document-evidence"))
            : Path.GetFullPath(configured.Trim());
    }

    /// <summary>
    /// Whether the aggregate exists in this business, and which branch it belongs to. An aggregate with no branch
    /// (an expense booked without one) is still an aggregate, so the two answers are kept apart rather than both
    /// being a null.
    /// </summary>
    private static async Task<(bool Exists, Guid? LocationId)> ResolveAggregateAsync(
        BillingDbContext db,
        Guid businessId,
        string aggregateType,
        Guid aggregateId,
        CancellationToken cancellationToken)
    {
        switch (aggregateType.ToLowerInvariant())
        {
            case DocumentAggregates.Invoice:
            {
                var invoice = await db.Invoices.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && row.Id == aggregateId)
                    .Select(row => new { row.Id, row.LocationId })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                return invoice is null ? (false, null) : (true, invoice.LocationId);
            }

            case DocumentAggregates.Expense:
            {
                var expense = await db.Expenses.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && row.Id == aggregateId)
                    .Select(row => new { row.Id, row.LocationId })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                return expense is null ? (false, null) : (true, expense.LocationId);
            }

            case DocumentAggregates.PurchaseOrder:
            {
                var order = await db.PurchaseOrders.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && row.Id == aggregateId)
                    .Select(row => new { row.Id, row.LocationId })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                return order is null ? (false, null) : (true, order.LocationId);
            }

            case DocumentAggregates.PurchaseReceipt:
            {
                var receipt = await db.PurchaseReceipts.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && row.Id == aggregateId)
                    .Select(row => new { row.Id, row.LocationId })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                return receipt is null ? (false, null) : (true, receipt.LocationId);
            }

            default:
                return (false, null);
        }
    }

    private static async Task<IResult?> AuthorityRefusalAsync(
        BillingDbContext db,
        Guid userId,
        Guid businessId,
        string aggregateType,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        var permission = AggregatePermission.TryGetValue(aggregateType, out var named) ? named : null;

        if (permission is null)
        {
            return UnknownAggregate();
        }

        // D02/F07: the authority is the one the membership holds now, not the set of claims handed out at sign-in.
        if (!await BusinessScope.HasCurrentPermissionAsync(db, userId, businessId, permission, cancellationToken)
                .ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                $"This evidence belongs to a {aggregateType.Replace('_', ' ')}, and managing one needs '{permission}'.",
                $"The signed-in membership does not hold '{permission}' here, so nothing was read or written.");
        }

        if (locationId is { } branch)
        {
            var allowed = await BusinessScope.AccessibleLocationIdsAsync(db, userId, businessId, cancellationToken)
                .ConfigureAwait(false);

            if (allowed is { } list && !list.Contains(branch))
            {
                return ApiResults.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.Forbidden,
                    "That document belongs to a branch this account is not given.",
                    "Evidence is read and written through its document's own branch scope, never around it.");
            }
        }

        return null;
    }

    private static async Task<int> NextVersionAsync(
        BillingDbContext db,
        Guid businessId,
        string aggregateType,
        Guid aggregateId,
        CancellationToken cancellationToken)
        => await db.DocumentEvidence
            .Where(evidence => evidence.BusinessId == businessId
                && evidence.AggregateType == aggregateType
                && evidence.AggregateId == aggregateId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false) + 1;

    private static bool IsCurrent(DocumentEvidence evidence) => evidence.ReplacedById is null;

    private static DocumentEvidenceDto ToDto(DocumentEvidence evidence)
        => new(
            evidence.Id,
            evidence.BusinessId,
            evidence.LocationId,
            evidence.AggregateType,
            evidence.AggregateId,
            evidence.Title,
            evidence.ContentType,
            evidence.FileExtension,
            evidence.SizeBytes,
            evidence.Sha256,
            evidence.VersionNumber,
            evidence.ReplacesId,
            evidence.ReplacedById,
            evidence.Status.ToString(),
            IsCurrent(evidence),
            evidence.UploadedAtUtc,
            evidence.UploadedByUserId,
            evidence.Note,
            evidence.RetainUntilUtc);

    private static string? NormalizeAggregate(string? aggregateType)
        => aggregateType is { } value
           && DocumentAggregates.Allowed.FirstOrDefault(allowed => string.Equals(allowed, value.Trim(), StringComparison.OrdinalIgnoreCase))
            is { } found
            ? found
            : null;

    private static string Trim(string value, int maximum)
        => value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IResult UnknownAggregate()
        => ApiResults.ValidationProblem(
            "Evidence belongs to a document this instance knows.",
            new Dictionary<string, string[]>
            {
                ["aggregateType"] =
                [
                    $"Only {string.Join(", ", DocumentAggregates.Allowed)} carry local evidence.",
                ],
            });

    private static IResult NotFoundAggregate(string aggregateType)
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            $"No {aggregateType.Replace('_', ' ')} of this business carries that identifier.",
            "Evidence is attached to a document that exists here, so the document is checked first.");

    private static IResult NotFoundEvidence()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "No evidence of this business carries that identifier.",
            "Another business's evidence is not answered with its own record, only with nothing.");
}
