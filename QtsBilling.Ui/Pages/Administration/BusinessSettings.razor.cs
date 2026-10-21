using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// Owner-facing business setup (specification task 14, recipe step 1): the identity and calendar, the numbering,
/// the branches, and the control accounts postings land in - each one reached through the route that is already
/// the authority for it, so the screen cannot show a different answer than the ledger will use.
/// </summary>
/// <remarks>
/// Nothing is fetched during initialisation: a server prerender has no session cookie of its own, and the first
/// interactive render is where the operator's authority becomes known (the rule task 13 established).
/// </remarks>
public partial class BusinessSettings
{
    [Inject] private IBusinessSetupApiClient Setup { get; set; } = default!;
    [Inject] private IAccountingApiClient Accounting { get; set; } = default!;
    [Inject] private IAccountApiClient Session { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>Every slot the posting rules read, with the classification the server insists on.</summary>
    public static readonly IReadOnlyList<ControlAccountSlot> Slots =
    [
        Slot("Cash", nameof(BusinessAccountingSettingsDto.CashAccountId), AccountKind.Asset,
            settings => settings.CashAccountId, (settings, id) => settings with { CashAccountId = id },
            "Where drawer money and cash receipts settle."),
        Slot("Bank clearing", nameof(BusinessAccountingSettingsDto.BankClearingAccountId), AccountKind.Asset,
            settings => settings.BankClearingAccountId, (settings, id) => settings with { BankClearingAccountId = id },
            "Money received that has not been matched to a bank statement line yet."),
        Slot("Accounts receivable", nameof(BusinessAccountingSettingsDto.AccountsReceivableAccountId), AccountKind.Asset,
            settings => settings.AccountsReceivableAccountId, (settings, id) => settings with { AccountsReceivableAccountId = id },
            "What customers owe on an issued invoice."),
        Slot("Inventory asset", nameof(BusinessAccountingSettingsDto.InventoryAssetAccountId), AccountKind.Asset,
            settings => settings.InventoryAssetAccountId, (settings, id) => settings with { InventoryAssetAccountId = id },
            "The value of stock on hand. Stock movements and this account must agree."),
        Slot("Delivered, not invoiced", nameof(BusinessAccountingSettingsDto.DeliveredNotInvoicedAccountId), AccountKind.Asset,
            settings => settings.DeliveredNotInvoicedAccountId, (settings, id) => settings with { DeliveredNotInvoicedAccountId = id },
            "Goods that left the counter before an invoice recognised them."),
        Slot("Input tax receivable", nameof(BusinessAccountingSettingsDto.InputTaxReceivableAccountId), AccountKind.Asset,
            settings => settings.InputTaxReceivableAccountId, (settings, id) => settings with { InputTaxReceivableAccountId = id },
            "Tax paid on purchases, waiting to be recovered."),
        Slot("Accounts payable", nameof(BusinessAccountingSettingsDto.AccountsPayableAccountId), AccountKind.Liability,
            settings => settings.AccountsPayableAccountId, (settings, id) => settings with { AccountsPayableAccountId = id },
            "What suppliers are owed on a received bill."),
        Slot("Output tax payable", nameof(BusinessAccountingSettingsDto.OutputTaxPayableAccountId), AccountKind.Liability,
            settings => settings.OutputTaxPayableAccountId, (settings, id) => settings with { OutputTaxPayableAccountId = id },
            "Tax collected on sales until it is remitted."),
        Slot("GRNI clearing", nameof(BusinessAccountingSettingsDto.GrniClearingAccountId), AccountKind.Liability,
            settings => settings.GrniClearingAccountId, (settings, id) => settings with { GrniClearingAccountId = id },
            "Goods received but not yet invoiced by the supplier."),
        Slot("Customer deposits", nameof(BusinessAccountingSettingsDto.CustomerDepositsAccountId), AccountKind.Liability,
            settings => settings.CustomerDepositsAccountId, (settings, id) => settings with { CustomerDepositsAccountId = id },
            "Money taken before anything was delivered or invoiced."),
        Slot("Retained earnings", nameof(BusinessAccountingSettingsDto.RetainedEarningsAccountId), AccountKind.Equity,
            settings => settings.RetainedEarningsAccountId, (settings, id) => settings with { RetainedEarningsAccountId = id },
            "Where a fiscal year close carries the accumulated result."),
        Slot("Sales revenue", nameof(BusinessAccountingSettingsDto.SalesRevenueAccountId), AccountKind.Revenue,
            settings => settings.SalesRevenueAccountId, (settings, id) => settings with { SalesRevenueAccountId = id },
            "Recognised sale value."),
        Slot("Sales returns", nameof(BusinessAccountingSettingsDto.SalesReturnsAccountId), AccountKind.Revenue,
            settings => settings.SalesReturnsAccountId, (settings, id) => settings with { SalesReturnsAccountId = id },
            "Refunds and credit notes, kept visible rather than buried in revenue."),
        Slot("Cost of goods sold", nameof(BusinessAccountingSettingsDto.CogsAccountId), AccountKind.Expense,
            settings => settings.CogsAccountId, (settings, id) => settings with { CogsAccountId = id },
            "The cost released when stock is sold."),
        Slot("Purchase price variance", nameof(BusinessAccountingSettingsDto.PurchasePriceVarianceAccountId), AccountKind.Expense,
            settings => settings.PurchasePriceVarianceAccountId, (settings, id) => settings with { PurchasePriceVarianceAccountId = id },
            "The difference between a billed cost and the cost the receipt carried."),
        Slot("Inventory shrinkage", nameof(BusinessAccountingSettingsDto.InventoryShrinkageAccountId), AccountKind.Expense,
            settings => settings.InventoryShrinkageAccountId, (settings, id) => settings with { InventoryShrinkageAccountId = id },
            "What a stock count found missing."),
        Slot("Cash over and short", nameof(BusinessAccountingSettingsDto.CashOverShortAccountId), AccountKind.Expense,
            settings => settings.CashOverShortAccountId, (settings, id) => settings with { CashOverShortAccountId = id },
            "A drawer that does not balance, named rather than hidden in a plug."),
    ];

    public static readonly IReadOnlyList<(int Value, string Name)> Months =
    [
        (1, "January"), (2, "February"), (3, "March"), (4, "April"), (5, "May"), (6, "June"),
        (7, "July"), (8, "August"), (9, "September"), (10, "October"), (11, "November"), (12, "December"),
    ];

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _saveProblem;
    private string? _blockedFields;
    private string? _accountingReason;
    private bool _busy;
    private bool _started;
    private BusinessSettingsDto? _settings;
    private BusinessAccountingSettingsDto? _accounting;
    private BusinessAccountingSettingsDto? _draft;
    private IReadOnlyDictionary<Guid, ReferenceItem> _accountItems = new Dictionary<Guid, ReferenceItem>();

    private bool _canManageSettings;
    private bool _canManageAccounts;
    private bool _canReadMapping;
    private bool _structureLocked;

    private string _name = string.Empty;
    private string? _legalName;
    private string _timeZone = string.Empty;
    private int _fiscalYearStartMonth = 1;
    private string _invoicePrefix = string.Empty;
    private int _invoicePadding = 5;

    private BusinessLocationDto? _editingLocation;
    private bool _isNewLocation;
    private string _locationName = string.Empty;
    private string? _locationCode;
    private string? _locationAddress;
    private string? _locationTimeZone;

    private BusinessLocationDto? _deactivating;
    private string _deactivateReason = string.Empty;

    // Nothing is fetched during prerender; the operator's authority is only known on the first interactive pass.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started)
        {
            return;
        }

        _started = true;
        await LoadAsync().ConfigureAwait(false);
    }

