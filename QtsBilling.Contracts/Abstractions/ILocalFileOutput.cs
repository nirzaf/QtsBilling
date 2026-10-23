namespace QtsBilling.Contracts.Abstractions;

/// <summary>
/// One file the operator asked to keep locally. The name is a bare file name - never a path - and the content is
/// bounded, because a report export becomes a disk write the moment it is accepted and a name carrying a separator
/// or a parent segment is a directory, a drive or somebody else's file (specification task 33, recipe step 3).
/// </summary>
public sealed record StoredFile
{
    /// <summary>The largest export any host will put on disk. The server's own bound is reached long before this.</summary>
    public const int MaxContentBytes = 16_000_000;

    private const int MaxNameLength = 120;

    /// <summary>
    /// Characters no file name may hold, on any host. The list is fixed here instead of read from
    /// <see cref="Path.GetInvalidFileNameChars"/> because a report composed by a Linux server or a browser tab is
    /// saved on the device the operator is standing at, which may be Windows: a name that is legal on one and
    /// illegal on the other is a smuggled path, so the union is refused everywhere.
    /// </summary>
    private static readonly char[] ForbiddenNameCharacters =
    [
        '\0', '"', '*', '/', ':', '<', '>', '?', '\\', '|',
    ];

    public StoredFile(string fileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var trimmed = fileName.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"A saved report is named in at most {MaxNameLength} characters.", nameof(fileName));
        }

        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.IndexOfAny(ForbiddenNameCharacters) >= 0
            || Path.GetFileName(trimmed) != trimmed)
        {
            throw new ArgumentException("A saved report carries a bare file name, never a path or a parent segment.", nameof(fileName));
        }

        if (!trimmed.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only the CSV report export is offered as a local file.", nameof(fileName));
        }

        if (content.Length == 0)
        {
            throw new ArgumentException("An empty export is not a report; read the statement again.", nameof(content));
        }

        if (content.Length > MaxContentBytes)
        {
            throw new ArgumentException(
                $"A local export is at most {MaxContentBytes} bytes. Narrow the window or the branch and read it again.",
                nameof(content));
        }

        FileName = trimmed;
        Content = content;
    }

    public string FileName { get; }

    public byte[] Content { get; }

    public int Bytes => Content.Length;
}

/// <summary>
/// Puts bytes the server produced into the operator's own hands, on the operator's own machine (specification task
/// 33). No BI service, no upload and no remote share is involved: the browser saves through its own download, the
/// desktop through the same bridge, and a host that cannot put a file anywhere says so instead of pretending.
/// </summary>
public interface ILocalFileOutput
{
    /// <summary>
    /// Whether this host can hand a file to the operator at all. A static server render has no client to talk to,
    /// so the screen explains that instead of offering a button that does nothing.
    /// </summary>
    bool CanSaveFiles { get; }

    /// <summary>
    /// Writes the file where the operator's device keeps it. Returns <see langword="false"/> when the host could not
    /// place it, in which case the screen keeps the statement on display and says the export did not happen.
    /// </summary>
    Task<bool> SaveAsync(StoredFile file, CancellationToken cancellationToken = default);
}
