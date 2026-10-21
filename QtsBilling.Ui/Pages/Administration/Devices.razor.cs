using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sync;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// The terminal lifecycle an owner operates (specification task 14, recipe step 3): open an enrolment code, see
/// what each till holds, revoke, move a terminal only against a recorded custody resolution, and retire one only
/// once nothing is outstanding. There is no wipe control, because deleting received money is never what an
/// administrator wants (D07).
/// </summary>
public partial class Devices
{
    /// <summary>The lifecycle command the panel below the table is about.</summary>
    public enum DeviceCommand
    {
        Revoke,
        Reassign,
        Retire,
    }

    [Inject] private IDeviceAdministrationApiClient DevicesApi { get; set; } = default!;
    [Inject] private IBusinessSetupApiClient Setup { get; set; } = default!;
    [Inject] private IAccountApiClient Session { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private IReadOnlyList<DeviceInfoDto> _devices = [];
    private IReadOnlyList<BusinessLocationDto> _branches = [];
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private bool _canManage;
    private bool _busy;
    private bool _started;

    private bool _enrolling;
    private CreateEnrolmentChallengeResponse? _challenge;
    private Guid? _challengeBranch;
    private string _challengeBranchName = string.Empty;

    private DeviceInfoDto? _selected;
    private DeviceCommand _mode = DeviceCommand.Revoke;
    private string _reason = string.Empty;
    private decimal _acknowledgedCash;
    private int _acknowledgedOperations;
    private string _clearanceReason = string.Empty;
    private DeviceReassignmentClearanceDto? _clearance;
    private Guid? _moveTarget;

    private string CommandTitle => _mode switch
    {
        DeviceCommand.Reassign => "Move this terminal to another branch",
        DeviceCommand.Retire => "Retire this terminal",
        _ => "Revoke this terminal",
    };

    private string CommandConfirmLabel => _mode switch
    {
        DeviceCommand.Reassign => "Move it",
        DeviceCommand.Retire => "Confirm: retire",
        _ => "Confirm: revoke",
    };

    /// <summary>The server's own precondition, stated before the operator presses anything.</summary>
    private string? CommandBlock => _selected switch
    {
        null => null,
        { } device when _mode == DeviceCommand.Retire => AdministrationRules.RetirementBlock(device),
        { } device when _mode == DeviceCommand.Reassign => _clearance is not null
            ? null
            : AdministrationRules.ReassignmentBlock(device),
        { } device when AdministrationRules.IsClosed(device) => "This terminal is already closed, so there is nothing left to do here.",
        _ => null,
    };

    private bool CanRunCommand
    {
        get
        {
            if (_busy || _selected is not { } device || CommandBlock is not null || _reason.Trim().Length == 0)
            {
                return false;
            }

            return _mode switch
            {
                DeviceCommand.Reassign => _moveTarget is not null,
                DeviceCommand.Retire => AdministrationRules.RetirementBlock(device) is null,
                _ => true,
            };
        }
    }

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
        _message = null;
        StateHasChanged();

        try
        {
            var session = await Session.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = AdministrationRules.Can(session.Permissions, Permissions.DevicesManage);

            if (!_canManage)
            {
                _state = ShellStateKind.Forbidden;
                _message = AdministrationRules.MissingReason("Seeing or changing terminals", Permissions.DevicesManage);
                return;
            }

            _branches = await Setup.ListLocationsAsync().ConfigureAwait(false);
            _devices = await DevicesApi.ListDevicesAsync().ConfigureAwait(false);
            _state = _devices.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            _message = _devices.Count == 0 ? "No terminal is registered with this business yet." : null;
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = exception.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ enrolment codes

    private void BeginEnrolment()
    {
        _problem = null;
        _enrolling = true;
        _challenge = null;
        _challengeBranch = null;
        _challengeBranchName = string.Empty;
    }

    private void CloseEnrolment()
    {
        _enrolling = false;
        _challenge = null;
        _challengeBranch = null;
    }

    private Task<IReadOnlyList<ReferenceItem>> SearchBranchesAsync(string term, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var needle = term.Trim();

        return Task.FromResult<IReadOnlyList<ReferenceItem>>(_branches
            .Where(branch => branch.IsActive
                && (branch.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (branch.Code ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Select(branch => ReferenceItem.From(branch.Id, branch.Name, branch.Code))
            .ToArray());
    }

    private Task ChosenBranchAsync(ReferenceItem? branch)
    {
        _challengeBranch = branch?.Id;
        _challengeBranchName = branch?.Name ?? string.Empty;
        return Task.CompletedTask;
    }

    private async Task IssueChallengeAsync()
    {
        if (_challengeBranch is not { } branchId || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            _challenge = await DevicesApi.CreateEnrolmentChallengeAsync(branchId).ConfigureAwait(false);
            _challengeBranchName = _branches.FirstOrDefault(branch => branch.Id == branchId)?.Name ?? "this branch";
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;

            // A closed or foreign branch is the usual cause; re-read so the picker shows the truth.
            await LoadAsync().ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ lifecycle commands

    private void BeginRevoke(DeviceInfoDto device) => OpenCommand(device, DeviceCommand.Revoke);

    private void BeginReassign(DeviceInfoDto device) => OpenCommand(device, DeviceCommand.Reassign);

    private void BeginRetire(DeviceInfoDto device) => OpenCommand(device, DeviceCommand.Retire);

    private void OpenCommand(DeviceInfoDto device, DeviceCommand mode)
    {
        _problem = null;
        _enrolling = false;
        _selected = device;
        _mode = mode;
        _reason = string.Empty;
        _clearance = null;
        _clearanceReason = string.Empty;
        _moveTarget = null;
        _acknowledgedCash = device.DeviceHeldProvisionalCash;
        _acknowledgedOperations = AdministrationRules.MinimumAcknowledgedOperations(device);
    }

    private void CancelCommand()
    {
        _selected = null;
        _clearance = null;
        _reason = string.Empty;
    }

    private Task MoveTargetAsync(ReferenceItem? branch)
    {
        _moveTarget = branch?.Id;
        return Task.CompletedTask;
    }

    private async Task IssueClearanceAsync()
    {
        if (_selected is not { } device || _busy || string.IsNullOrWhiteSpace(_clearanceReason))
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            _clearance = await DevicesApi.IssueClearanceAsync(device.DeviceId, new IssueReassignmentClearanceRequest(
                device.Version,
                _acknowledgedCash,
                _acknowledgedOperations,
                _clearanceReason)).ConfigureAwait(false);

            Snackbar.Add("Custody resolution recorded. The move is now allowed, once.", Severity.Info);
            await LoadAsync().ConfigureAwait(false);
            _selected = _devices.FirstOrDefault(row => row.DeviceId == device.DeviceId);
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;

            // The count on the terminal moved while this form was open: re-read rather than resend a stale figure.
            await LoadAsync().ConfigureAwait(false);
            _selected = _devices.FirstOrDefault(row => row.DeviceId == device.DeviceId);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task RunCommandAsync()
    {
        if (!CanRunCommand || _selected is not { } device)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            switch (_mode)
            {
                case DeviceCommand.Revoke:
                    await DevicesApi.RevokeAsync(device.DeviceId, new RevokeDeviceRequest(device.Version, _reason))
                        .ConfigureAwait(false);
                    Snackbar.Add("Revoked. The till stops working here, and everything it already sent stays readable for reconciliation.", Severity.Info);
                    break;

                case DeviceCommand.Retire:
                    await DevicesApi.RetireAsync(device.DeviceId, new RetireDeviceRequest(device.Version, _reason))
                        .ConfigureAwait(false);
                    Snackbar.Add("Retired. Nothing it received was deleted.", Severity.Info);
                    break;

                case DeviceCommand.Reassign:
                    if (_moveTarget is not { } target || _clearance is not { } paper)
                    {
                        _problem = "A move needs a target branch and the clearance that covers this terminal's money.";
                        return;
                    }

                    await DevicesApi.ReassignAsync(
                        device.DeviceId, new ReassignDeviceRequest(device.Version, target, paper.ClearanceId, _reason))
                        .ConfigureAwait(false);
                    Snackbar.Add("Moved. Its earlier sales keep the branch they were taken at.", Severity.Success);
                    break;
            }

            CancelCommand();
            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _problem = exception.Message;

            if (exception.IsConflict)
            {
                // Either the record moved under this form, or the custody resolution is not current. Re-read the
                // truth instead of offering the same write twice.
                CancelCommand();
                await LoadAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static Color StateColor(DeviceInfoDto device) => device.Status switch
    {
        "Active" => Color.Success,
        "Revoked" => Color.Warning,
        _ => Color.Default,
    };
}
