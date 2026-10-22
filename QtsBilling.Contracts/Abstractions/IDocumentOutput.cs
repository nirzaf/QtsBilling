namespace QtsBilling.Contracts.Abstractions;

/// <summary>
/// A committed document snapshot that the host has already fetched through its own authenticated client, ready
/// for local output.
/// </summary>
/// <remarks>
/// The record carries <em>content</em> and never a location (specification task 24, recipe step 3). That is the
/// deliberate shape of the whole print contract: a URL would let a caller point a browser tab, a WebView or a
/// print driver at an arbitrary origin, and the only way to authenticate a bearer-protected document by URL is
/// to put the token in it - where it lands in history, in the address bar, in server logs and in anything the
/// operator copies. Nothing here can express either of those, so no host can be tricked into doing it.
/// A snapshot is also refused before it reaches any host: content that is not a self-contained HTML document is
/// not a printable document, whatever a caller labelled it.
/// </remarks>
public sealed record DocumentOutputRequest
{
    /// <summary>The largest snapshot any host is asked to print. The server's own document renderer is far below it.</summary>
    public const int MaxContentLength = 2_000_000;

    private const int MaxTitleLength = 120;

    public DocumentOutputRequest(string content, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (content.Length > MaxContentLength)
        {
            throw new ArgumentException(
                $"A printable document is at most {MaxContentLength} characters, and this payload is larger.",
                nameof(content));
        }

        // A URL, a redirect body or an error page is not a document. Refusing here means no host adapter can
        // ever be handed something it would have to navigate to - which is where a token would have to appear.
        if (LooksLikeLocation(content))
        {
            throw new ArgumentException(
                "A print request carries the document's own content, never a location to fetch it from.",
                nameof(content));
        }

        // The server renders a self-contained page (specification task 10: no external asset may be reachable),
        // so anything that is not such a page did not come from the document renderer.
        if (!content.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Only the server's own HTML document snapshot can be handed to the printer.",
                nameof(content));
        }

        if (title.Length > MaxTitleLength || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"A document name is at most {MaxTitleLength} characters and must not contain path or file-name characters.",
                nameof(title));
        }

        Content = content;
        Title = title.Trim();
    }

    /// <summary>The complete HTML document, exactly as the authenticated server returned it.</summary>
    public string Content { get; init; }

    /// <summary>What the operator knows this document as, for example <c>Invoice INV-0007</c>.</summary>
    public string Title { get; init; }

    private static bool LooksLikeLocation(string content)
    {
        var trimmed = content.AsSpan().TrimStart();

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Local document output for one committed snapshot. Each host implements it with its own facilities: the
/// browser hands the page to the browser's print dialog, the device hands it to the operating system's printer
/// list (which includes Print to PDF). Neither calls a network service, neither navigates, neither uploads.
/// </summary>
public interface IDocumentOutput
{
    /// <summary>
    /// Whether this host can put a document in front of a printer at all. A static server render has no client
    /// to talk to, so the screen says so instead of offering a button that does nothing.
    /// </summary>
    bool CanPresentDocuments { get; }

    /// <summary>
    /// Prints or exports the snapshot. Returns <see langword="false"/> when the host could not present it, in
    /// which case the screen keeps the document visible on screen instead of claiming a print happened.
    /// </summary>
    Task<bool> PresentAsync(DocumentOutputRequest request, CancellationToken cancellationToken = default);
}
