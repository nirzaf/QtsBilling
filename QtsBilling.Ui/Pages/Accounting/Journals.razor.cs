using System.Globalization;
using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Accounting;

/// <summary>
/// The journal: what has been posted, what one entry is made of, a manual entry offered for posting, and a posted
/// entry answered by a reversal (specification task 32, recipe steps 1-2 and 4). Every figure the screen quotes is
/// read back from the server; the totals shown while an entry is being built are the screen repeating its own lines
/// for review, never a balance the ledger would accept on trust, and the trial balance is derived from posted lines
/// only. A posted entry is never edited or deleted here: a mistake is answered by a new entry that names it (D10).
/// </summary>
public partial class Journals : IAsyncDisposable
{
    private static readonly (JournalEntryState State, string Word)[] StatusChoices =
    [
        (JournalEntryState.Posted, "Posted"),
        (JournalEntryState.Draft, "Draft"),
    ];

    private static readonly (JournalOrigin Origin, string Word)[] OriginChoices =
    [
        .. Enum.GetValues<JournalOrigin>().Select(origin => (origin, origin.ToString())),
    ];

    [Inject] private IAccountingApiClient Ledger { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;

    private readonly List<PostingLine> _lines = [];
    private readonly HeldIntention<PostJournalEntryRequest> _post = new("the journal entry");
    private readonly HeldIntention<ReverseJournalRequest> _reverse = new("the reversal");

    private IReadOnlyList<JournalEntrySummaryDto> _entries = [];
    private IReadOnlyList<AccountDto> _chart = [];
    private string? _nextCursor;
    private JournalEntryDto? _selected;
    private TrialBalanceDto? _trial;
    private int _scale = 2;

    private string _statusFilter = "Posted";
    private string _originFilter = string.Empty;
    private DateTime? _from;
    private DateTime? _to;
    private DateTime? _trialFrom;
    private DateTime? _trialTo;
    private DateTime? _entryDate;
    private string _memo = string.Empty;
    private string? _currency;
    private DateTime? _correctionDate;
    private string? _correctionReason;
    private string? _error;
    private string? _notice;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _canPost;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private bool HasMore => _nextCursor is not null;

    private bool CanPostNow
        => AccountingRules.CanPost(_canPost, _entryDate is not null, EnteredLines.Count) && !_post.Outstanding && !_busy;

    private IReadOnlyList<PostingLine> EnteredLines
        => _lines.Where(line => line.Account is not null && !line.IsEmpty).ToList();

    private decimal TotalDebit => EnteredLines.Sum(line => line.DebitAmount);

    private decimal TotalCredit => EnteredLines.Sum(line => line.CreditAmount);

    protected override void OnInitialized()
    {
        _state = ShellStateKind.Loading;
        _lines.Add(new PostingLine());
        _lines.Add(new PostingLine());
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        return default;
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        _busy = true;
        _error = null;
        _state = ShellStateKind.Loading;
        _message = null;
        _nextCursor = null;
        _entries = [];

        try
        {
            var session = await AccountsApi.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = "Sign in to read the journal.";
                return;
            }

            _canPost = session.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal);

            _chart = await Ledger.ListAccountsAsync(cancellationToken: _pending.Token).ConfigureAwait(false);
            _scale = (await Ledger.GetSettingsAsync(_pending.Token).ConfigureAwait(false)).SettlementScale;
            var page = await PageAsync(null, _pending.Token).ConfigureAwait(false);
            _entries = page.Items;
            _nextCursor = page.NextCursor;
            _state = _entries.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            if (_entries.Count == 0)
            {
                _message = "Nothing has been posted under these filters yet. Widen the dates or clear the filters to read the journal again.";
            }
            else
            {
                _selected = await Ledger.GetJournalAsync(_entries[0].Id, _pending.Token).ConfigureAwait(false);
            }
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = _state == ShellStateKind.Forbidden
                ? $"Reading the journal needs the '{Permissions.ReportsView}' authority, which this account does not hold in this business."
                : AccountingRules.RefusalSentence(exception, "the journal");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure; a slow page for a filter nobody is looking at is dropped.
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so nothing was read and nothing was posted.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }
    }

    /// <summary>One page of the journal under the filters on screen, newest first.</summary>
    private async Task<PagedResult<JournalEntrySummaryDto>> PageAsync(
        string? cursor, CancellationToken cancellationToken = default)
        => await Ledger.ListJournalsAsync(
            cursor,
            25,
            string.IsNullOrWhiteSpace(_statusFilter) ? null : _statusFilter,
            string.IsNullOrWhiteSpace(_originFilter) ? null : _originFilter,
            _from is { } start ? DateOnly.FromDateTime(start) : null,
            _to is { } end ? DateOnly.FromDateTime(end) : null,
            cancellationToken).ConfigureAwait(false);

    private async Task LoadMoreAsync()
    {
        if (_nextCursor is not { Length: > 0 } cursor)
        {
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            var page = await PageAsync(cursor).ConfigureAwait(false);
            _entries = [.. _entries, .. page.Items];
            _nextCursor = page.NextCursor;
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "the next page of the journal");
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task SelectAsync(Guid entryId)
    {
        _busy = true;
        _error = null;
        try
        {
            _selected = await Ledger.GetJournalAsync(entryId).ConfigureAwait(false);
            _correctionReason = null;
            _correctionDate = null;
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "that entry");
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task ReadTrialBalanceAsync()
    {
        if (_trialFrom is not { } trialStart || _trialTo is not { } trialEnd)
        {
            _error = "A trial balance is asked for over a window: choose the first and last day it should cover.";
            return;
        }

        var from = DateOnly.FromDateTime(trialStart);
        var to = DateOnly.FromDateTime(trialEnd);
        _busy = true;
        _error = null;
        try
        {
            _trial = await Ledger.GetTrialBalanceAsync(from, to).ConfigureAwait(false);
            _notice = $"Trial balance {_trial.From:yyyy-MM-dd} to {_trial.To:yyyy-MM-dd}: "
                + $"{Money(_trial.TotalDebit)} debited against {Money(_trial.TotalCredit)} credited over {_trial.Rows.Count} accounts. "
                + (_trial.TotalDebit == _trial.TotalCredit
                    ? "The books are in balance for this window."
                    : "They do not balance, which is a fact to answer, not a figure to round.");
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "the trial balance");
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private void AddLine() => _lines.Add(new PostingLine());

    private void RemoveLine(PostingLine line)
    {
        if (_lines.Count > 2)
        {
            _lines.Remove(line);
        }
    }

    private string BalanceSentence
        => EnteredLines.Count == 0
            ? "Nothing entered yet. A journal needs at least two lines with an amount on one side."
            : TotalDebit == TotalCredit
                ? $"These lines add to {Money(TotalDebit)} on each side. The server decides again - at its own scale, against the period the effective date falls in."
                : $"These lines are {Money(TotalDebit)} debited against {Money(TotalCredit)} credited. An unbalanced entry is refused whole and nothing is written until it balances.";

    private async Task PostAsync()
    {
        if (_entryDate is not { } date)
        {
            _error = "Choose the date the entry takes effect. It decides which accounting period owns the entry.";
            return;
        }

        if (EnteredLines.Count < 2)
        {
            _error = "A journal entry needs at least two lines, each with an account and an amount on one side.";
            return;
        }

        var body = new PostJournalEntryRequest(
            DateOnly.FromDateTime(date),
            string.IsNullOrWhiteSpace(_memo) ? null : _memo.Trim(),
            string.IsNullOrWhiteSpace(_currency) ? null : _currency.Trim(),
            JournalOrigin.Manual,
            null,
            [.. EnteredLines.Select(line => new JournalLineRequest(
                line.Account!.Id, line.DebitAmount, line.CreditAmount,
                string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim(), null))]);

        var key = _post.Key ??= AccountingRules.NewKey("journal-post", Guid.CreateVersion7());
        _post.Body = body;

        await SendAsync(_post, async () =>
        {
            _selected = await Ledger.PostJournalAsync(body, key).ConfigureAwait(false);
            _entryDate = null;
            _memo = string.Empty;
            _currency = null;
            _lines.Clear();
            _lines.Add(new PostingLine());
            _lines.Add(new PostingLine());
        }, "the journal entry").ConfigureAwait(false);
    }

    private async Task ReverseAsync()
    {
        if (_selected is not { } entry)
        {
            _error = "Choose the posted entry this correction answers.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_correctionReason))
        {
            _error = "A reversal is recorded with the reason an auditor will read.";
            return;
        }

        var body = new ReverseJournalRequest(
            _correctionDate is { } date ? DateOnly.FromDateTime(date) : null, _correctionReason.Trim());
        var key = _reverse.Key ??= AccountingRules.NewKey($"journal-reverse-{entry.EntryNumber}", entry.Id);
        _reverse.Body = body;

        await SendAsync(_reverse, async () =>
        {
            _selected = await Ledger.ReverseJournalAsync(entry.Id, body, key).ConfigureAwait(false);
        }, $"the reversal of {entry.EntryNumber}").ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one act under its held key. A lost answer keeps the key and the exact figures, so the button asks the
    /// server about the same act instead of posting a second journal (specification F04).
    /// </summary>
    private async Task SendAsync<TBody>(HeldIntention<TBody> intention, Func<Task> send, string what)
    {
        _busy = true;
        _error = null;
        _notice = null;
        try
        {
            await send().ConfigureAwait(false);
            _notice = $"{char.ToUpperInvariant(what[0])}{what[1..]} answered.";
            intention.Release();
            await ReloadListAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = exception.Errors.Count > 0
                ? $"{AccountingRules.RefusalSentence(exception, what)} {string.Join(' ', exception.Errors.Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}"))}"
                : AccountingRules.RefusalSentence(exception, what);
            if (intention.Key is not null && AccountingRules.CommittedNothing(exception.Code))
            {
                intention.Release();
            }
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task ReloadListAsync()
    {
        try
        {
            var page = await PageAsync(null).ConfigureAwait(false);
            _entries = page.Items;
            _nextCursor = page.NextCursor;
            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "the journal re-read");
        }
    }

    private string Money(decimal amount) => AccountingRules.Money(amount, _scale);

    /// <summary>The chart, chosen by name or code - no account is ever typed as an identifier (R07).</summary>
    private async Task<IReadOnlyList<ReferenceItem>> SearchChartAsync(string term, CancellationToken cancellationToken)
    {
        var wanted = term?.Trim() ?? string.Empty;
        IReadOnlyList<ReferenceItem> matches = _chart
            .Where(account => account.IsActive)
            .Where(account => wanted.Length == 0
                || account.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || account.Code.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(account => ReferenceItem.From(account.Id, account.Name, $"{account.Code} · {account.AccountType}"))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        await Task.CompletedTask;
        return matches;
    }

    private string AccountName(Guid accountId)
    {
        var account = _chart.FirstOrDefault(row => row.Id == accountId);
        return account is null ? "an account in this business" : $"{account.Code} {account.Name}";
    }

    /// <summary>The account column of a posted line, named the way the chart names it.</summary>
    private string LineAccount(JournalLineDto line)
        => $"{line.AccountCode} {line.AccountName}";

    private string SideSentence(JournalLineDto line)
        => line.Debit != 0m
            ? $"debited {Money(line.Debit)}"
            : $"credited {Money(line.Credit)}";

    private string OriginSentence(JournalOrigin origin) => origin switch
    {
        JournalOrigin.Manual => "posted by an accountant from this screen",
        JournalOrigin.Sale or JournalOrigin.Invoice => "produced by a sale or an invoice, and read-only here",
        JournalOrigin.Purchase => "produced by a supplier bill, credit or payment of one, and read-only here",
        JournalOrigin.Payment or JournalOrigin.PaymentAllocation => "produced by a settlement, and read-only here",
        JournalOrigin.Expense => "produced by an expense, and read-only here",
        JournalOrigin.Reversal => "a reversal that answers another entry, which stays as it was posted",
        JournalOrigin.FiscalYearClose => "the year-end transfer of revenue and expense into retained earnings",
        JournalOrigin.StockAdjustment => "produced by a stock count or adjustment, and read-only here",
        JournalOrigin.MigrationOpening => "an opening balance carried in from the migrated books",
        _ => "produced by a business document",
    };

    private (string Label, string Href)? SourceDocument => _selected is { } entry
        ? AccountingRules.SourceDocumentLink(entry.Origin, entry.SourceId)
        : null;

    /// <summary>One line of an entry being built for review. Amounts stay text until they are sent.</summary>
    private sealed class PostingLine
    {
        public ReferenceItem? Account { get; set; }

        public string Debit { get; set; } = string.Empty;

        public string Credit { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsEmpty => DebitAmount == 0m && CreditAmount == 0m;

        public decimal DebitAmount => Parse(Debit) ?? 0m;

        public decimal CreditAmount => Parse(Credit) ?? 0m;

        private static decimal? Parse(string? text)
            => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