    private bool FormReady => _settings is not null && !_busy;

    /// <summary>The version the form was filled from, in the text the save sends back as its precondition.</summary>
    private string VersionText => _settings?.Version.ToString(CultureInfo.InvariantCulture) ?? "-";

    private async Task LoadAsync()
    {
        _busy = true;
        _state = ShellStateKind.Loading;
        _message = null;
        _accountingReason = null;
        StateHasChanged();

        try
        {
            var session = await Session.GetSessionAsync().ConfigureAwait(false);

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManageSettings = AdministrationRules.Can(session.Permissions, Permissions.BusinessSettingsManage);
            _canReadMapping = AdministrationRules.Can(session.Permissions, Permissions.ReportsView);
            _canManageAccounts = AdministrationRules.Can(session.Permissions, Permissions.AccountingClose);

            if (!_canManageSettings)
            {
                // The route is hidden by the shell and refused by the server; this is the direct request.
                _state = ShellStateKind.Forbidden;
                _message = AdministrationRules.MissingReason("Business and branch setup", Permissions.BusinessSettingsManage);
                return;
            }

            _settings = await Setup.GetBusinessSettingsAsync().ConfigureAwait(false);
            ApplyForm(_settings);

            if (_canReadMapping)
            {
                try
                {
                    _accounting = await Accounting.GetSettingsAsync().ConfigureAwait(false);
                    _draft = _accounting;
                }
                catch (ApiException mappingFailure) when (mappingFailure.IsNotFound)
                {
                    // "This business has no mapping yet" is a different answer from "you may not see it", and the
                    // screen treats it that way: the slots open empty for somebody who may write them, rather than
                    // the whole page failing over a fact that simply has not been recorded (task 13, T13.03).
                    _accounting = null;
                    _draft = _canManageAccounts ? EmptyMapping(_settings) : null;
                    _accountingReason = _canManageAccounts
                        ? "Nothing is mapped to accounts yet. Choose an account for every rule below and save; the server refuses an inactive account, one from another business, or the wrong side of the ledger."
                        : "This business has no control-account mapping yet. Ask somebody with the accounting.close authority to set it before anything is posted.";
                }
            }
            else
            {
                _accounting = null;
                _draft = null;
                _accountingReason = AdministrationRules.MissingReason("Reading the posting accounts", Permissions.ReportsView);
            }

            _accountItems = await LoadAccountItemsAsync().ConfigureAwait(false);
            _state = ShellStateKind.Content;
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
            _message = Describe(exception);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void ApplyForm(BusinessSettingsDto settings)
    {
        _name = settings.Name;
        _legalName = settings.LegalName;
        _timeZone = settings.TimeZone;
        _fiscalYearStartMonth = settings.FiscalYearStartMonth;
        _invoicePrefix = settings.Numbering?.InvoicePrefix ?? "INV-";
        _invoicePadding = settings.Numbering?.InvoicePadding ?? 5;

        // The server's own structural test, shown before the operator tries to break it.
        _structureLocked = settings.Setup.HasPostedFacts;
        _blockedFields = _structureLocked
            ? $"Posted facts already exist, so these are fixed: base currency {settings.BaseCurrency}, settlement scale "
                + $"{ScaleText(settings.SettlementScale)}, fiscal year starting in "
                + $"{MonthName(settings.FiscalYearStartMonth)}. Everything else on this screen stays editable."
            : null;
    }

    private static string MonthName(int month)
        => Months.FirstOrDefault(item => item.Value == month).Name ?? month.ToString(CultureInfo.InvariantCulture);

    /// <summary>Money is settled to this many decimals; "not configured" is the honest answer, not a guess.</summary>
    private static string ScaleText(int? scale)
        => scale is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "not configured";

    /// <summary>
    /// The account picker searches the chart of accounts on the server. The operator types a name or a code and
    /// chooses; the identifier is never something they had to produce (requirement R07).
    /// </summary>
    private async Task<IReadOnlyList<ReferenceItem>> SearchAccountsAsync(string term, CancellationToken token)
    {
        var accounts = await Accounting.ListAccountsAsync(search: term, includeInactive: true, cancellationToken: token)
            .ConfigureAwait(false);

        return accounts
            .Select(account => ReferenceItem.From(account.Id, account.Name, account.IsActive ? account.Code : $"{account.Code} (inactive)"))
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<Guid, ReferenceItem>> LoadAccountItemsAsync()
    {
        var accounts = await Accounting.ListAccountsAsync(includeInactive: true).ConfigureAwait(false);

        return accounts.ToDictionary(account => account.Id, account => ReferenceItem.From(account.Id, account.Name, account.Code));
    }

    // ------------------------------------------------------------------ identity

    private async Task SaveIdentityAsync()
    {
        if (_settings is null || !FormReady)
        {
            return;
        }

        _busy = true;
        _saveProblem = null;

        try
        {
            // Currency and settlement scale are deliberately not sent: this screen does not own them, and a
            // blanket PUT must not quietly restate what the accounting settings already hold.
            var saved = await Setup.SaveBusinessSettingsAsync(new SaveBusinessSettingsRequest(
                _settings.Version,
                _name,
                _legalName,
                _timeZone,
                _fiscalYearStartMonth,
                BaseCurrency: null,
                SettlementScale: null,
                Numbering: new NumberingSettingsDto(_invoicePrefix, _invoicePadding))).ConfigureAwait(false);

            ApplyForm(saved);
            Snackbar.Add("Business identity saved.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _saveProblem = Describe(exception);

            if (exception.IsConflict)
            {
                await ReloadKeepingProblemAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ branches

    private void EditLocation(BusinessLocationDto location)
    {
        _deactivating = null;
        _isNewLocation = false;
        _editingLocation = location;
        _locationName = location.Name;
        _locationCode = location.Code;
        _locationAddress = location.Address;
        _locationTimeZone = location.TimeZone;
    }

    private void NewLocation()
    {
        _deactivating = null;
        _editingLocation = null;
        _isNewLocation = true;
        _locationName = string.Empty;
        _locationCode = null;
        _locationAddress = null;
        _locationTimeZone = _settings?.TimeZone;
    }

    private void CancelLocation()
    {
        _editingLocation = null;
        _isNewLocation = false;
    }

    private async Task SaveLocationAsync()
    {
        if (_settings is null || _locationName.Trim().Length == 0 || !FormReady)
        {
            return;
        }

        _busy = true;
        _saveProblem = null;

        try
        {
            if (_isNewLocation)
            {
                await Setup.CreateLocationAsync(new CreateLocationRequest(
                    _locationName, _locationCode, _locationAddress, _locationTimeZone)).ConfigureAwait(false);
                Snackbar.Add("Branch added.", Severity.Success);
            }
            else if (_editingLocation is { } existing)
            {
                await Setup.UpdateLocationAsync(existing.Id, new UpdateLocationRequest(
                    existing.Version, _locationName, _locationCode, _locationAddress, _locationTimeZone)).ConfigureAwait(false);
                Snackbar.Add("Branch saved.", Severity.Success);
            }

            CancelLocation();
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _saveProblem = Describe(exception);

            if (exception.IsConflict)
            {
                CancelLocation();
                await ReloadKeepingProblemAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private void BeginDeactivate(BusinessLocationDto location)
    {
        _editingLocation = null;
        _deactivating = location;
        _deactivateReason = string.Empty;
    }

    private void CancelDeactivate() => _deactivating = null;

    private async Task ConfirmDeactivateAsync()
    {
        if (_deactivating is not { } location || _deactivateReason.Trim().Length == 0 || !FormReady)
        {
            return;
        }

        _busy = true;
        _saveProblem = null;

        try
        {
            await Setup.DeactivateLocationAsync(location.Id, new DeactivateLocationRequest(location.Version, _deactivateReason))
                .ConfigureAwait(false);
            Snackbar.Add("Branch taken out of service. Its posted rows still point at it, so it stays readable.", Severity.Info);
            CancelDeactivate();
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _saveProblem = Describe(exception);

            if (exception.IsConflict)
            {
                CancelDeactivate();
                await ReloadKeepingProblemAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ------------------------------------------------------------------ mapping

    private ReferenceItem? Resolve(Guid accountId)
        => _accountItems.TryGetValue(accountId, out var item) ? item : null;

    private Task SlotChangedAsync(ControlAccountSlot slot, ReferenceItem? chosen)
    {
        if (_draft is null || chosen is null || chosen.Id == slot.Current(_draft))
        {
            return Task.CompletedTask;
        }

        _draft = slot.WithAccount(_draft, chosen.Id);
        return Task.CompletedTask;
    }

    private async Task SaveMappingAsync()
    {
        if (_draft is null || !_canManageAccounts || !FormReady)
        {
            return;
        }

        _busy = true;
        _saveProblem = null;

        try
        {
            _accounting = await Accounting.SaveSettingsAsync(ToRequest(_draft)).ConfigureAwait(false);
            _draft = _accounting;
            _accountItems = await LoadAccountItemsAsync().ConfigureAwait(false);
            Snackbar.Add("Posting accounts saved. New postings use them; posted journals keep what they were booked with.", Severity.Success);
        }
        catch (ApiException exception)
        {
            _saveProblem = Describe(exception);

            if (!exception.IsConflict)
            {
                // A refused mapping (wrong side of the ledger, another business's account) is re-read rather than
                // retried, so the operator sees what the server actually holds.
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    private static SaveBusinessAccountingSettingsRequest ToRequest(BusinessAccountingSettingsDto settings)
        => new(
            settings.SettlementScale,
            settings.CashAccountId,
            settings.BankClearingAccountId,
            settings.AccountsReceivableAccountId,
            settings.InventoryAssetAccountId,
            settings.DeliveredNotInvoicedAccountId,
            settings.InputTaxReceivableAccountId,
            settings.AccountsPayableAccountId,
            settings.OutputTaxPayableAccountId,
            settings.GrniClearingAccountId,
            settings.CustomerDepositsAccountId,
            settings.RetainedEarningsAccountId,
            settings.SalesRevenueAccountId,
            settings.SalesReturnsAccountId,
            settings.CogsAccountId,
            settings.PurchasePriceVarianceAccountId,
            settings.InventoryShrinkageAccountId,
            settings.CashOverShortAccountId,
            settings.RoundingAccountId);

    /// <summary>An unmapped business starts from nothing chosen, so the operator cannot save a half-truth by accident.</summary>
    private static BusinessAccountingSettingsDto EmptyMapping(BusinessSettingsDto? settings)
        => new(
            settings?.SettlementScale ?? 2,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);

    private static ControlAccountSlot Slot(
        string title,
        string key,
        AccountKind kind,
        Func<BusinessAccountingSettingsDto, Guid> current,
        Func<BusinessAccountingSettingsDto, Guid, BusinessAccountingSettingsDto> withAccount,
        string why)
        => new(title, key, kind, current, withAccount, why);

    // ------------------------------------------------------------------ helpers

    private async Task RefreshAsync()
    {
        _settings = await Setup.GetBusinessSettingsAsync().ConfigureAwait(false);
        ApplyForm(_settings);
        _accountItems = await LoadAccountItemsAsync().ConfigureAwait(false);
    }

    // A lost race says so and reloads, but keeps the sentence the server gave: the operator must be able to see
    // that their edit did not land, rather than watching a form silently reset.
    private async Task ReloadKeepingProblemAsync()
    {
        var problem = _saveProblem;
        await RefreshAsync().ConfigureAwait(false);
        _saveProblem = problem;
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
