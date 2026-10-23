using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;

namespace QtsBilling.Ui.Pages.Accounting;

/// <summary>
/// The chart of accounts and the business's control-account mapping (specification task 32, recipe step 1), on the
/// same contract the posting path reads: an operator sees the account each rule will use, chooses accounts by name,
/// and can read the history of an account that is taken out of service without being able to rewrite it. Adding an
/// account, renaming one and deactivating one are three separate acts here; none of them posts anything.
/// </summary>
public partial class Accounts : IAsyncDisposable
{
    private static readonly (AccountKind Kind, string Word)[] Kinds =
    [
        (AccountKind.Asset, "Asset"),
        (AccountKind.Liability, "Liability"),
        (AccountKind.Equity, "Equity"),
        (AccountKind.Revenue, "Revenue"),
        (AccountKind.Expense, "Expense"),
    ];

    [Inject] private IAccountingApiClient Ledger { get; set; } = default!;
    [Inject] private IAccountApiClient AccountsApi { get; set; } = default!;

    private readonly HeldIntention<SaveAccountRequest> _create = new("new account");
    private readonly HeldIntention<UpdateAccountRequest> _change = new("account change");
    private readonly HeldIntention<SaveBusinessAccountingSettingsRequest> _mapping = new("control mapping");

    private IReadOnlyList<AccountDto> _chart = [];
    private BusinessAccountingSettingsDto? _settings;
    private BusinessAccountingSettingsDto _draft = BlankSettings();
    private AccountDto? _selected;
    private ReferenceItem? _parentChoice;
    private string _newCode = string.Empty;
    private string _newName = string.Empty;
    private AccountKind _newKind = AccountKind.Asset;
    private string _newKindWord = "Asset";
    private string _rename = string.Empty;
    private string? _error;
    private string? _notice;
    private string? _message;
    private ShellStateKind _state = ShellStateKind.Loading;
    private bool _busy;
    private bool _canPost;
    private bool _canClose;
    private bool _started;
    private int _loadVersion;
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Every slot the posting rules consult, in the words an accountant uses for it, with the kind the server
    /// insists on. The list is the same mapping the ledger reads, so a slot shown here cannot differ from the
    /// account a journal will use (D05).
    /// </summary>
    public static IReadOnlyList<ControlAccountSlot> Slots { get; } =
    [
        Slot("Cash on hand", nameof(BusinessAccountingSettingsDto.CashAccountId), AccountKind.Asset, "Tills and cash drawers", (s, a) => s with { CashAccountId = a }),
        Slot("Bank clearing", nameof(BusinessAccountingSettingsDto.BankClearingAccountId), AccountKind.Asset, "Card and bank settlements", (s, a) => s with { BankClearingAccountId = a }),
        Slot("Accounts receivable", nameof(BusinessAccountingSettingsDto.AccountsReceivableAccountId), AccountKind.Asset, "Invoices issued to customers", (s, a) => s with { AccountsReceivableAccountId = a }),
        Slot("Inventory", nameof(BusinessAccountingSettingsDto.InventoryAssetAccountId), AccountKind.Asset, "Stock on hand at every branch", (s, a) => s with { InventoryAssetAccountId = a }),
        Slot("Delivered not invoiced", nameof(BusinessAccountingSettingsDto.DeliveredNotInvoicedAccountId), AccountKind.Asset, "Goods out before the invoice exists", (s, a) => s with { DeliveredNotInvoicedAccountId = a }),
        Slot("Input tax receivable", nameof(BusinessAccountingSettingsDto.InputTaxReceivableAccountId), AccountKind.Asset, "Tax paid on purchases", (s, a) => s with { InputTaxReceivableAccountId = a }),
        Slot("Accounts payable", nameof(BusinessAccountingSettingsDto.AccountsPayableAccountId), AccountKind.Liability, "Supplier bills matched and owed", (s, a) => s with { AccountsPayableAccountId = a }),
        Slot("Output tax payable", nameof(BusinessAccountingSettingsDto.OutputTaxPayableAccountId), AccountKind.Liability, "Tax collected on sales", (s, a) => s with { OutputTaxPayableAccountId = a }),
        Slot("Goods received not invoiced", nameof(BusinessAccountingSettingsDto.GrniClearingAccountId), AccountKind.Liability, "Deliveries booked before the bill arrives", (s, a) => s with { GrniClearingAccountId = a }),
        Slot("Customer deposits", nameof(BusinessAccountingSettingsDto.CustomerDepositsAccountId), AccountKind.Liability, "Money taken and not yet applied", (s, a) => s with { CustomerDepositsAccountId = a }),
        Slot("Retained earnings", nameof(BusinessAccountingSettingsDto.RetainedEarningsAccountId), AccountKind.Equity, "Where each fiscal close leaves the year", (s, a) => s with { RetainedEarningsAccountId = a }),
        Slot("Sales revenue", nameof(BusinessAccountingSettingsDto.SalesRevenueAccountId), AccountKind.Revenue, "Revenue recognised on a sale", (s, a) => s with { SalesRevenueAccountId = a }),
        Slot("Sales returns", nameof(BusinessAccountingSettingsDto.SalesReturnsAccountId), AccountKind.Revenue, "Contra-revenue for returned goods", (s, a) => s with { SalesReturnsAccountId = a }),
        Slot("Cost of goods sold", nameof(BusinessAccountingSettingsDto.CogsAccountId), AccountKind.Expense, "The stock value a sale consumed", (s, a) => s with { CogsAccountId = a }),
        Slot("Purchase price variance", nameof(BusinessAccountingSettingsDto.PurchasePriceVarianceAccountId), AccountKind.Expense, "Where a bill's price differs from the order", (s, a) => s with { PurchasePriceVarianceAccountId = a }),
        Slot("Inventory shrinkage", nameof(BusinessAccountingSettingsDto.InventoryShrinkageAccountId), AccountKind.Expense, "Stock written off by a count", (s, a) => s with { InventoryShrinkageAccountId = a }),
        Slot("Cash over short", nameof(BusinessAccountingSettingsDto.CashOverShortAccountId), AccountKind.Expense, "What a drawer count cannot explain", (s, a) => s with { CashOverShortAccountId = a }),
    ];

