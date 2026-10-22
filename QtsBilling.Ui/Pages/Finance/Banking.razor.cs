using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Banking;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Finance;

/// <summary>
/// The bank-reconciliation workbench (specification task 31): a statement of the bank's own movements read against
/// what the booked bank account actually moved, the posted lines each movement could be reconciled to, a reasoned
/// correction that keeps the match it replaces, a reviewed journal for the fee or interest the bank charged, and a
/// finalisation that is refused while an exception is still visible. Every figure comes from the server, every
/// reference is chosen by name or number, and each write holds one key while its answer is outstanding.
/// </summary>
public partial class Banking : IAsyncDisposable
{
    [Inject] private IBankingApiClient BankingApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IAccountingApiClient Ledger { get; set; } = default!;

    private readonly HeldIntention<FinaliseStatementRequest> _finalise = new("finalise the statement");
    private readonly HeldIntention<ReopenStatementRequest> _reopen = new("reopen the review");
    private readonly HeldIntention<ImportBundle> _import = new("import the statement");
    private readonly Dictionary<Guid, Guid> _chosen = [];
    private readonly Dictionary<Guid, string> _reasons = [];
    private readonly Dictionary<Guid, ReferenceItem?> _movement = [];
    private readonly Dictionary<Guid, HeldIntention<object>> _lineIntents = [];

    private IReadOnlyList<BankAccountDto> _accounts = [];
    private IReadOnlyList<BankStatementImportDto> _statements = [];
    private BankStatementReviewDto? _review;
    private ReferenceItem? _account;
    private BankStatementImportDto? _statement;
    private string? _reason;
    private DateTime? _statementDate;
    private string? _openingBalance;
    private string? _closingBalance;
    private byte[]? _csvBytes;
    private string? _csvName;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _canPost;
    private bool _canClose;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    private IReadOnlyList<BankStatementLineDto> Lines => _review?.Statement.Lines ?? [];

    private bool Outstanding => _lineIntents.Values.Any(intent => intent.Outstanding)
        || _finalise.Outstanding || _reopen.Outstanding || _import.Outstanding;

    /// <summary>The size ceiling the server enforces on an uploaded statement file, quoted rather than guessed.</summary>
    public const int MaxStatementBytes = 5 * 1024 * 1024;

    private bool CanImport => _canPost && _account is not null && _csvBytes is { Length: > 0 } && _statementDate is not null;

    private bool IsFinalised => _review?.Statement.Import.Status == BankReconciliationState.Finalised;

    private IReadOnlyList<BankStatementCandidateDto> CandidatesFor(BankStatementLineDto line)
    {
        var row = _review?.Lines.FirstOrDefault(candidate => candidate.LineId == line.Id);
        return row?.Candidates ?? [];
    }

    private BankStatementLineReviewDto? ReviewFor(BankStatementLineDto line)
        => _review?.Lines.FirstOrDefault(row => row.LineId == line.Id);

    private Guid? ChosenFor(BankStatementLineDto line)
    {
        if (_chosen.TryGetValue(line.Id, out var chosen) && CandidatesFor(line).Any(candidate => candidate.JournalLineId == chosen))
        {
            return chosen;
        }

        // A single candidate is not a choice an operator has to make: it is offered as the default they confirm.
        var candidates = CandidatesFor(line);
        return candidates.Count == 1 ? candidates[0].JournalLineId : null;
    }

    private string ReasonFor(BankStatementLineDto line)
        => _reasons.TryGetValue(line.Id, out var stored) && !string.IsNullOrWhiteSpace(stored) ? stored : _reason ?? string.Empty;

    /// <summary>The account the fee or interest leg closes against, chosen by name - never typed as a code or key.</summary>
    private ReferenceItem? MovementAccountFor(BankStatementLineDto line)
        => _movement.TryGetValue(line.Id, out var chosen) ? chosen : null;

    protected override void OnInitialized() => _state = ShellStateKind.Loading;

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

        try
        {
            var session = await Accounts.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = "Sign in to read a bank statement.";
                return;
            }

