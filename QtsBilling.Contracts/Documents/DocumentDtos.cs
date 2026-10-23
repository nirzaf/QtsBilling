using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Contracts.Documents;

/// <summary>
/// The aggregates local evidence may be attached to. The list is closed on purpose: evidence belongs to a document
/// this instance already authorises, and an open-ended string would let a caller invent a table (specification
/// task 39, recipe step 1).
/// </summary>
public static class DocumentAggregates
{
    public const string Invoice = "invoice";
    public const string Expense = "expense";
    public const string PurchaseOrder = "purchase_order";
    public const string PurchaseReceipt = "purchase_receipt";

    public static readonly IReadOnlyList<string> Allowed = [Invoice, Expense, PurchaseOrder, PurchaseReceipt];

    public static bool IsAllowed(string? aggregateType)
        => aggregateType is { } value && Allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One version of one piece of local evidence. The digest and size describe the bytes this instance holds; the
/// storage key is opaque and is never a path, and the file name an operator sees is composed on this machine from
/// the title, with the characters that could make it a place on a disk removed.
/// </summary>
public sealed record DocumentEvidenceDto(
    Guid Id,
    Guid BusinessId,
    Guid? LocationId,
    string AggregateType,
    Guid AggregateId,
    string Title,
    string ContentType,
    string FileExtension,
    long SizeBytes,
    string Sha256,
    int Version,
    Guid? ReplacesId,
    Guid? ReplacedById,
    string Status,
    bool Current,
    DateTimeOffset UploadedAtUtc,
    Guid UploadedByUserId,
    string? Note,
    DateTimeOffset? RetainUntilUtc);

/// <summary>
/// Attaching evidence: the aggregate it belongs to, what the operator calls it, and the bytes. The client supplies
/// no file name, no path, no URL and no storage key - those are this instance's business, and a claimed content type
/// is only a claim the leading bytes have to agree with.
/// </summary>
public sealed record AttachDocumentEvidenceRequest(
    string AggregateType,
    Guid AggregateId,
    string Title,
    string? ContentType,
    byte[] Content,
    string? Note);

/// <summary>
/// Replacing a version that is already held. The aggregate is read from the version being replaced rather than
/// supplied, so a replacement cannot move evidence from one document to another.
/// </summary>
public sealed record ReplaceDocumentEvidenceRequest(
    string Title,
    string? ContentType,
    byte[] Content,
    string? Note);

/// <summary>
/// Evidence bytes fetched by the authenticated client. The content never travels in a URL and the file name is the
/// one the server composed, so the screen can hand it to the operator's own device without inventing either.
/// </summary>
public sealed record DocumentContentDto(
    string FileName,
    string ContentType,
    string Sha256,
    long SizeBytes,
    byte[] Content);

/// <summary>
/// The evidence inventory of this business: every version, its digest and its size, measured at one moment. This is
/// what a backup has to carry alongside the database dump, because the bytes live outside the database and a dump
/// alone would restore documents whose evidence is gone.
/// </summary>
public sealed record DocumentEvidenceInventoryDto(
    DateTimeOffset MeasuredAtUtc,
    int Versions,
    int CurrentVersions,
    long TotalBytes,
    string Directory,
    IReadOnlyList<DocumentEvidenceDto> Items);

/// <summary>What the bounded local cleanup found and removed (specification task 39, case T39.03).</summary>
public sealed record DocumentEvidenceCleanupDto(
    int Examined,
    IReadOnlyList<string> RemovedKeys,
    int ReferencedKept,
    int TooYoungToJudge,
    DateTimeOffset MeasuredAtUtc);

/// <summary>
/// The typed contract for local document evidence (specification task 39). One client, so a screen cannot disagree
/// with the routes the server publishes, and so evidence moves only over the authenticated channel the host already
/// uses for every other write.
/// </summary>
public interface IDocumentApiClient
{
    Task<IReadOnlyList<DocumentEvidenceDto>> ListEvidenceAsync(
        string aggregateType,
        Guid aggregateId,
        bool includeHistory = true,
        CancellationToken cancellationToken = default);

    Task<DocumentEvidenceDto> AttachAsync(AttachDocumentEvidenceRequest request, CancellationToken cancellationToken = default);

