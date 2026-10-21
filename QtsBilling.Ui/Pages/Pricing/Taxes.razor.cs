using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Pricing;

/// <summary>
/// Tax-rate maintenance (specification task 16, recipe step 2). The form shows a percentage because that is how a
/// person reasons about tax, and sends the exact decimal fraction the server stores - 15 % leaves as 0.15 and reads
/// back as 15 %. The stored value is printed beside it so nobody has to trust the conversion.
/// </summary>
public partial class Taxes
{
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private IReadOnlyList<TaxRateDto> _rates = [];
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private bool _canManage;
    private bool _busy;
    private bool _started;
    private bool _includeInactive;

    private bool _creating;
    private TaxRateDto? _editing;
    private string _name = string.Empty;
    private decimal _percent;
    private bool _isDefault;

    private string FractionHint => $"Sent to the server as {PricingRules.StoredText(PricingRules.PercentToRate(_percent))}. At most six decimal places.";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private static string PercentOf(decimal rate) => PricingRules.PercentText(rate);

    private async Task LoadAsync()
    {
        _busy = true;
        _state = ShellStateKind.Loading;
        _message = null;
        StateHasChanged();

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.TaxesManage, StringComparer.Ordinal);

            if (!_canManage)
            {
                // The route is refused by the server too; this is the direct request, so it says so plainly.
                _state = ShellStateKind.Forbidden;
                _message = $"Maintaining tax rates needs the '{Permissions.TaxesManage}' authority.";
                return;
            }

            _rates = await Pricing.ListTaxRatesAsync(_includeInactive ? true : null).ConfigureAwait(false);
            _state = _rates.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            _message = _rates.Count == 0 ? "No tax rate is defined for this business yet." : null;
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

    private void BeginCreate()
    {
        _problem = null;
        _creating = true;
        _editing = null;
        _name = string.Empty;
        _percent = 0m;
        _isDefault = false;
    }

    private void BeginEdit(TaxRateDto rate)
    {
        _problem = null;
        _creating = false;
        _editing = rate;
        _name = rate.Name;
        _percent = PricingRules.RateToPercent(rate.Rate);
        _isDefault = rate.IsDefault;
    }

    private void Cancel()
    {
        _creating = false;
        _editing = null;
    }

    private async Task SaveAsync()
    {
        if (_busy || _name.Trim().Length == 0)
        {
            return;
        }

        _busy = true;
        _problem = null;

        // The percentage is a display unit only: what travels is the fraction, and the server refuses anything
        // outside [0, 1] or finer than six decimals rather than storing a rounded guess.
        var request = new SaveTaxRateRequest(_name, PricingRules.PercentToRate(_percent), _isDefault, _editing?.IsActive ?? true);

        try
        {
            if (_creating)
            {
                await Pricing.CreateTaxRateAsync(request).ConfigureAwait(false);
                Snackbar.Add("Tax rate created.", Severity.Success);
            }
            else if (_editing is { } rate)
            {
                await Pricing.UpdateTaxRateAsync(rate.Id, request).ConfigureAwait(false);
                Snackbar.Add("Tax rate saved. Posted documents keep the rate they were priced with.", Severity.Info);
            }

            Cancel();
            await LoadAsync().ConfigureAwait(false);
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

    private async Task DeactivateAsync(TaxRateDto rate)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Take this rate out of service",
            "Invoices and sale lines already charged with it keep their own snapshot of the rate and the mode, so "
                + "nothing is re-priced. The rate stops being selectable for new work and is no longer the default.",
            yesText: "Take out of service",
            cancelText: "Cancel");

        if (confirmed != true || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        try
        {
            await Pricing.DeactivateTaxRateAsync(rate.Id).ConfigureAwait(false);
            Snackbar.Add("Rate taken out of service. Posted documents were not changed.", Severity.Success);
            await LoadAsync().ConfigureAwait(false);
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