    private ReferenceItem? ReferenceFor(ControlAccountSlot slot)
    {
        var account = AccountFor(slot.Current(_draft));
        return account is null ? null : ReferenceItem.From(account.Id, account.Name, account.Code);
    }

    private string SelectedSentence(AccountDto account)
        => account.IsActive
            ? $"{account.Code} is in service now, and the history it already holds stays readable whichever way this screen moves."
            : $"{account.Code} is out of service: it cannot be chosen for new work, and its posted history is untouched.";

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
            var session = await AccountsApi.GetSessionAsync(_pending.Token).ConfigureAwait(false);
            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                _message = "Sign in to read the chart of accounts.";
                return;
            }

            _canPost = session.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal);
            _canClose = session.Permissions.Contains(Permissions.AccountingClose, StringComparer.Ordinal);

            _chart = await Ledger.ListAccountsAsync(includeInactive: true, cancellationToken: _pending.Token).ConfigureAwait(false);
            _settings = await Ledger.GetSettingsAsync(_pending.Token).ConfigureAwait(false);
            _draft = _settings;
            _selected ??= _chart.OrderBy(account => account.Code, StringComparer.Ordinal).FirstOrDefault();
            _rename = _selected?.Name ?? string.Empty;
            _state = _chart.Count == 0 ? ShellStateKind.Empty : ShellStateKind.Content;
            if (_chart.Count == 0)
            {
                _message = "This business has no accounts yet. Add the accounts the posting rules need, then map them below.";
            }
        }
        catch (ApiException exception)
        {
            _state = exception.StatusCode is 401 or 403 ? ShellStateKind.Forbidden : ShellStateKind.Failure;
            _message = _state == ShellStateKind.Forbidden
                ? $"Reading the chart needs the '{Permissions.ReportsView}' authority, which this account does not hold in this business."
                : AccountingRules.RefusalSentence(exception, "the chart");
            _error = _message;
        }
        catch (OperationCanceledException)
        {
            // A superseded read is not a failure.
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

    private static ControlAccountSlot Slot(
        string title,
        string key,
        AccountKind expected,
        string why,
        Func<BusinessAccountingSettingsDto, Guid, BusinessAccountingSettingsDto> withAccount)
        => new(title, key, expected, SlotCurrent(key), withAccount, why);

    private static Func<BusinessAccountingSettingsDto, Guid> SlotCurrent(string key) => settings => key switch
    {
        nameof(BusinessAccountingSettingsDto.CashAccountId) => settings.CashAccountId,
        nameof(BusinessAccountingSettingsDto.BankClearingAccountId) => settings.BankClearingAccountId,
        nameof(BusinessAccountingSettingsDto.AccountsReceivableAccountId) => settings.AccountsReceivableAccountId,
        nameof(BusinessAccountingSettingsDto.InventoryAssetAccountId) => settings.InventoryAssetAccountId,
        nameof(BusinessAccountingSettingsDto.DeliveredNotInvoicedAccountId) => settings.DeliveredNotInvoicedAccountId,
        nameof(BusinessAccountingSettingsDto.InputTaxReceivableAccountId) => settings.InputTaxReceivableAccountId,
        nameof(BusinessAccountingSettingsDto.AccountsPayableAccountId) => settings.AccountsPayableAccountId,
        nameof(BusinessAccountingSettingsDto.OutputTaxPayableAccountId) => settings.OutputTaxPayableAccountId,
        nameof(BusinessAccountingSettingsDto.GrniClearingAccountId) => settings.GrniClearingAccountId,
        nameof(BusinessAccountingSettingsDto.CustomerDepositsAccountId) => settings.CustomerDepositsAccountId,
        nameof(BusinessAccountingSettingsDto.RetainedEarningsAccountId) => settings.RetainedEarningsAccountId,
        nameof(BusinessAccountingSettingsDto.SalesRevenueAccountId) => settings.SalesRevenueAccountId,
        nameof(BusinessAccountingSettingsDto.SalesReturnsAccountId) => settings.SalesReturnsAccountId,
        nameof(BusinessAccountingSettingsDto.CogsAccountId) => settings.CogsAccountId,
        nameof(BusinessAccountingSettingsDto.PurchasePriceVarianceAccountId) => settings.PurchasePriceVarianceAccountId,
        nameof(BusinessAccountingSettingsDto.InventoryShrinkageAccountId) => settings.InventoryShrinkageAccountId,
        nameof(BusinessAccountingSettingsDto.CashOverShortAccountId) => settings.CashOverShortAccountId,
        _ => throw new ArgumentException($"Unknown control-account slot '{key}'.", nameof(key)),
    };

    private AccountDto? AccountFor(Guid id) => _chart.FirstOrDefault(account => account.Id == id);

    private string SlotSentence(ControlAccountSlot slot)
    {
        var chosen = AccountFor(slot.Current(_draft));
        if (chosen is null)
        {
            return $"{slot.Title}: nothing mapped yet. {slot.Why} needs an {slot.ExpectedKind.ToString().ToLowerInvariant()} account.";
        }

        return AccountingRules.CanHoldMapping(chosen.AccountType, slot.ExpectedKind)
            ? $"{slot.Title}: {chosen.Name} ({chosen.Code}) - {slot.Why}."
            : $"{slot.Title}: {chosen.Name} ({chosen.Code}) is {chosen.AccountType}, and the server will refuse it - {slot.Why} needs a {slot.ExpectedKind.ToString().ToLowerInvariant()} account.";
    }

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

    private void ChooseSlotAccount(ControlAccountSlot slot, ReferenceItem? chosen)
    {
        if (chosen is null)
        {
            return;
        }

        _draft = slot.WithAccount(_draft, chosen.Id);
    }

    private void ChooseScale(string text)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var scale)
            && scale is >= 0 and <= 4)
        {
            _draft = _draft with { SettlementScale = scale };
        }
    }

    private void SelectAccount(AccountDto account)
    {
        _selected = account;
        _rename = account.Name;
        _parentChoice = ReferenceFor(account.ParentId ?? Guid.Empty);
    }

    private ReferenceItem? ReferenceFor(Guid id)
    {
        var account = AccountFor(id);
        return account is null ? null : ReferenceItem.From(account.Id, account.Name, account.Code);
    }

    private void KindChosen(string word)
    {
        _newKindWord = word;
        _newKind = Kinds.First(kind => string.Equals(kind.Word, word, StringComparison.Ordinal)).Kind;
    }

    private async Task CreateAsync()
    {
        var code = _newCode.Trim();
        var name = _newName.Trim();
        if (code.Length == 0 || name.Length == 0)
        {
            _error = "An account needs its own code and a name an operator will recognise.";
            return;
        }

        var body = new SaveAccountRequest(code, name, _newKind, _parentChoice?.Id);
        var key = _create.Key ??= AccountingRules.NewKey($"account-{code}", Guid.CreateVersion7());
        _create.Body = body;

        await SendAsync(_create, () => Ledger.CreateAccountAsync(body), "the new account", reload: true).ConfigureAwait(false);
        if (!_create.Outstanding)
        {
            _newCode = string.Empty;
            _newName = string.Empty;
        }
    }

    private async Task RenameAsync()
    {
        if (_selected is not { } account)
        {
            _error = "Choose the account whose name is changing.";
            return;
        }

        var name = _rename.Trim();
        if (name.Length == 0)
        {
            _error = "An account keeps a name; deactivating it is a separate act.";
            return;
        }

        if (name == account.Name)
        {
            _error = "That is already the account's name.";
            return;
        }

        var body = new UpdateAccountRequest(name, account.ParentId, account.IsActive);
        var key = _change.Key ??= AccountingRules.NewKey($"account-rename-{account.Code}", account.Id);
        _change.Body = body;

        await SendAsync(_change, () => Ledger.UpdateAccountAsync(account.Id, body), $"the name of {account.Code}", reload: true)
            .ConfigureAwait(false);
    }

    private async Task SetActiveAsync(AccountDto account, bool active)
    {
        var body = new UpdateAccountRequest(account.Name, account.ParentId, active);
        var key = _change.Key ??= AccountingRules.NewKey($"account-{(active ? "activate" : "deactivate")}-{account.Code}", account.Id);
        _change.Body = body;

        await SendAsync(_change, () => Ledger.UpdateAccountAsync(account.Id, body),
            active ? $"{account.Code} reactivated" : $"{account.Code} taken out of service", reload: true).ConfigureAwait(false);
    }

    private async Task SaveMappingAsync()
    {
        if (_settings is null)
        {
            _error = "Read the current mapping before changing it.";
            return;
        }

        var body = new SaveBusinessAccountingSettingsRequest(
            _draft.SettlementScale, _draft.CashAccountId, _draft.BankClearingAccountId, _draft.AccountsReceivableAccountId,
            _draft.InventoryAssetAccountId, _draft.DeliveredNotInvoicedAccountId, _draft.InputTaxReceivableAccountId,
            _draft.AccountsPayableAccountId, _draft.OutputTaxPayableAccountId, _draft.GrniClearingAccountId,
            _draft.CustomerDepositsAccountId, _draft.RetainedEarningsAccountId, _draft.SalesRevenueAccountId,
            _draft.SalesReturnsAccountId, _draft.CogsAccountId, _draft.PurchasePriceVarianceAccountId,
            _draft.InventoryShrinkageAccountId, _draft.CashOverShortAccountId, _draft.RoundingAccountId);

        var key = _mapping.Key ??= AccountingRules.NewKey("control-mapping", Guid.CreateVersion7());
        _mapping.Body = body;

        await SendAsync(_mapping, () => Ledger.SaveSettingsAsync(body), "the control-account mapping", reload: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one act under its held key. A lost answer keeps both key and body so the button asks about the same act;
    /// only a refusal that proves nothing was written releases it (specification F04).
    /// </summary>
    private async Task SendAsync<TBody>(
        HeldIntention<TBody> intention, Func<Task> send, string what, bool reload)
    {
        _busy = true;
        _error = null;
        _notice = null;
        try
        {
            await send().ConfigureAwait(false);
            _notice = $"{char.ToUpperInvariant(what[0])}{what[1..]} answered.";
            intention.Release();
            if (reload)
            {
                await ReloadAsync().ConfigureAwait(false);
            }
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

    private async Task ReloadAsync()
    {
        try
        {
            _chart = await Ledger.ListAccountsAsync(includeInactive: true).ConfigureAwait(false);
            _settings = await Ledger.GetSettingsAsync().ConfigureAwait(false);
            _draft = _settings;
            if (_selected is { } chosen)
            {
                _selected = _chart.FirstOrDefault(account => account.Id == chosen.Id) ?? _selected;
            }
        }
        catch (ApiException exception)
        {
            _error = AccountingRules.RefusalSentence(exception, "the chart re-read");
        }
    }

    private static BusinessAccountingSettingsDto BlankSettings() => new(
        2, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
        Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
}
