using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Sales;

/// <summary>
/// The drawer workbench a manager or cashier runs without an API tool (specification task 19, recipe step 1):
/// the registers of the branches this account may reach, the open session with its derived arithmetic, paid-in and
/// paid-out, the session history, the custody position the close is judged against, an authorised carry-forward,
/// and the close itself. Every command carries one idempotency key for one intention, and nothing here decides
/// whether money may be closed over - the server answers that and its sentence is what the screen shows.
/// </summary>
public partial class Registers : IAsyncDisposable
{
    [Inject] private ICashRegisterApiClient RegisterApi { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private string? _committed;
    private bool _busy;
    private bool _canManage;
    private bool _started;

    private IReadOnlyList<CashRegisterDto> _registersList = [];
    private IReadOnlyDictionary<Guid, string> _locationNames = new Dictionary<Guid, string>();
    private Guid? _selectedRegisterId;
    private CashSessionDetailDto? _detail;
    private DrawerCustodyDto? _custody;
    private IReadOnlyList<CashCustodianDto> _custodians = [];

    private readonly Dictionary<(CashCustodySourceState Kind, Guid SourceId), decimal> _declared = new();

    private readonly PagedListState<CashRegisterSessionDto> _history;

    public Registers()
    {
        // Session history is paged and searched by the server, and a superseded page cannot repaint the list the
        // operator has moved past (the task 13/14/15 rule, applied to the drawer history).
        _history = new PagedListState<CashRegisterSessionDto>(LoadHistoryAsync, StateHasChanged, 10);
    }

    private async Task<PagedRows<CashRegisterSessionDto>> LoadHistoryAsync(
        string? search, string? cursor, int pageSize, CancellationToken token)
    {
        if (_selectedRegisterId is not { } registerId)
        {
            return new PagedRows<CashRegisterSessionDto>([], null);
        }

        var page = await RegisterApi.ListSessionsAsync(registerId, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<CashRegisterSessionDto>(page.Items, page.NextCursor);
    }

    private decimal _openingFloat;
    private string _movementKind = "cashIn";
    private decimal _movementAmount;
    private string _movementReason = string.Empty;
    private decimal? _countedCash;
    private string _closeNote = string.Empty;
    private string _transferReason = string.Empty;
    private Guid? _custodianId;

    // One key per standing intention (specification D06). They are re-minted when the intention is completed or
    // abandoned, never per click, so a retry after a lost answer is the same command rather than a second one.
    private string _openKey = RegisterRules.NewCommandKey("drawer-open");
    private string _movementKey = RegisterRules.NewCommandKey("drawer-movement");
    private string _closeKey = RegisterRules.NewCommandKey("drawer-close");
    private string _transferKey = RegisterRules.NewCommandKey("carry-forward");

    private CashRegisterDto? Selected => _registersList.FirstOrDefault(register => register.Id == _selectedRegisterId);

    private bool HasOpenSession => _detail is not null;

    private bool CanClose => _canManage && _busy == false && RegisterRules.CanAttemptClose(_custody) && _countedCash is >= 0;

    private decimal ExpectedCash => _detail?.ExpectedCash ?? 0m;

    private string Currency => _detail?.Session.Currency ?? string.Empty;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _state = ShellStateKind.Loading;
        _problem = null;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.RegistersManage, StringComparer.Ordinal);

            var registers = await RegisterApi.GetRegistersAsync().ConfigureAwait(false);
            var locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

            _registersList = registers;
            _locationNames = locations.ToDictionary(location => location.Id, location => location.Name);

            if (_selectedRegisterId is null || registers.All(register => register.Id != _selectedRegisterId))
            {
                _selectedRegisterId = registers.FirstOrDefault(register => register.CurrentSession is not null)?.Id
                    ?? registers.FirstOrDefault()?.Id;
            }

            await LoadSelectedAsync().ConfigureAwait(false);

            // The history route is the manager's, so it is read only for an account that may see it.
            if (_canManage && _selectedRegisterId is not null)
            {
                await _history.FirstPageAsync().ConfigureAwait(false);
            }

            _state = _registersList.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            _message = _registersList.Count == 0
                ? "This business has no cash register yet. A register belongs to one branch and holds one open drawer at a time."
                : null;
        }
        catch (ApiException exception)
        {
            _state = StateOf(exception);
            _message = exception.StatusCode == 403
                ? $"Running registers needs the '{Permissions.RegistersManage}' authority; the register list above is read with the sale authority."
                : exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static ShellStateKind StateOf(ApiException exception) => exception.StatusCode switch
    {
        401 => ShellStateKind.Unauthenticated,
        403 => ShellStateKind.Forbidden,
        404 => ShellStateKind.Empty,
        409 => ShellStateKind.Conflict,
        _ => ShellStateKind.Failure,
    };

    private async Task LoadSelectedAsync()
    {
        _detail = null;
        _custody = null;

        if (_selectedRegisterId is not { } registerId)
        {
            return;
        }

        try
        {
            // A register with no open drawer answers 404 CASH_REGISTER_NOT_OPEN, which is a different state from a
            // failure and is shown as "open a drawer" rather than as an error (specification task 13).
            _detail = await RegisterApi.GetCurrentSessionAsync(registerId).ConfigureAwait(false);

            if (_canManage)
            {
                _custody = await RegisterApi.GetCustodyAsync(registerId, _detail.Session.Id).ConfigureAwait(false);
                _countedCash ??= _detail.ExpectedCash;

                // The people a carry-forward may name are read only when something is actually unresolved, so a
                // clean drawer never asks for the member list (task 19, recipe step 1: bounded requests).
                if (!_custody.Closable && _custody.Unresolved.Count > 0)
                {
                    await LoadCustodiansAsync().ConfigureAwait(false);
                }
            }
        }
        catch (ApiException exception) when (exception.Code == ApiErrorCodes.CashRegisterNotOpen || exception.StatusCode == 404)
        {
            _detail = null;
            _custody = null;
        }
    }

    private async Task SelectRegisterAsync(Guid registerId)
    {
        _selectedRegisterId = registerId;
        _problem = null;
        _committed = null;
        _countedCash = null;
        _declared.Clear();
        _openKey = RegisterRules.NewCommandKey("drawer-open");
        _movementKey = RegisterRules.NewCommandKey("drawer-movement");
        _closeKey = RegisterRules.NewCommandKey("drawer-close");

        _busy = true;
        await LoadSelectedAsync().ConfigureAwait(false);
        await _history.FirstPageAsync().ConfigureAwait(false);
        _busy = false;
        StateHasChanged();
    }

    // ------------------------------------------------------------------ commands

    private async Task OpenDrawerAsync()
    {
        if (_busy || !_canManage || Selected is not { } register || _openingFloat < 0m)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            _detail = await RegisterApi.OpenSessionAsync(
                register.Id, new OpenCashRegisterSessionRequest(_openingFloat, null), _openKey).ConfigureAwait(false);

            _custody = await RegisterApi.GetCustodyAsync(register.Id, _detail.Session.Id).ConfigureAwait(false);
            _countedCash = _detail.ExpectedCash;
            _openingFloat = 0m;

            // The intention is complete; the next drawer is a new one.
            _openKey = RegisterRules.NewCommandKey("drawer-open");
            _committed = $"Drawer opened on {register.Name} with {RegisterRules.MoneyText(_detail.Session.OpeningFloat, _detail.Session.Currency)}.";
            Snackbar.Add(_committed, Severity.Success);
            await ReloadRegistersAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task AddMovementAsync()
    {
        if (_busy || !_canManage || _detail is null || _movementAmount <= 0m || string.IsNullOrWhiteSpace(_movementReason))
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await RegisterApi.AddMovementAsync(
                _detail.Session.CashRegisterId,
                _detail.Session.Id,
                new CashMovementRequest(_movementKind, _movementAmount, _movementReason.Trim()),
                _movementKey).ConfigureAwait(false);

            await RefreshDrawerAsync().ConfigureAwait(false);

            _movementAmount = 0m;
            _movementReason = string.Empty;
            _movementKey = RegisterRules.NewCommandKey("drawer-movement");
            Snackbar.Add("Drawer movement recorded.", Severity.Success);
        }
        catch (ApiException exception)
        {
            // The typed amount and reason stay on the screen; a refusal is read, not retyped from memory.
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task CloseDrawerAsync()
    {
        if (_busy || !_canManage || _detail is not { } drawer || _countedCash is not { } counted)
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Close this drawer",
            $"{RegisterRules.VarianceSentence(counted, drawer.ExpectedCash, drawer.Session.Currency)} "
                + "The count, the expected figure and the difference are stored with the session; the movements stay exactly as they were.",
            yesText: "Close drawer",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            _detail = await RegisterApi.CloseSessionAsync(
                drawer.Session.CashRegisterId,
                drawer.Session.Id,
                new CloseCashRegisterSessionRequest(counted, string.IsNullOrWhiteSpace(_closeNote) ? null : _closeNote.Trim()),
                _closeKey).ConfigureAwait(false);

            _custody = null;
            _countedCash = null;
            _closeNote = string.Empty;
            _closeKey = RegisterRules.NewCommandKey("drawer-close");
            _committed = "Drawer closed.";
            Snackbar.Add(_committed, Severity.Success);
            await ReloadRegistersAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);

            if (exception.Code == ApiErrorCodes.ApprovalRequired)
            {
                // The refusal is the custody rule speaking: show what is still unresolved rather than a generic error.
                await RefreshCustodyAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task CarryForwardAsync()
    {
        if (_busy || !_canManage || _custody is null || _custodianId is null || string.IsNullOrWhiteSpace(_transferReason))
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            var items = _custody.Unresolved
                .Select(item => new CustodyTransferItemRequest(
                    item.Kind,
                    item.SourceId,
                    item.Kind == CashCustodySourceState.ProvisionalPayment
                        ? item.Amount
                        : _declared.TryGetValue((item.Kind, item.SourceId), out var declared) ? declared : item.Amount))
                .ToArray();

            _custody = await RegisterApi.CreateCustodyTransferAsync(
                _custody.CashRegisterId,
                _custody.SessionId,
                new CreateCustodyTransferRequest(_custodianId.Value, _transferReason.Trim(), items),
                _transferKey).ConfigureAwait(false);

            _transferKey = RegisterRules.NewCommandKey("carry-forward");
            _transferReason = string.Empty;
            _declared.Clear();
            Snackbar.Add("Carry-forward recorded. The money is still the business's; the duty to account for it now has a name.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task RefreshDrawerAsync()
    {
        if (Selected is not { } register)
        {
            return;
        }

        await LoadSelectedAsync().ConfigureAwait(false);
    }

    private async Task RefreshCustodyAsync()
    {
        if (_detail is null || !_canManage)
        {
            return;
        }

        try
        {
            _custody = await RegisterApi.GetCustodyAsync(_detail.Session.CashRegisterId, _detail.Session.Id).ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
    }

    private async Task ReloadRegistersAsync()
    {
        try
        {
            _registersList = await RegisterApi.GetRegistersAsync().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // The drawer answer above is the one that matters; a stale register list is refreshed by Reload.
        }
    }

    private async Task LoadCustodiansAsync()
    {
        if (_custodians.Count > 0 || !_canManage)
        {
            return;
        }

        try
        {
            _custodians = await RegisterApi.GetCustodiansAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
        }
    }

    /// <summary>
    /// A provisional payment already has an amount the server holds; a terminal's custody or an unposted operation
    /// does not, so the person receiving it has to declare the figure they answer for (specification D07).
    /// </summary>
    private bool NeedsDeclaration(CashCustodySourceState kind, Guid sourceId)
        => _custody?.Unresolved.FirstOrDefault(item => item.Kind == kind && item.SourceId == sourceId) is { } item
            ? RegisterRules.NeedsDeclaredAmount(item)
            : false;

    private decimal MinimumFor(CashCustodySourceState kind, Guid sourceId)
        => _custody?.Unresolved.FirstOrDefault(item => item.Kind == kind && item.SourceId == sourceId) is { } item
            ? RegisterRules.MinimumCarried(item)
            : 0m;

    private Task NextHistoryAsync() => _history.NextPageAsync();

    private Task PreviousHistoryAsync() => _history.PreviousPageAsync();

    private static string SessionStateText(CashRegisterSessionDto session)
        => session.Status == CashRegisterSessionState.Open
            ? "Open"
            : session.Variance is { } variance && variance != 0m
                ? $"Closed, {RegisterRules.MoneyText(variance, session.Currency)} off"
                : "Closed, count matched";

    public async ValueTask DisposeAsync()
    {
        // Leaving the page must not leave a history load that can still write to disposed state.
        await _history.DisposeAsync().ConfigureAwait(false);
    }

    private static string Describe(ApiException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return exception.Message;
        }

        var lines = exception.Errors.Select(pair => $"{pair.Key}: {string.Join(" ", pair.Value)}");

        return string.Join(" ", [exception.Message, .. lines]);
    }
}
