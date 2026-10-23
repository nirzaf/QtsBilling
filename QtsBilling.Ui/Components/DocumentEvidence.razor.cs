using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Documents;

namespace QtsBilling.Ui.Components;

/// <summary>
/// The evidence panel of one document (specification task 39, recipe step 4). It lists the versions this instance
/// holds, offers a file the operator picks from their own disk, and hands a held file back to that same disk through
/// the host's own save facility. The panel never composes a URL for a file, never opens one inline, and never claims a
/// copy exists when the device refused it.
/// </summary>
/// <remarks>
/// The attach act is held until the operator confirms it, because a chosen file is a decision about what becomes part
/// of a document's record - and a screen that writes on the way past a file picker writes facts nobody agreed to.
/// </remarks>
public partial class DocumentEvidence : IDisposable
{
    [Inject] private IDocumentApiClient Documents { get; set; } = default!;
    [Inject] private ILocalFileOutput Files { get; set; } = default!;

    /// <summary>One of <see cref="DocumentAggregates" />. The server refuses anything else.</summary>
    [Parameter, EditorRequired] public string AggregateType { get; set; } = string.Empty;

    [Parameter, EditorRequired] public Guid AggregateId { get; set; }

    /// <summary>
    /// Whether the signed-in membership may manage this kind of document. Only ever an affordance: the route answers
    /// on its own authority whatever this says.
    /// </summary>
    [Parameter] public bool MayManage { get; set; }

    private IReadOnlyList<DocumentEvidenceDto> _rows = [];
    private PendingFile? _pending;
    private string _title = string.Empty;
    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _loaded;
    private CancellationTokenSource _pendingCancellation = new();

    private string AggregateWords => AggregateType.Replace('_', ' ');

    private bool CanAttach => !string.IsNullOrWhiteSpace(_title) && !string.IsNullOrWhiteSpace(AggregateType) && AggregateId != Guid.Empty;

    private string AttachBlock => string.IsNullOrWhiteSpace(_title)
        ? "Name the file first: evidence without a title is a blob nobody can find again."
        : "This document has no identifier yet, so nothing can be attached to it.";

    public void Dispose()
    {
        _pendingCancellation.Cancel();
        _pendingCancellation.Dispose();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_loaded)
        {
            _loaded = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private Task LoadAsync()
    {
        if (AggregateId == Guid.Empty)
        {
            _rows = [];
            _error = null;
            return Task.CompletedTask;
        }

        return ReadAsync();
    }

    private async Task ReadAsync()
    {
        _busy = true;
        _error = null;

        try
        {
            _rows = await Documents.ListEvidenceAsync(AggregateType, AggregateId, cancellationToken: _pendingCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
            _rows = [];
        }
        catch (HttpRequestException)
        {
            _error = "The instance did not answer, so nothing is known about its evidence. Read it again when it is reachable.";
            _rows = [];
        }
        catch (OperationCanceledException)
        {
            // A superseded read: the operator asked again, or left. Nothing is said about it.
        }

        _busy = false;
    }

    private async Task AttachAsync(InputFileChangeEventArgs args)
    {
        _error = null;
        _notice = null;
        _pending = null;

        if (args?.File is not { } file)
        {
            return;
        }

        byte[] content;

        try
        {
            await using var stream = file.OpenReadStream(DocumentEvidenceRules.MaxFileBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, _pendingCancellation.Token).ConfigureAwait(false);
            content = buffer.ToArray();
        }
        catch (IOException)
        {
            _error = "The chosen file could not be read from this device. Nothing was sent.";
            return;
        }
        catch (InvalidOperationException)
        {
            _error = DocumentEvidenceRules.SizeSentence(file.Size);
            return;
        }

        if (content.Length == 0)
        {
            _error = "That file has no bytes, so it is not evidence of anything.";
            return;
        }

        _pending = new PendingFile(file.Name, content.LongLength, content, file.ContentType);
    }

    private async Task ConfirmAttachAsync()
    {
        if (_pending is not { } pending || !CanAttach)
        {
            return;
        }

        _busy = true;
        _error = null;

        try
        {
            var attached = await Documents.AttachAsync(
                new AttachDocumentEvidenceRequest(
                    AggregateType,
                    AggregateId,
                    _title.Trim(),
                    pending.ClaimedContentType,
                    pending.Content,
                    null),
                _pendingCancellation.Token).ConfigureAwait(false);

            _notice = $"Version {attached.Version} is held on this instance now, as {attached.ContentType}, "
                + $"{BytesWords(attached.SizeBytes)} with the digest {Short(attached.Sha256)}.";

            _pending = null;
            _title = string.Empty;
            await ReadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (HttpRequestException)
        {
            _error = "The instance did not answer, so no evidence is claimed. The file is still on this device.";
        }
        catch (OperationCanceledException)
        {
            _error = "This panel went away before the evidence was placed.";
        }

        _busy = false;
    }

    private void DiscardPending()
    {
        _pending = null;
        _notice = null;
    }

    private async Task KeepAsync(DocumentEvidenceDto evidence)
    {
        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var content = await Documents.DownloadAsync(evidence.Id, _pendingCancellation.Token).ConfigureAwait(false);

            if (content.Sha256 != evidence.Sha256)
            {
                _error = "The bytes that came back do not match the digest this record holds, so they are not "
                    + "offered as a copy of it.";
                return;
            }

            var saved = await Files.SaveAsync(new StoredFile(content.FileName, content.Content), _pendingCancellation.Token)
                .ConfigureAwait(false);

            _notice = saved
                ? $"Kept on this device as {content.FileName} ({BytesWords(content.SizeBytes)}). Nothing was uploaded anywhere."
                : $"This device refused the file, so no copy was made. The version held here is still {Short(content.Sha256)}.";
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        catch (HttpRequestException)
        {
            _error = "The instance did not answer, so no copy was made.";
        }
        catch (ArgumentException argumentException)
        {
            _error = argumentException.Message;
        }
        catch (OperationCanceledException)
        {
            // The operator moved on. Nothing is claimed either way.
        }

        _busy = false;
    }

    private static string Short(string sha256) => sha256.Length <= 12 ? sha256 : sha256[..12];

    private static string BytesWords(long bytes)
        => bytes switch
        {
            < 1_024 => $"{bytes} bytes",
            < 1_048_576 => $"{bytes / 1_024.0:0.#} KB",
            _ => $"{bytes / 1_048_576.0:0.#} MB",
        };

    /// <summary>A file the operator has chosen and not yet agreed to place on the record.</summary>
    private sealed record PendingFile(string Name, long Bytes, byte[] Content, string? ClaimedContentType);
}
