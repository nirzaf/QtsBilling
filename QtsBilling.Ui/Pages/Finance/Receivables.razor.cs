using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Sales;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Finance;

/// <summary>
/// The receivables workbench (specification task 25). One customer's account is read as at a business date, and the
/// two operations that move its money - applying a deposit to exactly one document, and handing unapplied money
/// back - are offered only to a role the server will answer. Like every shared screen it holds one stable key per
/// intention, so a lost answer is resolved by re-sending exactly what was sent rather than by posting a second
/// movement that might land twice (T25.02).
/// </summary>
public partial class Receivables
{
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private ICustomerApiClient CustomersApi { get; set; } = default!;
    [Inject] private IPaymentApiClient Payments { get; set; } = default!;
    [Inject] private ICashRegisterApiClient Registers { get; set; } = default!;

    private readonly Held _apply = new();
    private readonly Held _refund = new();

    private ReferenceItem? _customer;
    private CustomerStatementDto? _statement;
    private IReadOnlyList<CustomerOpenItemDto> _openItems = [];
    private IReadOnlyList<CashRegisterDto> _registers = [];
    private DateTime? _asOf;
    private string? _error;
    private string? _notice;
    private bool _loading;
    private bool _busy;
    private bool _canRead;
    private bool _canApply;
    private bool _canRefund;
    private bool _canReadRegisters;
    private bool _registersReadFailed;
    private bool _sessionRead;
    private int _loadVersion;

    private Guid _depositId;
    private decimal _applyAmount;
    private string _refundMethod = "cash";
    private decimal _refundAmount;
    private Guid? _registerId;
    private string? _bankReference;
    private string? _refundReason;
    private CustomerOpenItemDto? _applyingTo;
    private CustomerDepositDto? _refunding;

    /// <summary>An intention whose key and body are held until the server has answered for it.</summary>
    private sealed class Held
    {
        public bool Outstanding { get; set; }

        public string? Key { get; set; }

        public void Release()
        {
            Outstanding = false;
            Key = null;
        }
    }

    /// <summary>Any unresolved money command locks both panels: neither may reprice while the other stands.</summary>
    private bool Applying => _apply.Outstanding || _refund.Outstanding || _busy;

    private CustomerOpenItemDto? ApplyingTo => _applyingTo;

    private CustomerDepositDto? Refunding => _refunding;

    private IReadOnlyList<CustomerDepositDto> DepositsWithBalance => (_statement?.Deposits ?? [])
        .Where(deposit => deposit.UnallocatedAmount > 0m)
        .ToArray();

    private CustomerDepositDto? DepositWithBalance => DepositsWithBalance.FirstOrDefault();

    private decimal ApplyCeiling
    {
        get
        {
            if (_applyingTo is null)
            {
                return 0m;
            }

            var deposit = (_statement?.Deposits ?? []).FirstOrDefault(row => row.PaymentId == _depositId) ?? DepositWithBalance;

            return Math.Min(_applyingTo.RemainingAmount, deposit?.UnallocatedAmount ?? 0m);
        }
    }

    private decimal ApplyStep => _statement?.SettlementScale is { } scale and > 0
        ? 1m / PowersOfTen(scale)
        : 0.01m;

    private IReadOnlyList<CashRegisterDto> OpenRegisters => _registers
        .Where(register => register.IsActive && register.CurrentSession is not null)
        .OrderBy(register => register.Name, StringComparer.Ordinal)
        .ToArray();

    private string CashRegisterHint => _registersReadFailed
        ? "This role cannot list the branch's drawers, so a cash refund cannot be named to one. Settle the refund as a bank movement instead."
        : OpenRegisters.Count > 0
            ? "Only a drawer whose session is open can pay money out, and it must actually hold the amount - the server refuses either way."
            : "No drawer on this terminal has an open session, so cash cannot leave one. Open a session, or settle the refund as a bank movement.";

    protected override async Task OnInitializedAsync()
    {
        // A prerender carries no session cookie, so the account is read on the interactive pass - the same rule
        // every shared screen follows, and the reason the first paint shows a loader rather than an invented table.
        _asOf = InvoiceRules.BusinessClock().ToDateTime(TimeOnly.MinValue);
        await LoadAsync(cancel: false);
    }

    private async Task ReadAsync()
    {
        _asOf ??= InvoiceRules.BusinessClock().ToDateTime(TimeOnly.MinValue);
        await LoadAsync(cancel: true);
    }

