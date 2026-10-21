using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// Accounts and one-time handover codes (specification task 14, recipe step 2). The operator never types an
/// identifier, never sees a discount default of 100 %, never has a code shown twice, and never gets a warning-free
/// way to remove the last owner of the business.
/// </summary>
/// <remarks>
/// The list is read through <see cref="PagedListState{T}"/> so paging, search and the cancellation of a superseded
/// load behave exactly like every other screen. Mutations go to the same routes the tests exercise, and a lost
/// race with another editor reloads the row instead of overwriting it.
/// </remarks>
public partial class Users
{
    [Inject] private IAdministrationUserApiClient UsersApi { get; set; } = default!;
    [Inject] private IBusinessSetupApiClient Setup { get; set; } = default!;
    [Inject] private IAccountApiClient Session { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private static readonly string[] Roles = ["Owner", "Manager", "Accountant", "Cashier", "Viewer"];

    private readonly PagedListState<AdministrationUserDto> _list;
    private IReadOnlyList<BusinessLocationDto> _branches = [];
    private IReadOnlyDictionary<Guid, string> _branchNames = new Dictionary<Guid, string>();

    public Users()
    {
        _list = new PagedListState<AdministrationUserDto>(LoadPageAsync, StateHasChanged);
    }

    private bool _canManage;
    private bool _busy;
    private bool _started;
    private string _search = string.Empty;
    private string? _problem;

    // The handover secret lives only in these two fields, for as long as the panel is open.
    private SetupTicketDto? _ticket;
    private string _ticketAccountName = "this person";

    private AdministrationUserDto? _editing;
    private bool _isNew;
    private string _email = string.Empty;
    private string _displayName = string.Empty;
    private string _role = "Cashier";
    private bool _allLocations;
    private decimal _maxDiscount;
    private string _reason = string.Empty;
    private readonly List<ReferenceItem> _chosenBranches = [];

    private AdministrationUserDto? _ticketTarget;
    private string _reauthentication = string.Empty;
    private string _ticketPurpose = "Setup";
    private string _ticketReason = string.Empty;

    private AdministrationUserDto? _stateTarget;
    private bool _activating;
    private string _stateReason = string.Empty;

    private string DialogTitle => _isNew ? "Add a person to this business" : $"What {_editing?.DisplayName} may do";

    private string DiscountHint => string.Equals(_role, "Cashier", StringComparison.OrdinalIgnoreCase)
        ? "A cashier starts with no allowance at all. The stored column default is deliberately not offered here."
        : "The largest fraction of a sale this person may discount. 1 means the whole price.";

    private string? _lastOwnerWarning
        => _editing is { } target
            ? AdministrationRules.LastOwnerBlock(_list.Rows, target, _isNew ? null : _role)
            : null;

    private bool CanSave
        => !_busy
            && _displayName.Trim().Length > 0
            && _reason.Trim().Length > 0
            && (_allLocations || _chosenBranches.Count > 0)
            && (_isNew ? _email.Contains('@', StringComparison.Ordinal) : true)
            && _lastOwnerWarning is null;

    // Nothing is fetched during prerender: the session - and therefore whether this screen may show any account
    // at all - only exists on the first interactive render.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;

        var session = await Session.GetSessionAsync().ConfigureAwait(false);
        _canManage = session is not null && AdministrationRules.Can(session.Permissions, Permissions.UsersManage);

        if (session is null || !_canManage)
        {
            // An ended session or a role without users.manage: the list is never even asked for, so no account
            // detail reaches a screen that may not show it (specification T14.02).
            await _list.ReloadAsync().ConfigureAwait(false);
            StateHasChanged();
            return;
        }

        await LoadBranchesAsync().ConfigureAwait(false);
        await _list.ReloadAsync().ConfigureAwait(false);
        StateHasChanged();
    }

