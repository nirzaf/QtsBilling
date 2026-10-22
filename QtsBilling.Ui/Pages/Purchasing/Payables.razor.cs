using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Models;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Purchasing;

/// <summary>
/// The payables workbench (specification task 27, recipe step 5): one supplier's account as at a business date, the
/// documents still owed - issued bills and imported opening payables together - the credits held against them, the
/// settlements that close them, and the matching of a delivery to a bill. Every figure shown is the server's, and the
/// three writes this screen can make each hold one stable key while their answer is outstanding, so a lost response
/// is recovered by asking the server about the same document rather than by paying a supplier twice.
/// </summary>
public partial class Payables : IAsyncDisposable
{
    [Inject] private IPurchasingApiClient Purchasing { get; set; } = default!;
    [Inject] private ISupplierApiClient Suppliers { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;
    [Inject] private IInventoryApiClient Inventory { get; set; } = default!;

    private readonly Dictionary<SupplierOpenItemDto, decimal> _settle = [];
    private readonly List<BillLineEntry> _draftLines = [];
    private readonly HeldIntention<CreateSupplierBillRequest> _match = new("match");
    private readonly HeldIntention<string> _issue = new("issue");
    private readonly HeldIntention<CreateSupplierPaymentRequest> _settleIntention = new("settle");
    private readonly HeldIntention<CreateSupplierCreditRequest> _credit = new("credit");
    private readonly HeldIntention<ApplySupplierCreditRequest> _apply = new("apply");

    private IReadOnlyList<BusinessLocationDto> _locations = [];
    private IReadOnlyList<SupplierBillDto> _draftBills = [];
    private IReadOnlyList<PurchaseOrderSummaryDto> _orders = [];
    private IReadOnlyList<PurchaseReceiptDto> _receipts = [];
    private SupplierStatementDto? _statement;

    private ReferenceItem? _supplier;
    private ReferenceItem? _order;
    private ReferenceItem? _creditBill;
    private ReferenceItem? _returnSource;
    private SupplierCreditDto? _appliedCredit;
    private SupplierOpenItemDto? _applyTarget;

    private DateTime? _asOf;
    private DateTime? _billDate;
    private DateTime? _dueDate;
    private DateTime? _paidOn;
    private string? _supplierReference;
    private string? _paymentReference;
    private string? _method = "cash";
    private string? _creditReason;
    private decimal _creditQuantity;
    private decimal _applyAmount;

    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;
    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _canManage;
    private bool _canPost;
    private bool _started;
    private bool _matching;
    private int _loadVersion;

    private string? Currency => _statement?.Currency;

    private SupplierBillLineDto? CreditBillLine => CreditedBill?.Lines.FirstOrDefault(PayablesRules.IsOpen);

    /// <summary>The bill chosen for a credit, read from the bills this account read returned.</summary>
    private SupplierBillDto? CreditedBill => _creditBill is null
        ? null
        : _bills.FirstOrDefault(candidate => candidate.Id == _creditBill.Id);

    private IReadOnlyList<SupplierBillDto> _bills = [];

    private IEnumerable<(PurchaseReceiptDto Receipt, PurchaseReceiptLineDto Line)> BillableLines => _receipts
        .Where(receipt => !PurchasingRules.IsReturn(receipt.Kind))
        .SelectMany(receipt => receipt.Lines
            .Where(line => line.Quantity - line.QuantityBilled > 0m)
            .Select(line => (receipt, line)));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_started)
        {
            _started = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        _state = ShellStateKind.Loading;
        _message = null;
        _statement = null;

        try
        {
            var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (session is null)
            {
                _state = ShellStateKind.Unauthenticated;
                return;
            }

            _canManage = session.Permissions.Contains(Permissions.PurchasingManage, StringComparer.Ordinal);
            _canPost = session.Permissions.Contains(Permissions.AccountingPost, StringComparer.Ordinal);

            if (!_canManage)
            {
                _state = ShellStateKind.Forbidden;
                _message = $"Reading a supplier's account needs the '{Permissions.PurchasingManage}' authority, which this account does not hold in this business.";
                return;
            }

            _locations = await Inventory.GetLocationsAsync().ConfigureAwait(false);

            if (version != _loadVersion)
            {
                return;
            }

            if (_supplier is null)
            {
                _state = ShellStateKind.Empty;
                _message = "Choose the supplier whose account you need. The list is searched by name, so no identifier is asked for.";
                return;
            }

            await ReadAccountAsync(version).ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                404 => ShellStateKind.Empty,
                409 => ShellStateKind.Conflict,
                _ => ShellStateKind.Failure,
            };
            _message = PayablesRules.RefusalSentence(exception, "this account");
        }
        finally
        {
            if (version == _loadVersion)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>The account read and the two lists that drive its panels, loaded together so they cannot disagree.</summary>
    private async Task ReadAccountAsync(int version)
    {
        var asOf = _asOf is { } date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        var statement = await Purchasing.GetStatementAsync(_supplier!.Id, asOf).ConfigureAwait(false);

        if (version != _loadVersion)
        {
            return;
        }

        _statement = statement;
        _bills = await Purchasing.ListBillsAsync(_supplier.Id, openOnly: false).ConfigureAwait(false);

        if (version != _loadVersion)
        {
            return;
        }

        _draftBills = [.. _bills.Where(bill => bill.Status == SupplierBillState.Draft)];

        // The orders that can still be billed: anything placed, because a fully delivered order is exactly what a
        // supplier's invoice arrives against.
        var placed = await Purchasing.ListOrdersAsync(PurchaseOrderState.Ordered, supplierId: _supplier.Id, pageSize: 50).ConfigureAwait(false);
        var partial = await Purchasing.ListOrdersAsync(PurchaseOrderState.PartiallyReceived, supplierId: _supplier.Id, pageSize: 50).ConfigureAwait(false);
        var complete = await Purchasing.ListOrdersAsync(PurchaseOrderState.Received, supplierId: _supplier.Id, pageSize: 50).ConfigureAwait(false);

        if (version != _loadVersion)
        {
            return;
        }

        _orders = [.. placed.Items, .. partial.Items, .. complete.Items];

        if (_order is { } chosen)
        {
            _receipts = await Purchasing.ListReceiptsAsync(chosen.Id).ConfigureAwait(false);
            if (version != _loadVersion)
            {
                return;
            }
        }
        else
        {
            _receipts = [];
        }

        _state = ShellStateKind.Content;
    }

    private async Task SupplierChangedAsync(ReferenceItem? supplier)
    {
        _supplier = supplier;
        ClearIntentions();
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task OrderChangedAsync(ReferenceItem? order)
    {
        _order = order;
        _draftLines.Clear();
        _error = null;

        if (order is null)
        {
            _receipts = [];
            return;
        }

        try
        {
            _receipts = await Purchasing.ListReceiptsAsync(order.Id).ConfigureAwait(false);
            _returns = [.. _receipts.Where(receipt => PurchasingRules.IsReturn(receipt.Kind))];
            _returnSource = null;
        }
        catch (ApiException exception)
        {
            _error = PayablesRules.RefusalSentence(exception, "its deliveries");
        }
    }

    private IReadOnlyList<PurchaseReceiptDto> _returns = [];

    private void AddBillableLine(PurchaseReceiptDto receipt, PurchaseReceiptLineDto line)
    {
        var billable = line.Quantity - line.QuantityBilled;

        if (billable <= 0m || _draftLines.Any(entry => entry.ReceiptLineId == line.Id))
        {
            return;
        }

        _draftLines.Add(new BillLineEntry(line.Id, line.ProductName, receipt.ReceiptNumber, billable, billable,
            line.UnitCost, 0m, 0m, "Exclusive"));
    }

    private void RemoveBillLine(BillLineEntry entry) => _draftLines.Remove(entry);

    private async Task MatchBillAsync()
    {
        if (_supplier is null || _order is null || _busy)
        {
            _error = "Choose the supplier and the order the supplier's invoice arrived against.";
            return;
        }

        if (_draftLines.Count == 0)
        {
            _error = "Add at least one delivered line to the bill. Only quantities the delivery holds can be matched.";
            return;
        }

        var invalid = _draftLines.FirstOrDefault(entry => entry.Quantity <= 0m || entry.Quantity > entry.Ceiling
            || entry.UnitCost < 0m || entry.DiscountAmount < 0m || entry.TaxRate < 0m || entry.TaxRate > 100m);

        if (invalid is not null)
        {
            _error = $"Check \"{invalid.ProductName}\": the quantity must be between 0 and the {PurchasingRules.QuantityText(invalid.Ceiling)} this line has not been billed for, and the cost, discount and tax rate within their bounds.";
            return;
        }

        var locationId = LocationOf(_order);
        var request = new CreateSupplierBillRequest(
            _supplier.Id,
            locationId,
            _order.Id,
            string.IsNullOrWhiteSpace(_supplierReference) ? null : _supplierReference.Trim(),
            _billDate is { } billed
                ? DateOnly.FromDateTime(billed)
                : InvoiceRules.BusinessClock(BranchZone(locationId)),
            _dueDate is { } due ? DateOnly.FromDateTime(due) : null,
            [.. _draftLines.Select(entry => new SupplierBillLineRequest(
                entry.ReceiptLineId, entry.Quantity, entry.UnitCost, entry.DiscountAmount, entry.TaxRate / 100m, entry.TaxMode))]);

        await SendAsync(_match, request, "the supplier bill",
            (body, key) => Purchasing.CreateBillAsync(body, key)).ConfigureAwait(false);
        _draftLines.Clear();
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task IssueBillAsync(SupplierBillDto bill)
    {
        var key = PayablesRules.NewKey("issue", bill.Id);
        await SendAsync(_issue, key, $"issuing {bill.BillNumber}",
            (_, held) => Purchasing.IssueBillAsync(bill.Id, held)).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task SettleAsync()
    {
        if (_supplier is null || _busy)
        {
            return;
        }

        var targets = _settle.Where(pair => pair.Value > 0m)
            .Select(pair => new SupplierAllocationTarget(
                pair.Key.TargetKind == "bill" ? pair.Key.Id : null,
                pair.Key.TargetKind == "opening" ? pair.Key.Id : null,
                pair.Value))
            .ToArray();

        if (targets.Length == 0)
        {
            _error = "State what is being settled on at least one document. Each target is paid in full or in part, and a payment that names nothing is refused.";
            return;
        }

        var total = targets.Sum(target => target.Amount);
        var currency = _statement?.Currency ?? "QAR";
        var request = new CreateSupplierPaymentRequest(
            _supplier.Id,
            _method ?? "cash",
            total,
            // The settlement date is the business's own trading day, not the machine's calendar (finding F21).
            _paidOn is { } paid
                ? DateOnly.FromDateTime(paid)
                : InvoiceRules.BusinessClock(_statement?.TimeZoneId),
            currency,
            string.IsNullOrWhiteSpace(_paymentReference) ? null : _paymentReference.Trim(),
            targets);

        await SendAsync(_settleIntention, request, $"the settlement of {PayablesRules.Money(total, _statement)} {currency}",
            (body, key) => Purchasing.PaySupplierAsync(body, key)).ConfigureAwait(false);
        _settle.Clear();
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task CreditAsync()
    {
        if (_supplier is null || _busy)
        {
            return;
        }

        var bill = CreditedBill;
        var line = bill?.Lines.FirstOrDefault(candidate => PayablesRules.IsOpen(candidate) && _creditQuantity > 0m);

        if (bill is null || line is null)
        {
            _error = "Choose the bill the supplier is crediting, and a quantity within what its line can still yield.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_creditReason))
        {
            _error = "State why the credit exists: the reason is audited with whoever issues it, and whoever reconciles the account reads it.";
            return;
        }

        var sourceReceipt = _returnSource is null
            ? null
            : _returns.FirstOrDefault(receipt => receipt.Id == _returnSource.Id);

        if (_returnSource is not null && sourceReceipt is null)
        {
            _error = "The return chosen for this credit is no longer listed against that order. Read the bill back before issuing the credit.";
            return;
        }

        var request = new CreateSupplierCreditRequest(
            _creditReason.Trim(),
            sourceReceipt?.Id,
            [new SupplierCreditLineRequest(line.Id, _creditQuantity, sourceReceipt?.Lines.FirstOrDefault()?.Id)]);

        await SendAsync(_credit, request, "the supplier credit",
            (body, key) => Purchasing.CreateCreditAsync(bill.Id, body, key)).ConfigureAwait(false);
        _creditQuantity = 0m;
        _creditReason = null;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task ApplyCreditAsync()
    {
        if (_appliedCredit is null || _applyTarget is null || _busy)
        {
            _error = "Choose the credit to use and the document it should settle.";
            return;
        }

        var request = new ApplySupplierCreditRequest(
            _applyTarget.TargetKind == "bill" ? _applyTarget.Id : null,
            _applyTarget.TargetKind == "opening" ? _applyTarget.Id : null,
            _applyAmount);

        await SendAsync(_apply, request, $"applying {PayablesRules.Money(_applyAmount, _statement)} of credit",
            (body, key) => Purchasing.ApplyCreditAsync(_appliedCredit.Id, body, key)).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one intention under one key. A refusal that proves nothing was written releases the key; a lost answer
    /// holds the key and the exact body, so the next press re-asks about the same document (recipe step 5's recovery
    /// rule, proved by task 27's replay cases).
    /// </summary>
    private async Task SendAsync<TBody>(
        HeldIntention<TBody> intention,
        TBody body,
        string what,
        Func<TBody, string, Task> send)
    {
        intention.Key ??= PayablesRules.NewKey(intention.Name, _supplier?.Id ?? Guid.Empty);
        intention.Body ??= body;

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            await send(intention.Body, intention.Key).ConfigureAwait(false);
            intention.Release();
            _notice = $"{char.ToUpperInvariant(what[0])}{what[1..]} was recorded. The account has been read back from the server, so the figures below are its answer, not this screen's.";
        }
        catch (ApiException exception)
        {
            _error = PayablesRules.RefusalSentence(exception, what);
            if (PayablesRules.CommittedNothing(exception.Code))
            {
                intention.Release();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _error = $"The answer about {what} did not reach the screen, so its key and its figures are held. Press again to ask the server about the same document; a second {intention.Name} will not be invented.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AbandonAsync<TBody>(HeldIntention<TBody> intention, string what)
    {
        intention.Release();
        _error = null;
        _notice = $"The unanswered {what} is no longer held by this screen. The account has been read back - if the server recorded it, it is listed; if it did not, nothing moved.";
        await LoadAsync().ConfigureAwait(false);
    }

    private void ClearIntentions()
    {
        _match.Release();
        _issue.Release();
        _settleIntention.Release();
        _credit.Release();
        _apply.Release();
        _settle.Clear();
        _draftLines.Clear();
        _appliedCredit = null;
        _applyTarget = null;
        _applyAmount = 0m;
        _creditBill = null;
        _creditQuantity = 0m;
        _order = null;
        _receipts = [];
    }

    private bool Outstanding => _match.Outstanding || _settleIntention.Outstanding
        || _credit.Outstanding || _apply.Outstanding || _issue.Outstanding;

    private string BranchText(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.Name ?? "the branch on the document";

    private string? BranchZone(Guid locationId)
        => _locations.FirstOrDefault(candidate => candidate.Id == locationId)?.TimeZone;

    private Guid LocationOf(ReferenceItem order)
        => _orders.FirstOrDefault(candidate => candidate.Id == order.Id)?.LocationId ?? Guid.Empty;

    private void ToggleMatching()
    {
        _matching = !_matching;
        _error = null;
    }

    private async Task AsOfChangedAsync(DateTime? date)
    {
        _asOf = date;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task CreditBillChangedAsync(ReferenceItem? bill)
    {
        _creditBill = bill;
        _creditQuantity = 0m;
        _returnSource = null;
        _error = null;

        if (CreditedBill?.PurchaseOrderId is { } order)
        {
            try
            {
                _receipts = await Purchasing.ListReceiptsAsync(order).ConfigureAwait(false);
                _returns = [.. _receipts.Where(receipt => PurchasingRules.IsReturn(receipt.Kind))];
            }
            catch (ApiException exception)
            {
                _error = PayablesRules.RefusalSentence(exception, "the deliveries behind this bill");
            }
        }
        else
        {
            _returns = [];
        }
    }

    private void UseCredit(SupplierCreditDto credit)
    {
        _appliedCredit = credit;
        _applyAmount = credit.RemainingAmount;
        _applyTarget = _statement?.OpenItems.FirstOrDefault(item => item.TargetKind == "bill" && item.Id != credit.SupplierBillId)
            ?? _statement?.OpenItems.FirstOrDefault();
        _error = null;
    }

    public async ValueTask DisposeAsync()
    {
        _match.Release();
        _settleIntention.Release();
        _credit.Release();
        _apply.Release();
        await Task.CompletedTask;
    }

    /// <summary>One line of a draft supplier bill, entered against a delivered quantity that is still unbilled.</summary>
    private sealed class BillLineEntry(
        Guid receiptLineId,
        string productName,
        string receiptNumber,
        decimal ceiling,
        decimal quantity,
        decimal unitCost,
        decimal discountAmount,
        decimal taxRate,
        string taxMode)
    {
        public Guid ReceiptLineId { get; } = receiptLineId;

        public string ProductName { get; } = productName;

        public string ReceiptNumber { get; } = receiptNumber;

        /// <summary>What this delivered line has left unbilled, which is the most this line may charge for.</summary>
        public decimal Ceiling { get; } = ceiling;

        public decimal Quantity { get; set; } = quantity;

        public decimal UnitCost { get; set; } = unitCost;

        public decimal DiscountAmount { get; set; } = discountAmount;

        /// <summary>Tax as a percentage (15 = 15 %), converted once at the wire boundary.</summary>
        public decimal TaxRate { get; set; } = taxRate;

        public string TaxMode { get; set; } = taxMode;
    }
}
