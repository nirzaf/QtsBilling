using QtsBilling.Contracts.Abstractions;

namespace QtsBilling.Ui.Layout;

/// <summary>One page of rows plus the cursor that continues them.</summary>
public sealed record PagedRows<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// The list behaviour every administration, catalogue and register screen needs, written once: server-side paging
/// and search, cancellation of a load the operator has already superseded, and the honest state the screen is in
/// when the answer comes back (specification task 02 established the cancellation rule for the invoice list;
/// task 13 established the states; task 14 makes both reusable so no screen re-invents them badly).
/// </summary>
/// <remarks>
/// The component owning an instance only repaints and renders <see cref="ShellStates"/> from
/// <see cref="State"/>. Nothing here touches HTTP or a database: the loader is the caller's typed client call.
/// </remarks>
public sealed class PagedListState<T> : IAsyncDisposable
{
    private readonly Func<string?, string?, int, CancellationToken, Task<PagedRows<T>>> _load;
    private readonly Action _repaint;
    private readonly int _pageSize;

    private readonly Stack<string?> _back = new();
    private CancellationTokenSource? _inFlight;
    private IReadOnlyList<T> _rows = [];
    private string _search = string.Empty;
    private string? _cursor;
    private string? _nextCursor;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;

    public PagedListState(
        Func<string?, string?, int, CancellationToken, Task<PagedRows<T>>> load,
        Action repaint,
        int pageSize = 25)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _repaint = repaint ?? throw new ArgumentNullException(nameof(repaint));
        _pageSize = Math.Clamp(pageSize, 1, 200);
    }

    /// <summary>The rows the newest completed load returned.</summary>
    public IReadOnlyList<T> Rows => _rows;

    /// <summary>The term the current page was searched with.</summary>
    public string Search => _search;

    public bool IsBusy => _busy;

    /// <summary>What the screen shows: content, empty, an ended session, a refusal, a lost race, or a failure.</summary>
    public ShellStateKind State => _state;

    /// <summary>The screen's own sentence when the default state wording is not specific enough.</summary>
    public string? Message => _message;

    public bool CanGoPrevious => _back.Count > 0;

    public bool CanGoNext => !string.IsNullOrWhiteSpace(_nextCursor);

    /// <summary>A new term is a new list: the paging history belonged to the term that produced it.</summary>
    public Task SearchAsync(string? term)
    {
        _search = term?.Trim() ?? string.Empty;
        return RestartAsync();
    }

    /// <summary>Back to the first page of the current term.</summary>
    public Task FirstPageAsync() => RestartAsync();

    /// <summary>Reads the current page again without moving the cursor.</summary>
    public Task ReloadAsync() => LoadAsync();

    public async Task NextPageAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        _back.Push(_cursor);
        _cursor = _nextCursor;
        _nextCursor = null;
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task PreviousPageAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        _cursor = _back.Pop();
        _nextCursor = null;
        await LoadAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        // Leaving the screen must not leave a load that can still write into state nobody is reading.
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
        return ValueTask.CompletedTask;
    }

    private Task RestartAsync()
    {
        _cursor = null;
        _nextCursor = null;
        _back.Clear();
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Any load still in flight is superseded the moment this one starts, so its response can never replace
        // the rows the operator is looking at now.
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        var cts = new CancellationTokenSource();
        _inFlight = cts;
        var token = cts.Token;

        _busy = true;
        _message = null;
        _repaint();

        try
        {
            var page = await _load(_search, _cursor, _pageSize, token).ConfigureAwait(false);
            Apply(page, null, token);
        }
        catch (OperationCanceledException)
        {
            // Discarded on purpose: a newer load owns the screen now and will paint it.
        }
        catch (ApiException exception)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // A paging cursor that the server no longer honours is not the operator's mistake to interpret: the
            // list moved, so the screen goes back to the first page and says so.
            if (exception.IsConflict && _cursor is not null)
            {
                _cursor = null;
                _nextCursor = null;
                _back.Clear();

                try
                {
                    var page = await _load(_search, null, _pageSize, token).ConfigureAwait(false);
                    _state = ShellStateKind.Conflict;
                    _message = "The list changed while paging, so this shows the first page again. Nothing was lost.";
                    _rows = page.Items;
                    _nextCursor = page.NextCursor;
                }
                catch (ApiException retryFailure) when (!token.IsCancellationRequested)
                {
                    Apply(null, retryFailure, token);
                }
                catch (OperationCanceledException)
                {
                    // Superseded again; the newest load will paint.
                }
            }
            else
            {
                Apply(null, exception, token);
            }
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cts))
            {
                _busy = false;
                _repaint();
            }
        }
    }

    private void Apply(PagedRows<T>? page, ApiException? failure, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (failure is not null)
        {
            _message = failure.Message;
            _state = failure.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            return;
        }

        _rows = page?.Items ?? [];
        _nextCursor = page?.NextCursor;
        _state = _rows.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;

        // An empty result is not an error, and a search that found nothing is a different sentence from a
        // business with no records at all.
        _message = _rows.Count == 0
            ? string.IsNullOrWhiteSpace(_search)
                ? "Nothing has been created for this business yet."
                : $"Nothing matches \"{_search}\". The search runs on the server, so it covers every record, not only the first page."
            : null;
    }
}
