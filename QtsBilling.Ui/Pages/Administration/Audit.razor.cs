using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Audit;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Diagnostics;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// The audit feed as an operator works it (specification task 34, recipe step 1): who did what, to which record, when,
/// under which correlation - filtered by record type, action or date, paged by the server's own cursor. The feed is
/// append-only, so this screen carries no edit, no delete and no clear: a wrong entry is answered by the entry that
/// corrects it, and the facts stay under retention (D10, task 34's recovery note).
/// </summary>
public partial class Audit : IAsyncDisposable
{
    [Inject] private IOperationsApiClient Operations { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;

    private const int PageSize = 50;

    private IReadOnlyList<AuditEventDto> _rows = [];
    private string? _nextCursor;
    private readonly List<string?> _cursors = [];
    private string? _cursor;

    private string _entityType = string.Empty;
    private string _action = string.Empty;
    private DateTime? _from;
    private DateTime? _to;
    private string? _error;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _mayRead;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private bool CanSearch => _mayRead && !_busy;

    private bool CanGoNext => !string.IsNullOrWhiteSpace(_nextCursor);

    protected override void OnInitialized() => _state = ShellStateKind.Loading;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;

        try
        {
            var session = await AccountsApi.GetSessionAsync().ConfigureAwait(false);
            _mayRead = session?.Permissions.Contains(Permissions.AuditView, StringComparer.Ordinal) == true;
            _state = _mayRead ? ShellStateKind.Empty : ShellStateKind.Forbidden;
            _message = _mayRead
                ? "Nothing has been read yet. Narrow the feed by record type, action or date, then search."
                : $"Reading the audit feed needs the '{Permissions.AuditView}' authority, which this account does not hold here.";
        }
        catch (ApiException)
        {
            _mayRead = false;
            _state = ShellStateKind.Unauthenticated;
            _message = "Sign in again to read the audit feed.";
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so no audit entries were read.";
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    /// <summary>A new filter is a new query: the cursor trail starts over, so no page is mixed across filters.</summary>
    private async Task SearchAsync()
    {
        _cursor = null;
        _cursors.Clear();
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task NextPageAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        _cursors.Add(_cursor);
        _cursor = _nextCursor;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task PreviousPageAsync()
    {
        if (_cursors.Count == 0)
        {
            return;
        }

        _cursor = _cursors[^1];
        _cursors.RemoveAt(_cursors.Count - 1);
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _busy = true;
        _error = null;

        try
        {
            var page = await Operations.ListAuditEventsAsync(
                NullIfBlank(_entityType),
                NullIfBlank(_action),
                from: _from is { } start ? DateOnly.FromDateTime(start) : null,
                to: _to is { } end ? DateOnly.FromDateTime(end) : null,
                cursor: _cursor,
                pageSize: PageSize,
                cancellationToken: _pending.Token).ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            _rows = page.Items;
            _nextCursor = page.NextCursor;
            _state = _rows.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            _message = _rows.Count == 0
                ? "No audit entry matches that filter in this business. An empty feed is an answer, not an error."
                : $"{_rows.Count} entr(s) on this page, read newest first"
                    + (_nextCursor is null ? "." : ", more on the next page.");
        }
        catch (ApiException exception)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = DiagnosticsRules.RefusalSentence(exception, "the audit feed");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
        }
        catch (HttpRequestException)
        {
            _error = "The business server could not be reached, so the feed was left as it was.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The words for a stored action, so a reader is not asked to guess a code name.</summary>
    private static string ActionWords(string action) => action switch
    {
        "updated" => "changed",
        "posted" => "posted to the ledger",
        "exported" => "taken out of the instance",
        "revoked" => "revoked",
        "closed" => "closed",
        "void" or "voided" => "voided",
        _ => action,
    };
}
