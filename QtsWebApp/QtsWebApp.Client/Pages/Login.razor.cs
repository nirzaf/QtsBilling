using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;

namespace QtsWebApp.Client.Pages;

public partial class Login
{
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _token = string.Empty;
    private string _ticketId = string.Empty;
    private string _ticketSecret = string.Empty;
    private string _newPassword = string.Empty;
    private string? _error;
    private bool _needsSetup;
    private bool _busy;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        var session = await Accounts.GetSessionAsync();

        if (session is not null && !session.PasswordResetRequired)
        {
            Navigation.NavigateTo("customers", replace: true);
        }
    }

    private async Task SignInAsync()
    {
        _busy = true;
        _error = null;

        try
        {
            await Accounts.LoginAsync(new LoginRequest(_email, _password));
            Navigation.NavigateTo("customers");
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
            _needsSetup = string.Equals(exception.Code, ApiErrorCodes.PasswordResetRequired, StringComparison.Ordinal);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RequestTicketAsync()
    {
        _busy = true;
        _error = null;

        try
        {
            var ticket = await Accounts.RequestPasswordResetAsync(_email);
            _needsSetup = true;

            // The acknowledgement is deliberately identical whether or not the account exists. This instance
            // sends no email or SMS: an owner hands the one-time ticket over locally.
            Snackbar.Add(ticket is null
                ? "Your request was recorded. Ask an owner of this business for a one-time setup ticket."
                : "Development only: the reset token is filled in below.", Severity.Info);

            if (ticket is not null)
            {
                _token = ticket.Token;
            }
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CompleteSetupAsync()
    {
        _busy = true;
        _error = null;

        try
        {
            // A ticket decides whose password is set, so no target account is typed in here.
            if (Guid.TryParse(_ticketId, out var ticketId) && !string.IsNullOrWhiteSpace(_ticketSecret))
            {
                await Accounts.CompleteSetupTicketAsync(new CompleteSetupTicketRequest(ticketId, _ticketSecret.Trim(), _newPassword));
                _ticketSecret = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(_token))
            {
                await Accounts.CompletePasswordResetAsync(new ResetPasswordRequest(_email, _token, _newPassword));
            }
            else
            {
                _error = "Paste the ticket id and secret from your administrator, or a reset token.";
                return;
            }

            Snackbar.Add("Password established. Sign in with the new password.", Severity.Success);
            _password = _newPassword;
            _needsSetup = false;
        }
        catch (ApiException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