    /// <summary>
    /// Reads the account. Each load gets a token and a version, so an older statement cannot paint itself over the
    /// one the operator is now looking at - a receivable read is exactly where a stale balance would mislead.
    /// </summary>
    private async Task LoadAsync(bool cancel)
    {
        var version = ++_loadVersion;
        if (cancel)
        {
            CancelInFlight();
        }

        if (_customer is null)
        {
            _statement = null;
            _openItems = [];
            return;
        }

        _loading = true;
        _busy = true;
        _error = null;
        try
        {
            // What the screen may offer comes from the authorities this session actually holds - the same ones the
            // routes check - so an action is never presented to a role the server would refuse.
            if (!_sessionRead)
            {
                var session = await Accounts.GetSessionAsync();
                if (version != _loadVersion)
                {
                    return;
                }

                _canRead = session?.Permissions.Contains(Permissions.InvoicesIssue, StringComparer.Ordinal) == true
                    || session?.Permissions.Contains(Permissions.ReportsView, StringComparer.Ordinal) == true;
                _canApply = session?.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal) == true;
                _canRefund = _canApply;
                _canReadRegisters = session?.Permissions.Contains(Permissions.SalesPost, StringComparer.Ordinal) == true;
                _sessionRead = true;
            }

            if (!_canRead)
            {
                _error = "Your current role cannot view customer receivables.";
                return;
            }

            var asOf = DateOnly.FromDateTime(_asOf ?? InvoiceRules.BusinessClock().ToDateTime(TimeOnly.MinValue));
            var statement = await Payments.GetStatementAsync(_customer.Id, asOf);
            if (version != _loadVersion)
            {
                return;
            }

            _statement = statement;
            _openItems = await Payments.GetOpenItemsAsync(_customer.Id, asOf);
            if (version != _loadVersion)
            {
                return;
            }

            // The deposit chosen for an application is the one that still holds money, named by its reference.
            if (_depositId == Guid.Empty || DepositsWithBalance.All(deposit => deposit.PaymentId != _depositId))
            {
                _depositId = DepositWithBalance?.PaymentId ?? Guid.Empty;
            }
        }
        catch (ApiException exception)
        {
            if (version == _loadVersion)
            {
                _error = ReceivablesRules.RefusalSentence(exception, "read this account");
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
                _busy = false;
            }
        }
    }

    private async Task CustomerChangedAsync(ReferenceItem? customer)
    {
        _customer = customer;
        _statement = null;
        _openItems = [];
        _applyingTo = null;
        _refunding = null;
        _applyAmount = 0m;
        _refundAmount = 0m;
        _depositId = Guid.Empty;
        await ReadAsync();
    }

    private async Task<IReadOnlyList<ReferenceItem>> SearchCustomersAsync(string term, CancellationToken token)
    {
        var page = await CustomersApi.GetCustomersAsync(search: term, pageSize: 25, cancellationToken: token);
        return page.Items
            .Select(customer => ReferenceItem.From(customer.Id, customer.Name, customer.Phone))
            .ToList<ReferenceItem>();
    }

    private void StartApply(CustomerOpenItemDto target)
    {
        _refunding = null;
        _applyingTo = target;
        _applyAmount = Math.Min(target.RemainingAmount, DepositWithBalance?.UnallocatedAmount ?? 0m);
        _error = null;
        _notice = null;
    }

    private void CancelApply()
    {
        _applyingTo = null;
        _applyAmount = 0m;
    }

    private void StartRefund(CustomerDepositDto receipt)
    {
        _applyingTo = null;
        _refunding = receipt;
        _refundAmount = receipt.UnallocatedAmount;
        _error = null;
        _notice = null;
    }

    private void CancelRefund()
    {
        _refunding = null;
        _refundAmount = 0m;
        _refundReason = null;
    }

    private async Task OnRefundMethodChanged(string method)
    {
        _refundMethod = method;
        if (method == "cash")
        {
            await LoadRegistersAsync();
        }
    }

    private async Task LoadRegistersAsync()
    {
        if (!_canReadRegisters || _registers.Count > 0)
        {
            return;
        }

        try
        {
            _registers = await Registers.GetRegistersAsync();
            _registersReadFailed = false;
            _registerId = OpenRegisters.FirstOrDefault()?.Id;
        }
        catch (ApiException exception) when (exception.IsForbidden || exception.IsNotFound)
        {
            _registersReadFailed = true;
        }
    }

    /// <summary>
    /// Applies deposit money to the one document chosen. While the answer is outstanding the same key and the same
    /// target are re-sent, so the server's own replay returns the original application instead of a second one
    /// taking money off the same deposit twice (T25.02).
    /// </summary>
    private async Task ApplyAsync()
    {
        if (_applyingTo is not { } target || _busy)
        {
            return;
        }

        if (_depositId == Guid.Empty)
        {
            _error = "Choose which deposit this money comes from - a receipt the customer still has with you.";
            return;
        }

        if (_applyAmount <= 0m || _applyAmount > ApplyCeiling)
        {
            _error = $"An application must be at least {ReceivablesRules.MoneyText(ApplyStep, _statement)} and no more than the {ReceivablesRules.MoneyText(ApplyCeiling, _statement)} this document still owes and this deposit still holds.";
            return;
        }

        _apply.Key ??= ReceivablesRules.NewKey("apply", _depositId);
        _apply.Outstanding = true;
        _busy = true;
        _error = null;
        _notice = null;

        var target2 = target.TargetKind == "invoice"
            ? new AllocationTarget(target.Id, null, _applyAmount)
            : new AllocationTarget(null, target.Id, _applyAmount);

        try
        {
            var answer = await Payments.AllocateAsync(_depositId, new AllocateCustomerPaymentRequest([target2]), _apply.Key!);

            _apply.Release();
            _applyingTo = null;
            _applyAmount = 0m;
            _notice = $"{ReceivablesRules.MoneyText(target2.Amount, _statement)} was applied to {ReceivablesRules.DocumentText(target)}; {ReceivablesRules.MoneyText(answer.UnallocatedAmount, _statement)} of that receipt is still unapplied.";
            await ReloadQuietlyAsync();
        }
        catch (ApiException exception)
        {
            _error = ReceivablesRules.RefusalSentence(exception, $"apply {ReceivablesRules.MoneyText(_applyAmount, _statement)} to {ReceivablesRules.DocumentText(target)}");
            if (ReceivablesRules.CommittedNothing(exception.Code))
            {
                _apply.Release();
            }

            await ReloadQuietlyAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _error = "The answer about this application did not reach the screen, so its key and its figures are held exactly as they were. Press again to ask the server about the same application; a second one will not be invented.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AbandonApply()
    {
        _apply.Release();
        _applyingTo = null;
        _error = null;
        _notice = "The unanswered application is no longer held by this screen. The account has been read back: if the server did apply the money, it shows as settled; if it did not, the deposit is still unapplied.";
        await LoadAsync(cancel: true);
    }

    private async Task RefundAsync()
    {
        if (_refunding is not { } receipt || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_refundReason))
        {
            _error = "Money going back to a customer always names why; the reason is audited with the operator who authorised it.";
            return;
        }

        if (_refundAmount <= 0m || _refundAmount > receipt.UnallocatedAmount)
        {
            _error = $"A refund can be between {ReceivablesRules.MoneyText(ApplyStep, _statement)} and the {ReceivablesRules.MoneyText(receipt.UnallocatedAmount, _statement)} this receipt still holds unapplied.";
            return;
        }

        if (_refundMethod == "cash" && _registerId is null)
        {
            _error = "Cash leaves a specific drawer: choose the register whose session will pay it out.";
            return;
        }

        if (_refundMethod != "cash" && string.IsNullOrWhiteSpace(_bankReference))
        {
            _error = "A bank refund needs the reference the settlement will be matched by, so the movement can be traced both ways.";
            return;
        }

        _refund.Key ??= ReceivablesRules.NewKey("refund", receipt.PaymentId);
        _refund.Outstanding = true;
        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var answer = await Payments.RefundUnallocatedAsync(
                receipt.PaymentId,
                new RefundUnallocatedRequest(
                    _refundAmount,
                    _refundReason.Trim(),
                    _refundMethod,
                    _refundMethod == "cash" ? _registerId : null,
                    _refundMethod == "cash" ? null : _bankReference?.Trim()),
                _refund.Key!);

            _refund.Release();
            _refunding = null;
            _refundAmount = 0m;
            _refundReason = null;
            _bankReference = null;
            _notice = $"{ReceivablesRules.MoneyText(answer.Amount, _statement)} was handed back"
                + (answer.Method.Equals("Cash", StringComparison.OrdinalIgnoreCase)
                    ? " out of the drawer, journalled as "
                    : $" by bank settlement {answer.BankReference}, journalled as ")
                + answer.JournalEntryNumber
                + $". {ReceivablesRules.MoneyText(answer.RemainingUnapplied, _statement)} of this receipt is still unapplied.";
            await ReloadQuietlyAsync();
        }
        catch (ApiException exception)
        {
            _error = ReceivablesRules.RefusalSentence(exception, $"refund {ReceivablesRules.MoneyText(_refundAmount, _statement)} of the deposit");
            if (ReceivablesRules.CommittedNothing(exception.Code))
            {
                _refund.Release();
            }

            await ReloadQuietlyAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _error = "The answer about this refund did not reach the screen, so its key and its figures are held. Press again to ask the server about the same refund - money is never handed back twice to a lost response.";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Re-reads the account without clearing the message the operator is being shown.</summary>
    private async Task ReloadQuietlyAsync()
    {
        var keep = (_error, _notice);
        await LoadAsync(cancel: false);
        (_error, _notice) = keep;
    }

    protected override void OnParametersSet()
    {
        // Nothing to bind: the parameters are route-free, and the session is read once on the interactive pass.
    }

    private void CancelInFlight()
    {
        // An older read is discarded by version, so a slow statement cannot overwrite the account now shown.
        _loading = false;
    }

    public ValueTask DisposeAsync()
    {
        _loadVersion++;
        return ValueTask.CompletedTask;
    }

    private static decimal PowersOfTen(int power)
    {
        var result = 1m;
        for (var i = 0; i < power; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