    private async Task<PagedRows<AdministrationUserDto>> LoadPageAsync(
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken token)
    {
        if (!_canManage)
        {
            throw new ApiException(
                403,
                ApiErrorCodes.Forbidden,
                AdministrationRules.MissingReason("Seeing who belongs to this business", Permissions.UsersManage));
        }

        var page = await UsersApi.ListUsersAsync(search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<AdministrationUserDto>(page.Items, page.NextCursor);
    }

    private async Task LoadBranchesAsync()
    {
        _branches = await Setup.ListLocationsAsync().ConfigureAwait(false);
        _branchNames = _branches.ToDictionary(branch => branch.Id, branch => branch.Name);
    }

    private string BranchText(AdministrationUserDto user)
    {
        if (user.AllLocations)
        {
            return "Every branch, now and later";
        }

        if (user.LocationIds.Count == 0)
        {
            return "No branch - cannot post at a counter";
        }

        var names = user.LocationIds.Select(id => _branchNames.TryGetValue(id, out var name) ? name : "a branch no longer listed");

        return string.Join(", ", names);
    }

    // ------------------------------------------------------------------ search and paging

    private Task SearchChanged(string term)
    {
        _search = term;
        return _list.SearchAsync(term);
    }

    private Task ReloadAsync() => _list.ReloadAsync();

    private Task NextAsync() => _list.NextPageAsync();

    private Task PreviousAsync() => _list.PreviousPageAsync();

    private async Task RefreshAfterChangeAsync()
    {
        await LoadBranchesAsync().ConfigureAwait(false);
        await _list.ReloadAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ create and edit

    private void BeginCreate()
    {
        _problem = null;
        _isNew = true;
        _editing = null;
        _email = string.Empty;
        _displayName = string.Empty;
        _role = "Cashier";
        _allLocations = false;
        _maxDiscount = AdministrationRules.DefaultDiscountRate(_role);
        _reason = string.Empty;
        _chosenBranches.Clear();
    }

    private void BeginEdit(AdministrationUserDto user)
    {
        _problem = null;
        _isNew = false;
        _editing = user;
        _email = user.Email;
        _displayName = user.DisplayName;
        _role = user.Role;
        _allLocations = user.AllLocations;
        _maxDiscount = user.MaxDiscountRate;
        _reason = string.Empty;

        _chosenBranches.Clear();
        _chosenBranches.AddRange(user.LocationIds
            .Select(id => _branchNames.TryGetValue(id, out var name)
                ? ReferenceItem.From(id, name, null)
                : ReferenceItem.From(id, "a branch no longer listed", null)));
    }

    private void CancelEdit()
    {
        _editing = null;
        _isNew = false;
        _reauthentication = string.Empty;
    }

    private Task<IReadOnlyList<ReferenceItem>> SearchBranchesAsync(string term, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var needle = term.Trim();

        return Task.FromResult<IReadOnlyList<ReferenceItem>>(_branches
            .Where(branch => branch.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (branch.Code ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(branch => ReferenceItem.From(branch.Id, branch.Name, branch.Code))
            .ToArray());
    }

    private Task AddBranchAsync(ReferenceItem? branch)
    {
        if (branch is not null && _chosenBranches.All(chosen => chosen.Id != branch.Id))
        {
            _chosenBranches.Add(branch);
        }

        return Task.CompletedTask;
    }

    private Task RemoveBranchAsync(ReferenceItem branch)
    {
        _chosenBranches.RemoveAll(chosen => chosen.Id == branch.Id);
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            var branchIds = _allLocations ? null : _chosenBranches.Select(branch => branch.Id).ToArray();

            if (_isNew)
            {
                var created = await UsersApi.CreateUserAsync(new CreateAdministrationUserRequest(
                    _email, _displayName, _role, _allLocations, branchIds, _maxDiscount, _reason)).ConfigureAwait(false);

                _ticket = created.Ticket;
                _ticketAccountName = created.User.DisplayName;
                Snackbar.Add("Account created. Hand the code over now - it will not be shown again.", Severity.Warning);
            }
            else if (_editing is { } target)
            {
                await UsersApi.UpdateMembershipAsync(target.UserId, new UpdateMembershipRequest(
                    target.MembershipVersion, _role, _allLocations, branchIds, _maxDiscount, _reason)).ConfigureAwait(false);
                Snackbar.Add("Authority saved. Their sessions end now, so they sign in again with the new scope.", Severity.Success);
            }

            CancelEdit();
            await RefreshAfterChangeAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);

            if (exception.IsConflict)
            {
                CancelEdit();
                await RefreshAfterChangeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ handover codes

    private void BeginTicket(AdministrationUserDto user)
    {
        _problem = null;
        _ticketTarget = user;
        _reauthentication = string.Empty;
        _ticketPurpose = user.PasswordResetRequired ? "Setup" : "Recovery";
        _ticketReason = string.Empty;
    }

    private void CancelTicket()
    {
        _ticketTarget = null;
        _reauthentication = string.Empty;
        _ticketReason = string.Empty;
    }

    private async Task IssueTicketAsync()
    {
        if (_ticketTarget is not { } user || _reauthentication.Length == 0 || !CanIssueTicket)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            _ticket = await UsersApi.IssueSetupTicketAsync(
                user.UserId, new IssueSetupTicketRequest(_reauthentication, _ticketPurpose, _ticketReason)).ConfigureAwait(false);
            _ticketAccountName = user.DisplayName;

            // The proof of who the issuer is disappears the moment the ticket exists. It was only ever a check.
            _reauthentication = string.Empty;
            CancelTicket();
            await RefreshAfterChangeAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);
            _reauthentication = string.Empty;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private bool CanIssueTicket => !_busy && !string.IsNullOrWhiteSpace(_ticketReason);

    /// <summary>The operator confirms the code changed hands; the screen forgets it here.</summary>
    private void DismissTicket()
    {
        _ticket = null;
        _ticketAccountName = "this person";
    }

    // ------------------------------------------------------------------ in and out of service

    private void BeginDeactivate(AdministrationUserDto user)
    {
        _problem = AdministrationRules.LastOwnerBlock(_list.Rows, user);
        _stateTarget = user;
        _activating = false;
        _stateReason = string.Empty;
    }

    private void BeginActivate(AdministrationUserDto user)
    {
        _problem = null;
        _stateTarget = user;
        _activating = true;
        _stateReason = string.Empty;
    }

    private void CancelState()
    {
        _stateTarget = null;
        _stateReason = string.Empty;
    }

    private async Task ApplyStateAsync()
    {
        if (_stateTarget is not { } user || _stateReason.Trim().Length == 0 || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            if (_activating)
            {
                await UsersApi.ActivateUserAsync(user.UserId, new ActivateUserRequest(user.MembershipVersion, _stateReason))
                    .ConfigureAwait(false);
                Snackbar.Add($"{user.DisplayName} is back in service.", Severity.Success);
            }
            else
            {
                await UsersApi.DisableUserAsync(user.UserId, new DeactivateUserRequest(user.MembershipVersion, _stateReason))
                    .ConfigureAwait(false);
                Snackbar.Add($"{user.DisplayName} is out of service. Their posted sales keep their name.", Severity.Info);
            }

            CancelState();
            await RefreshAfterChangeAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = Describe(exception);

            if (exception.IsConflict)
            {
                CancelState();
                await RefreshAfterChangeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
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

    public async ValueTask DisposeAsync()
    {
        // A handover secret never outlives the screen that showed it.
        _ticket = null;
        _reauthentication = string.Empty;

        await _list.DisposeAsync().ConfigureAwait(false);
    }
}