            _canPost = session.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal);
            _canClose = session.Permissions.Contains(Permissions.AccountingClose, StringComparer.Ordinal);

            _accounts = await BankingApi.GetAccountsAsync(_pending.Token).ConfigureAwait(false);
            if (_accounts.Count == 0)
            {
                _account = null;
                _statements = [];
                _review = null;
                _statement = null;
                _state = ShellStateKind.Empty;
                _message = "No bank account is registered against a ledger account in this business yet. Register one on the accounting setup screen before a statement can be reconciled.";
                return;
            }

            // The account named first is the one read, so the screen opens on a real position rather than a form.
            _account ??= ReferenceItem.From(_accounts[0].Id, _accounts[0].Name, $"{_accounts[0].LedgerAccountCode} · {_accounts[0].Currency}");
            _statements = await BankingApi.ListStatementsAsync(_account.Id, _pending.Token).ConfigureAwait(false);
            if (_statements.Count == 0)
            {
                _review = null;
                _statement = null;
                _state = ShellStateKind.Empty;
                _message = $"No statement has been imported for {_account.Name}. Import the bank's CSV, then reopen this screen.";
                return;
            }

            _statement ??= _statements[0];
            _review = await BankingApi.GetReviewAsync(_statement.Id, _pending.Token).ConfigureAwait(false);
            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            if (exception.StatusCode is 401 or 403)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading a bank statement needs the '{Permissions.ReportsView}' authority, which this account does not hold in this business.";
                return;
            }

            _state = ShellStateKind.Failure;
            _message = BankingRules.RefusalSentence(exception, "the statement");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure: a slower answer for a statement nobody is looking at is dropped.
        }
        catch (HttpRequestException)
        {
            _state = ShellStateKind.Offline;
            _message = "The business server could not be reached, so nothing was read and nothing was changed.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _busy = false;
            }
        }
    }

    private async Task AccountChangedAsync(ReferenceItem? chosen)
    {
        if (chosen is null || chosen.Id == _account?.Id)
        {
            return;
        }

        _account = chosen;
        _statement = null;
        _review = null;
        _chosen.Clear();
        _reasons.Clear();
        _movement.Clear();
        await LoadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    private async Task StatementChangedAsync(BankStatementImportDto? chosen)
    {
        if (chosen is null || chosen.Id == _statement?.Id)
        {
            return;
        }

        _statement = chosen;
        await LoadAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    private async Task MatchAsync(BankStatementLineDto line)
    {
        var candidate = ChosenFor(line);
        if (candidate is not { } chosen)
        {
            _error = "Choose the posted journal line this movement is, or record the fee or interest it represents.";
            return;
        }

        var intent = IntentFor(line.Id, "match");
        var body = new MatchStatementLineRequest(chosen, line.ExpectedVersion);
        // A match is replay-safe by its own shape - the same line to the same posted entry answers the same
        // document - so the intention held here is the pair itself rather than a separate key.
        intent.Body = body;

        await SendAsync(intent, () => BankingApi.MatchLineAsync(line.Id, body), "the match").ConfigureAwait(false);
    }

    private async Task UnmatchAsync(BankStatementLineDto line)
    {
        var reason = ReasonFor(line);
        if (string.IsNullOrWhiteSpace(reason))
        {
            _error = "A correction needs the reason an auditor will read.";
            return;
        }

        if (line.MatchedJournalLineId is not { } prior)
        {
            _error = "That line is not matched to anything, so there is nothing to correct.";
            return;
        }

        var intent = IntentFor(line.Id, "unmatch");
        var body = new UnmatchStatementLineRequest(line.ExpectedVersion, prior, reason.Trim());
        var key = intent.Key ??= BankingRules.NewKey($"bank-unmatch-{line.LineNo}", line.Id);
        intent.Body = body;

        await SendAsync(intent, () => BankingApi.UnmatchLineAsync(line.Id, body, key), "the correction").ConfigureAwait(false);
    }

    private async Task MovementAsync(BankStatementLineDto line)
    {
        var reason = ReasonFor(line);
        if (string.IsNullOrWhiteSpace(reason))
        {
            _error = "A bank movement is posted as a reviewed journal, so it needs the reason the review recorded.";
            return;
        }

        if (!_movement.TryGetValue(line.Id, out var chosen) || chosen is null)
        {
            _error = $"Choose {BankingRules.CounterAccountKind(line)} to close the line against.";
            return;
        }

        var intent = IntentFor(line.Id, "movement");
        // The movement is booked on the day the bank moved the money, not on a date the screen chooses: a fee dated
        // whenever the reviewer felt like posting it would drift from the statement it explains.
        var body = new RecordStatementMovementRequest(chosen.Id, line.ExpectedVersion, reason.Trim());
        var key = intent.Key ??= BankingRules.NewKey($"bank-movement-{line.LineNo}", line.Id);
        intent.Body = body;

        await SendAsync(intent, () => BankingApi.RecordMovementAsync(line.Id, body, key), "the bank movement").ConfigureAwait(false);
    }

    private async Task FinaliseAsync()
    {
        if (_review is not { } review)
        {
            return;
        }

        if (!BankingRules.CanFinalise(review))
        {
            _error = BankingRules.FinaliseBlocker(review);
            return;
        }

        var importId = review.Statement.Import.Id;
        var body = new FinaliseStatementRequest(review.Statement.Import.ExpectedVersion);
        var intent = _finalise;
        var key = intent.Key ??= BankingRules.NewKey("bank-finalise", importId);
        intent.Body = body;

        await SendAsync(intent, () => BankingApi.FinaliseStatementAsync(importId, body, key), "the finalisation").ConfigureAwait(false);
    }

    private async Task ReopenAsync()
    {
        if (_review is not { } review || string.IsNullOrWhiteSpace(_reason))
        {
            _error = "Reopening a finalised review needs the reason an auditor will read.";
            return;
        }

        var importId = review.Statement.Import.Id;
        var body = new ReopenStatementRequest(review.Statement.Import.ExpectedVersion, _reason.Trim());
        var intent = _reopen;
        var key = intent.Key ??= BankingRules.NewKey("bank-reopen", importId);
        intent.Body = body;

        await SendAsync(intent, () => BankingApi.ReopenStatementAsync(importId, body, key), "the reopening").ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one intention under its held key, then reads the statement back. An unknown answer keeps both key and
    /// body so the button asks about the same document again; only a refusal that proves nothing was written releases
    /// the intention (specification F04).
    /// </summary>
    private async Task SendAsync<TBody>(HeldIntention<TBody> intent, Func<Task> send, string what)
    {
        _busy = true;
        _error = null;
        _notice = null;
        try
        {
            await send().ConfigureAwait(false);
            _notice = $"{what[..1].ToUpperInvariant()}{what[1..]} answered.";
            intent.Release();
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _error = BankingRules.RefusalSentence(exception, what);
            if (intent.Key is not null && BankingRules.CommittedNothing(exception.Code))
            {
                intent.Release();
            }
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task ReloadAsync()
    {
        if (_statement is not null && _account is not null)
        {
            _review = await BankingApi.GetReviewAsync(_statement.Id).ConfigureAwait(false);
            _statement = _review.Statement.Import;
            _reason = null;
            _reasons.Clear();
            _chosen.Clear();
            foreach (var intent in _lineIntents.Values.Where(intent => !intent.Outstanding))
            {
                intent.Release();
            }
        }
    }

    private HeldIntention<object> IntentFor(Guid lineId, string act)
    {
        if (!_lineIntents.TryGetValue(lineId, out var intent))
        {
            intent = new HeldIntention<object>($"{act} line");
            _lineIntents[lineId] = intent;
        }

        return intent;
    }

    private Task<IReadOnlyList<ReferenceItem>> SearchBankAccountsAsync(string term, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReferenceItem> matches = _accounts
            .Where(account => term.Length == 0 || account.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(account => ReferenceItem.From(account.Id, account.Name, $"{account.LedgerAccountCode} · {account.Currency}"))
            .ToList();
        return Task.FromResult(matches);
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchLedgerAccountsAsync(string term, CancellationToken cancellationToken)
    {
        var accounts = await Ledger.ListAccountsAsync(term, includeInactive: false, cancellationToken).ConfigureAwait(false);
        return accounts
            .Where(account => account.IsActive && (account.AccountType == AccountKind.Expense || account.AccountType == AccountKind.Revenue))
            .Select(account => ReferenceItem.From(account.Id, account.Name, account.Code))
            .ToList();
    }

    private void ChooseMovementAccount(BankStatementLineDto line, ReferenceItem? chosen)
        => _movement[line.Id] = chosen;

    /// <summary>
    /// Imports the bank's own CSV for the account on view. The file, the day the bank closed and the two balances the
    /// bank printed are all the reviewer supplies: the server parses the rows, checks that the statement adds up,
    /// refuses a file this account already holds, and answers with the review the screen then shows (task 31, recipe
    /// step 1 - bounded size, bounded rows, explicit preview, no provider and no remote download).
    /// </summary>
    private async Task ImportAsync()
    {
        if (_account is not { } accountChoice)
        {
            _error = "Choose the bank account the statement belongs to before importing it.";
            return;
        }

        if (_csvBytes is not { Length: > 0 } bytes || _csvName is not { Length: > 0 } name)
        {
            _error = "Pick the CSV file the bank gave you.";
            return;
        }

        if (_statementDate is not { } date)
        {
            _error = "The statement date is the day the bank closed, so the review can be read as at that day.";
            return;
        }

        if (!decimal.TryParse(_openingBalance, NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var opening)
            || !decimal.TryParse(_closingBalance, NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var closing))
        {
            _error = "Opening and closing balances are the figures the bank printed, read as decimal numbers.";
            return;
        }

        var statementDate = DateOnly.FromDateTime(date);
        var body = new ImportBundle(bytes, name, statementDate, opening, closing);
        var key = _import.Key ??= BankingRules.NewKey($"bank-import-{statementDate:yyyyMMdd}", accountChoice.Id);
        _import.Body = body;

        await SendAsync(_import, async () =>
        {
            var imported = await BankingApi.ImportStatementAsync(
                accountChoice.Id, body.FileName, body.StatementDate, body.Opening, body.Closing, body.Bytes, key).ConfigureAwait(false);
            _statement = imported.Import;
            _statements = await BankingApi.ListStatementsAsync(accountChoice.Id).ConfigureAwait(false);
        }, "the statement import").ConfigureAwait(false);

        if (!_import.Outstanding)
        {
            _csvBytes = null;
            _csvName = null;
            _openingBalance = null;
            _closingBalance = null;
            await ReloadAsync().ConfigureAwait(false);
        }
    }

    private async Task ChooseFileAsync(InputFileChangeEventArgs chosen)
    {
        // The ceiling is the server's own: a larger file is refused here rather than truncated on the way up.
        await using var stream = chosen.File.OpenReadStream(MaxStatementBytes);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        _csvBytes = buffer.ToArray();
        _csvName = chosen.File.Name;
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    private sealed record ImportBundle(byte[] Bytes, string FileName, DateOnly StatementDate, decimal Opening, decimal Closing);
}