    Task<DocumentEvidenceDto> ReplaceAsync(
        Guid replacesId,
        ReplaceDocumentEvidenceRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentContentDto> DownloadAsync(Guid evidenceId, CancellationToken cancellationToken = default);

    Task<DocumentEvidenceInventoryDto> GetInventoryAsync(CancellationToken cancellationToken = default);

    Task<DocumentEvidenceCleanupDto> CleanOrphanedEvidenceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules the evidence screen and the server both quote, so a stated limit is the limit that is enforced
/// (specification task 39). The types are the ones a self-hosted instance will accept on its own disk: a PDF, a
/// JPEG or a PNG, recognised by their own leading bytes, and nothing that could run or render in a browser.
/// </summary>
public static class DocumentEvidenceRules
{
    /// <summary>The largest single piece of evidence this instance will hold, in bytes.</summary>
    public const int MaxFileBytes = 5_242_880;

    /// <summary>The largest number of versions one aggregate may hold.</summary>
    public const int MaxVersionsPerAggregate = 20;

    /// <summary>How long a file with no metadata row is left alone before the cleanup may judge it orphaned.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(15);

    /// <summary>The longest title an operator may give a piece of evidence.</summary>
    public const int MaxTitleLength = 200;

    public static readonly IReadOnlyList<string> AcceptedContentTypes = ["application/pdf", "image/jpeg", "image/png"];

    public const string AggregateRule =
        "Evidence belongs to one document - an invoice, an expense, a purchase order or a goods receipt - and only "
        + "someone who may manage that document may attach, replace or read it.";

    public const string StorageRule =
        "The bytes are written to a private directory of this instance, outside the part of it a browser can reach. "
        + "A name, a path or a link supplied by a caller is never used, and nothing is fetched from anywhere.";

    public const string TypeRule =
        "A PDF, JPEG or PNG is recognised from its own leading bytes. Anything that could run or render - a script, "
        + "an HTML or SVG document, an executable - is refused before a single byte reaches the disk.";

    public const string HistoryRule =
        "Replacing evidence adds a version and keeps the one it replaced readable while the retention rule reaches.";

    public const string BackupRule =
        "The evidence inventory - every version with its digest and size - belongs in a backup next to the database "
        + "dump, because the bytes themselves live outside the database.";

    /// <summary>The sentence the screen shows when a file is too big, naming the bound it hit.</summary>
    public static string SizeSentence(long bytes)
        => $"A single piece of evidence is at most {MaxFileBytes:N0} bytes; this one is {bytes:N0}.";

    /// <summary>
    /// A bare, safe download name composed from an operator's title. Display text can hold anything at all, so the
    /// name is rebuilt from the characters a file name may carry and the version this evidence is. The extension is
    /// checked against the same closed list the server writes, because a name composed here is a name that will be
    /// saved on somebody's disk.
    /// </summary>
    public static string ComposeFileName(string title, int version, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var normalizedExtension = extension.Trim().TrimStart('.').ToLowerInvariant();

        if (!AllowedExtensions.Contains(normalizedExtension, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"A saved file carries one of {string.Join(", ", AllowedExtensions)}; '{extension}' is not one of them.",
                nameof(extension));
        }

        var builder = new System.Text.StringBuilder(Math.Min(title.Length, 80) + 16);

        foreach (var character in title ?? string.Empty)
        {
            if (builder.Length >= 80)
            {
                break;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var stem = builder.ToString().Trim('-');

        if (stem.Length == 0)
        {
            stem = "evidence";
        }

        return $"{stem}-v{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{normalizedExtension}";
    }

    /// <summary>The extensions this instance will name a saved file with - the same list it is willing to hold.</summary>
    private static readonly IReadOnlyList<string> AllowedExtensions = ["pdf", "jpg", "jpeg", "png"];

    /// <summary>What the screen says about a version's place in the history.</summary>
    public static string VersionWords(DocumentEvidenceDto evidence)
        => evidence.Current
            ? $"version {evidence.Version} - held now"
            : evidence.ReplacedById is null
                ? $"version {evidence.Version} - {evidence.Status.ToLowerInvariant()}"
                : $"version {evidence.Version} - replaced, and still readable under the retention rule";
}
