using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Ui.Layout;

namespace QtsBilling.Ui.Pages.Pricing;

/// <summary>
/// Discount definitions (specification task 16, recipe step 3). Only what the contract carries is offered - one kind,
/// one bounded value, one scope, one window - and the window is entered as the counter's dates and stored as UTC
/// instants. No stacking, no tiers, no promotion engine: those are declared exclusions, not gaps to paper over.
/// </summary>
public partial class Discounts
{
    [Inject] private IPricingApiClient Pricing { get; set; } = default!;
    [Inject] private IBusinessSetupApiClient Setup { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private static readonly string[] Scopes = ["All", "Product", "Category", "Customer"];

    /// <summary>The picker's "do not filter" choice. A distinct string, because a scope name is a real value.</summary>
    public const string AnyScope = "(every scope)";

    private IReadOnlyList<DiscountDto> _discounts = [];
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _problem;
    private bool _canManage;
    private bool _busy;
    private bool _started;
    private bool _includeInactive;
    private string _scopeFilter = AnyScope;

    private bool _creating;
    private DiscountDto? _editing;
    private string _name = string.Empty;
    private string _kind = "Percentage";
    private decimal _value;
    private string _appliesTo = "All";
    private DateTime? _startsOn;
    private DateTime? _endsOn;
    private bool _isActive = true;

    /// <summary>The zone the window is shown in, and whether it is the business's own or the device's.</summary>
    private string BusinessTimeZone => _zone.Id;

    private TimeZoneInfo BusinessZone => _zone;

    private DateTimeOffset NowUtc => DateTimeOffset.UtcNow;

    private string ValueHint => _kind == "Percentage"
        ? "Percent of the line, 0 to 100. Four decimal places at most."
        : "An amount off, never negative. Four decimal places at most.";

    private string? WindowProblem
    {
        get
        {
            var start = DayOf(_startsOn);
            var end = DayOf(_endsOn);

            return start is { } starts && end is { } ends && ends < starts
                ? "The window must end on or after the day it starts."
                : null;
        }
    }

    private bool CanSave
        => _name.Trim().Length > 0
            && WindowProblem is null
            && (_kind != "Percentage" || PricingRules.PercentIsInRange(_value));

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
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.DiscountsManage, StringComparer.Ordinal);

            if (!_canManage)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Maintaining discount policy needs the '{Permissions.DiscountsManage}' authority.";
                return;
            }

            _zone = await ResolveZoneAsync().ConfigureAwait(false);
            _discounts = await Pricing.ListDiscountsAsync(
                _includeInactive ? true : null,
                _scopeFilter == AnyScope ? null : _scopeFilter).ConfigureAwait(false);
            _state = _discounts.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            _message = _discounts.Count == 0 ? "No discount definition exists for this business yet." : null;
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

    /// <summary>
    /// The window is dated in the business's zone when that can be read, and in the device's otherwise - and the
    /// screen names which one it used rather than letting a midnight look like a different midnight.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveZoneAsync()
    {
        try
        {
            var settings = await Setup.GetBusinessSettingsAsync().ConfigureAwait(false);

            return FindZone(settings.TimeZone);
        }
        catch (ApiException)
        {
            // Reading the business identity needs its own authority; a discount maintainer may not hold it.
            return LocalZone();
        }
    }

    private static TimeZoneInfo FindZone(string name)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return LocalZone();
        }
    }

    private static TimeZoneInfo LocalZone()
    {
        try
        {
            return TimeZoneInfo.Local;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    // ------------------------------------------------------------------ form

    private void BeginCreate()
    {
        _problem = null;
        _creating = true;
        _editing = null;
        _name = string.Empty;
        _kind = "Percentage";
        _value = 0m;
        _appliesTo = "All";
        _startsOn = null;
        _endsOn = null;
        _isActive = true;
    }

    private void BeginEdit(DiscountDto discount)
    {
        _problem = null;
        _creating = false;
        _editing = discount;
        _name = discount.Name;
        _kind = discount.Kind.ToString();
        _value = discount.Value;
        _appliesTo = discount.AppliesTo.ToString();
        _startsOn = discount.StartsAtUtc is { } start ? TimeZoneInfo.ConvertTime(start, _zone).DateTime : null;
        _endsOn = discount.EndsAtUtc is { } end ? TimeZoneInfo.ConvertTime(end.AddMilliseconds(-1), _zone).DateTime : null;
        _isActive = discount.IsActive;
    }

    private void Cancel()
    {
        _creating = false;
        _editing = null;
    }

    private async Task SaveAsync()
    {
        if (!CanSave || _busy)
        {
            return;
        }

        _busy = true;
        _problem = null;

        var request = new SaveDiscountRequest(
            _name,
            _kind,
            _value,
            _appliesTo,
            DayOf(_startsOn) is { } starts ? PricingRules.DayStartUtc(starts, _zone) : null,
            DayOf(_endsOn) is { } ends ? PricingRules.DayAfterUtc(ends, _zone) : null,
            _isActive);

        try
        {
            if (_creating)
            {
                await Pricing.CreateDiscountAsync(request).ConfigureAwait(false);
                Snackbar.Add("Discount definition created.", Severity.Success);
            }
            else if (_editing is { } discount)
            {
                await Pricing.UpdateDiscountAsync(discount.Id, request).ConfigureAwait(false);
                Snackbar.Add("Discount definition saved. Documents already discounted keep what was applied.", Severity.Info);
            }

            Cancel();
            await LoadAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            // Out-of-range values and an inverted window come back as field errors, and the form keeps what was
            // typed so the operator fixes the named field instead of starting again (T16.02).
            _problem = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task DeactivateAsync(DiscountDto discount)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Take this definition out of service",
            "Sales and orders that already used it keep the discount they were given. Taking it out of service "
                + "only stops it being applied to anything new.",
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
            await Pricing.DeactivateDiscountAsync(discount.Id).ConfigureAwait(false);
            Snackbar.Add("Definition taken out of service.", Severity.Success);
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

    /// <summary>
    /// The picker hands over a date-and-time in the browser's own reading; the window is a calendar day in the
    /// business's zone, so only the day part is taken and the day's own midnight/next-midnight is what is stored.
    /// </summary>
    private static DateOnly? DayOf(DateTime? picked)
        => picked is { } value ? new DateOnly(value.Year, value.Month, value.Day) : null;
}
