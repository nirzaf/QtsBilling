using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
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

            if (ticket is not null)
            {
                _token = ticket.Token;
                Snackbar.Add("Development ticket filled in automatically; production delivers it out of band.", Severity.Info);
            }
            else
            {
                Snackbar.Add("If the account exists, a setup ticket has been issued. Contact your administrator.", Severity.Info);
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
            await Accounts.CompletePasswordResetAsync(new ResetPasswordRequest(_email, _token, _newPassword));
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
