using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Sales;

namespace QtsBilling.Ui.Pages.Invoices;

/// <summary>
/// The settlement workbench of one issued document (specification task 24). It shows the server's own figures,
/// offers only the actions this document's state and this session's authority can still take, and holds one
/// stable key per money intention so a lost answer is resolved by re-sending exactly what was sent - never by
/// trying a second posting that might land twice (T24.03).
/// </summary>
public partial class InvoiceActions
{
    [Parameter, EditorRequired] public InvoiceDetailDto Detail { get; set; } = default!;
    [Parameter, EditorRequired] public InvoiceAccess Access { get; set; } = default!;

    /// <summary>Asks the owning screen to read the document back after an action or a refusal.</summary>
    [Parameter] public Func<Task>? Reload { get; set; }

    [Inject] private IInvoiceApiClient InvoiceApi { get; set; } = default!;
    [Inject] private ICashRegisterApiClient Registers { get; set; } = default!;
    [Inject] private IDocumentOutput Output { get; set; } = default!;

    private readonly Dictionary<Guid, decimal> _quantities = [];
    private readonly Dictionary<Guid, bool> _restock = [];

    /// <summary>One money intention whose key and body are held until the server has answered for it.</summary>
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

    private readonly Held _payment = new();
    private readonly Held _credit = new();

    private string? _error;
    private string? _notice;
    private bool _busy;
    private bool _printing;
    private string? _printedTitle;
    private string _paymentMethod = "cash";
    private decimal _paymentAmount;
    private string? _paymentReference;
    private Guid? _registerId;
    private bool _confirmCash;
    private bool _creditWhole;
    private string? _creditReason;
    private string? _creditNote;
    private string? _voidReason;
    private InvoicePaymentDto? _bouncing;
    private string? _bounceReason;
    private IReadOnlyList<CashRegisterDto> _registers = [];
    private bool _registersReadFailed;

    private InvoiceSummaryDto Invoice => Detail.Invoice;
    private int? Scale => InvoiceRules.Scale(Detail);
    private IReadOnlyList<InvoiceAction> Allowed => InvoiceRules.AllowedActions(Detail, Access);

    private IReadOnlyList<InvoicePaymentDto> Awaiting
        => Detail.Payments.Where(InvoiceRules.IsAwaitingClearance).ToArray();

    private decimal Step => Scale is { } scale and > 0
        ? 1m / PowerOfTen(scale)
        : 0.01m;

    private string FormatForScale => Scale switch
    {
        null or 0 => "0.00",
        { } scale => "0." + new string('0', scale),
    };

    /// <summary>Any money intention still without an answer locks the tender and credit panels: their inputs must
    /// not drift while the server's answer for that exact body is outstanding.</summary>
    private bool Paying => _payment.Outstanding || _credit.Outstanding || _busy;
    private bool Crediting => _credit.Outstanding;

    /// <summary>The tender the operator has opened a bounce reason for, or null while none is being refused.</summary>
    private InvoicePaymentDto? Bouncing => _bouncing;

    private string DueText => Invoice.DueDate is { } due ? InvoiceRules.DateText(due) : "on issue";

    private string ReferenceLabel => InvoiceRules.RequiresReference(_paymentMethod)
        ? "Cheque or transfer reference (the server requires it)"
        : "Reference (optional for cash)";

    private string PaymentKeyHint => _payment.Outstanding
        ? "This tender is still awaiting an answer. Its key and its figures are held exactly as they were: the next press asks the server about the same tender again."
        : "One key per intention: press the button once, and if the answer is lost the same tender is asked about rather than a new one invented.";

    private string CashRegisterHint => _registersReadFailed
        ? "This role cannot list the branch's registers, so the tender would be recorded without a drawer: the cash still posts to the cash account, but nobody's till is accountable for it until it is counted in."
        : OpenRegisters.Count > 0
            ? "Only a register whose drawer session is open can take cash; the server refuses any other."
            : "No register in this branch has an open drawer session, so cash has nowhere to be counted in. Open one at the register workbench, or take the tender as a transfer.";

    private string StockDispositionHint => InvoiceRules.RestockSentence(false);

    private string ClosingSentence => Invoice.Status switch
    {
        InvoiceState.Draft => "This document is still a draft: it is edited and issued on the left, and settles nothing until it is issued.",
        InvoiceState.Void => "This document is void. Its issued snapshot, its reversal and the reason stay on file forever; nothing further can be posted against it.",
        InvoiceState.Paid when Invoice.BalanceDue <= 0m => "This document is settled in full. Nothing more can be taken against it; a correction is made by a credit note against the original lines.",
        InvoiceState.PartiallyPaid or InvoiceState.Issued when !Access.CanSettle && !Access.CanManage =>
            InvoiceAccess.Missing(InvoiceAction.Credit),
        _ => "There is nothing left to post against this document as it stands.",
    };

    private IReadOnlyList<CashRegisterDto> OpenRegisters => _registers
        .Where(register => register.IsActive && register.CurrentSession is not null)
        .OrderBy(register => register.Name, StringComparer.Ordinal)
        .ToArray();

    private decimal CreditTotal => _creditWhole
        ? RemainingOnDocument()
        : Detail.Lines.Sum(line => CreditValueFor(line));

    private IReadOnlyList<InvoiceEvent> History
    {
        get
        {
            var events = new List<InvoiceEvent>();

            if (Invoice.IssuedAtUtc is { } issued)
            {
                events.Add(new InvoiceEvent(issued, $"Issued as {Invoice.InvoiceNumber ?? "an unnumbered document"}",
                    $"{InvoiceRules.MoneyText(Invoice.TotalAmount, Invoice.Currency, Scale)} over {Detail.Lines.Count} lines"));
            }

            foreach (var payment in Detail.Payments.OrderBy(row => row.ReceivedAtUtc))
            {
                events.Add(new InvoiceEvent(payment.ReceivedAtUtc, $"{MethodText(payment.Method)} recorded",
                    $"{InvoiceRules.MoneyText(payment.Amount, Invoice.Currency, Scale)}"
                    + (string.IsNullOrWhiteSpace(payment.Reference) ? string.Empty : $" · reference {payment.Reference}")
                    + $" · {StatusText(payment.Status)}"));

                if (payment.ClearedAtUtc is { } cleared)
                {
                    events.Add(new InvoiceEvent(cleared, $"{MethodText(payment.Method)} confirmed as collected",
                        InvoiceRules.MoneyText(payment.Amount, Invoice.Currency, Scale)));
                }

                if (payment.BouncedAtUtc is { } bounced)
                {
                    events.Add(new InvoiceEvent(bounced, $"{MethodText(payment.Method)} bounced",
                        $"{InvoiceRules.MoneyText(payment.Amount, Invoice.Currency, Scale)} · {payment.BounceReason ?? "no reason recorded"}"));
                }
            }

            foreach (var note in (Detail.CreditNotes ?? []).OrderBy(row => row.IssuedAtUtc))
            {
                events.Add(new InvoiceEvent(note.IssuedAtUtc, $"Credit note {note.CreditNumber} issued",
                    $"{InvoiceRules.MoneyText(note.Amount, Invoice.Currency, Scale)} · {note.Reason}"
                    + (string.IsNullOrWhiteSpace(note.JournalEntryNumber) ? string.Empty : $" · journal {note.JournalEntryNumber}")));
            }

            if (Invoice.VoidedAtUtc is { } voided)
            {
                events.Add(new InvoiceEvent(voided, "Voided", "the original entry was reversed; nothing was deleted"));
            }

            events.Sort((left, right) => left.At.CompareTo(right.At));

            return events;
        }
    }

    protected override void OnParametersSet()
    {
        // An issued document's own line quantities are the ceiling of any credit, so the buffers start there
        // and never carry a figure from a previous document across a navigation.
        foreach (var line in Detail.Lines)
        {
            if (!_quantities.ContainsKey(line.Id))
            {
                _quantities[line.Id] = 0m;
            }

            if (!_restock.ContainsKey(line.Id))
            {
                _restock[line.Id] = false;
            }
        }

        if (_paymentAmount == 0m && Invoice.BalanceDue > 0m)
        {
            _paymentAmount = RoundToScale(Invoice.BalanceDue);
        }
    }

    private decimal QuantityFor(InvoiceLineDto line) => _quantities.TryGetValue(line.Id, out var value) ? value : 0m;

    private void SetQuantity(InvoiceLineDto line, decimal value)
    {
        var ceiling = InvoiceRules.RemainingQuantity(line, Detail);
        _quantities[line.Id] = value < 0m ? 0m : value > ceiling ? ceiling : value;
    }

    private bool RestockFor(InvoiceLineDto line) => _restock.TryGetValue(line.Id, out var value) && value;

    private void SetRestock(InvoiceLineDto line, bool value) => _restock[line.Id] = value;

    private decimal CreditValueFor(InvoiceLineDto line)
    {
        var quantity = QuantityFor(line);

        return quantity == 0m || line.Quantity == 0m
            ? 0m
            : decimal.Round(line.LineTotal / line.Quantity * quantity, Math.Max(2, InvoiceRules.FractionDigits(line.LineTotal)));
    }

    private void OnCreditWholeChanged(bool value)
    {
        _creditWhole = value;

        foreach (var line in Detail.Lines)
        {
            _quantities[line.Id] = value ? InvoiceRules.RemainingQuantity(line, Detail) : 0m;
        }
    }

    private async Task OnPaymentMethodChanged(string method)
    {
        _paymentMethod = method;

        // The drawer a cash tender belongs to is chosen by name, so the branch's registers are read when the
        // operator first reaches for cash - and never guessed at.
        if (method == "cash")
        {
            await LoadRegistersAsync();
        }
    }

    private async Task LoadRegistersAsync()
    {
        if (!Access.CanReadRegisters || _registers.Count > 0)
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

    private Task PaymentAsync() => PaymentCoreAsync();

    private Task AbandonPayment() => AbandonAsync(_payment, "tender");

    private Task CreditAsync() => CreditCoreAsync();

    private Task AbandonCredit() => AbandonAsync(_credit, "credit note");

    /// <summary>
    /// Records a tender. With a key already held the request is sent again with exactly the body it was first
    /// sent with, which is how the server's own replay returns the original payment instead of a second one
    /// (T24.03). A code that proves nothing was written releases the key; anything else keeps it.
    /// </summary>
    private async Task PaymentCoreAsync()
    {
        if (!Allowed.Contains(InvoiceAction.RecordPayment) || _busy)
        {
            return;
        }

        if (_paymentMethod == "cash" && !_confirmCash)
        {
            _error = "Cash is only recorded once you have confirmed the money is in hand and named the drawer it belongs to.";
            return;
        }

        if (InvoiceRules.RequiresReference(_paymentMethod) && string.IsNullOrWhiteSpace(_paymentReference))
        {
            _error = $"A {MethodText(_paymentMethod)} needs the processing reference the bank or the customer's cheque will be matched by.";
            return;
        }

        if (_paymentAmount <= 0m || _paymentAmount > Invoice.BalanceDue)
        {
            _error = $"A tender must be between {InvoiceRules.Money(Step, Scale)} and the {InvoiceRules.MoneyText(Invoice.BalanceDue, Invoice.Currency, Scale)} this document still shows due.";
            return;
        }

        _payment.Key ??= InvoiceRules.NewKey("payment", Invoice.InvoiceNumber);
        _payment.Outstanding = true;
        _busy = true;
        _error = null;
        _notice = null;

        var request = new InvoicePaymentRequest(
            _paymentMethod,
            RoundToScale(_paymentAmount),
            string.IsNullOrWhiteSpace(_paymentReference) ? null : _paymentReference.Trim(),
            _paymentMethod == "cash" ? _registerId : null);

        try
        {
            var answer = await InvoiceApi.AddPaymentAsync(Invoice.Id, request, _payment.Key!);

            _payment.Release();
            _confirmCash = false;
            _paymentReference = null;
            _bouncing = null;
            _notice = answer.Payment.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase)
                ? $"{MethodText(answer.Payment.Method)} of {InvoiceRules.MoneyText(answer.Payment.Amount, Invoice.Currency, Scale)} is recorded and awaits clearance; the balance due is unchanged until it is confirmed."
                : $"{MethodText(answer.Payment.Method)} of {InvoiceRules.MoneyText(answer.Payment.Amount, Invoice.Currency, Scale)} was collected and applied. The document now shows {InvoiceRules.MoneyText(answer.Invoice.BalanceDue, Invoice.Currency, Scale)} due.";
            await ReloadAsync();
        }
        catch (ApiException exception)
        {
            _error = InvoiceRules.RefusalSentence(exception);

            if (InvoiceRules.CommittedNothing(exception.Code))
            {
                // Nothing stands in the way of a corrected intention under a fresh key.
                _payment.Release();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The outcome is unknown, which is precisely when a new key must not be invented.
            _payment.Outstanding = true;
            _error = "The answer about this tender did not reach the screen, so its key and its figures are held. Press the button again to ask the server about this same tender; it will return the payment it made, or tell you it made none.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CreditCoreAsync()
    {
        if (!Allowed.Contains(InvoiceAction.Credit) || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_creditReason))
        {
            _error = "A credit note always carries the reason it exists; nothing is posted without one.";
            return;
        }

        if (!_creditWhole && Detail.Lines.All(line => QuantityFor(line) <= 0m))
        {
            _error = "Name the quantity to credit on at least one line, or credit everything that is left of the document.";
            return;
        }

        if (CreditTotal > RemainingOnDocument() + Step)
        {
            _error = $"The lines you have named come to {InvoiceRules.MoneyText(CreditTotal, Invoice.Currency, Scale)}, which is more than the {InvoiceRules.MoneyText(RemainingOnDocument(), Invoice.Currency, Scale)} this document has left to give back.";
            return;
        }

        _credit.Key ??= InvoiceRules.NewKey("credit", Invoice.InvoiceNumber);
        _credit.Outstanding = true;
        _busy = true;
        _error = null;
        _notice = null;

        var lines = _creditWhole
            ? null
            : Detail.Lines
                .Where(line => QuantityFor(line) > 0m)
                .Select(line => new InvoiceCreditLineRequest(line.Id, QuantityFor(line), RestockFor(line)))
                .ToArray();

        var request = new InvoiceCreditNoteRequest(_creditReason.Trim(), NullIfBlank(_creditNote), lines);

        try
        {
            var answer = await InvoiceApi.CreditAsync(Invoice.Id, request, _credit.Key!);

            _credit.Release();
            _creditReason = null;
            _creditNote = null;
            _creditWhole = false;
            foreach (var line in Detail.Lines)
            {
                _quantities[line.Id] = 0m;
                _restock[line.Id] = false;
            }

            var note = answer.CreditNote;
            _notice = note is null
                ? "The credit was posted; the document's figures have been read back."
                : $"Credit note {note.CreditNumber} of {InvoiceRules.MoneyText(note.Amount, Invoice.Currency, Scale)} stands against this document"
                    + (string.IsNullOrWhiteSpace(note.JournalEntryNumber) ? "." : $", journalled as {note.JournalEntryNumber}.");
            await ReloadAsync();
        }
        catch (ApiException exception)
        {
            _error = InvoiceRules.RefusalSentence(exception);

            if (InvoiceRules.CommittedNothing(exception.Code))
            {
                _credit.Release();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _credit.Outstanding = true;
            _error = "The answer about this credit note did not reach the screen, so its key and its lines are held exactly as they were. Press again to ask about the same note; a second note will not be invented.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task VoidAsync()
    {
        if (!Allowed.Contains(InvoiceAction.Void) || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_voidReason))
        {
            _error = "Voiding a document always names the reason; it is audited with who voided it.";
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            await InvoiceApi.VoidAsync(Invoice.Id, new InvoiceVoidRequest(_voidReason.Trim(), null));
            _voidReason = null;
            _notice = "The document is void: its original entry is reversed by a journal of its own, and nothing was deleted.";
            await ReloadAsync();
        }
        catch (ApiException exception)
        {
            _error = InvoiceRules.RefusalSentence(exception);
            await ReloadAfterConflictAsync(exception);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ClearAsync(InvoicePaymentDto payment)
    {
        if (!Access.CanPost || _busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            var answer = await InvoiceApi.ClearPaymentAsync(payment.Id, InvoiceRules.NewKey("clear", Invoice.InvoiceNumber));
            _notice = $"{MethodText(payment.Method)} of {InvoiceRules.MoneyText(payment.Amount, Invoice.Currency, Scale)} is recorded as {StatusText(answer.Status)}: the balance due has been read back from the ledger.";
            await ReloadAsync();
        }
        catch (ApiException exception)
        {
            _error = InvoiceRules.RefusalSentence(exception);
            await ReloadAfterConflictAsync(exception);
        }
        finally
        {
            _busy = false;
        }
    }

    private void StartBounce(InvoicePaymentDto payment)
    {
        _bouncing = payment;
        _bounceReason = null;
        _error = null;
    }

    private void CancelBounce()
    {
        _bouncing = null;
        _bounceReason = null;
    }

    private async Task BounceAsync(InvoicePaymentDto payment)
    {
        if (!Access.CanPost || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_bounceReason))
        {
            _error = "A bounced tender is recorded with the reason the bank or the drawer gave; without one, the refusal has no trace.";
            return;
        }

        _busy = true;
        _error = null;
        _notice = null;

        try
        {
            await InvoiceApi.BouncePaymentAsync(payment.Id, new PaymentBounceRequest(_bounceReason.Trim()), InvoiceRules.NewKey("bounce", Invoice.InvoiceNumber));
            _notice = $"{MethodText(payment.Method)} of {InvoiceRules.MoneyText(payment.Amount, Invoice.Currency, Scale)} is recorded as bounced. It was never collected money, and the balance due still counts it as owing.";
            _bouncing = null;
            _bounceReason = null;
            await ReloadAsync();
        }
        catch (ApiException exception)
        {
            _error = InvoiceRules.RefusalSentence(exception);
            await ReloadAfterConflictAsync(exception);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Prints the committed snapshot (task 24, recipe step 3). The document is fetched through this host's
    /// authenticated client and only its content is handed on: nothing navigates, and a refusal - a document
    /// that is not this business's, an expired session, a body that is not the server's own page - means there
    /// is no document at all, so no paper is written and nothing is displayed (T24.02, T24.04).
    /// </summary>
    private async Task PrintAsync()
    {
        if (!Allowed.Contains(InvoiceAction.Print) || _printing)
        {
            return;
        }

        _printing = true;
        _error = null;
        _notice = null;

        try
        {
            var document = await InvoiceApi.GetPrintDocumentAsync(
                Invoice.Id,
                $"Invoice {Invoice.InvoiceNumber ?? "(unnumbered)"} - {Invoice.CustomerName ?? "no customer recorded"}");

            if (!await Output.PresentAsync(document))
            {
                _error = "This host could not open a print dialogue for the document. Nothing was printed, written to disk or shown from another source; the figures on this screen remain the way to read the document until a printer is reachable.";
            }
            else
            {
                _printedTitle = document.Title;
                _notice = "The stored snapshot was handed to this host's own print dialogue. Nothing was uploaded and no copy was written by the application.";
            }
        }
        catch (ApiException exception)
        {
            // T24.04: no content, therefore nothing to print and nothing to display.
            _error = exception.IsUnauthenticated
                ? "Your session no longer answers, so the document was not fetched and nothing was printed. Sign in again and read the document back."
                : InvoiceRules.RefusalSentence(exception);
        }
        catch (ArgumentException exception)
        {
            _error = $"The server did not return a printable document: {exception.Message}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _error = "The document could not be fetched, so nothing was printed. A print attempt never falls back to an address or a stored token.";
        }
        finally
        {
            _printing = false;
        }
    }

    private async Task AbandonAsync(Held held, string what)
    {
        held.Release();
        _error = null;
        _notice = $"The unanswered {what} is no longer held by this screen. Its figures have been read back - if the server did post it, that fact stands and is visible here; if it did not, nothing was written.";
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (Reload is not null)
        {
            await Reload();
        }
    }

    /// <summary>A version conflict is answered by reading the document back, never by guessing a new version.</summary>
    private async Task ReloadAfterConflictAsync(ApiException exception)
    {
        if (exception.Code is ApiErrorCodes.OptimisticConcurrencyFailure or ApiErrorCodes.SameKeyDifferentPayload)
        {
            await ReloadAsync();
        }
    }

    private decimal RemainingOnDocument()
        => decimal.Round(Invoice.TotalAmount - Invoice.PaidAmount - Invoice.CreditedAmount, Math.Max(2, Scale ?? 2));

    private decimal RoundToScale(decimal value)
        => Scale is { } scale ? decimal.Round(value, scale, MidpointRounding.AwayFromZero) : decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal PowerOfTen(int power)
    {
        var result = 1m;

        for (var i = 0; i < power; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string MethodText(string method) => method.ToLowerInvariant() switch
    {
        "cash" => "Cash",
        "cheque" => "Cheque",
        "banktransfer" => "Bank transfer",
        "card" => "Card",
        "customercredit" => "Customer credit",
        _ => method,
    };

    private static string StatusText(string status) => status.ToLowerInvariant() switch
    {
        "pending" => "awaiting clearance",
        "confirmed" => "collected",
        "bounced" => "bounced",
        "refunded" => "returned to the customer",
        _ => status,
    };

    private static string StatusLabel(InvoiceState status) => status switch
    {
        InvoiceState.Draft => "draft",
        InvoiceState.PartiallyPaid => "partially paid",
        InvoiceState.Void => "void",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string SourceText(InvoiceSourceDto source) => source.Kind switch
    {
        "sale" => $"sale {(string.IsNullOrWhiteSpace(source.Number) ? "(no number on the original)" : source.Number)}",
        "order" => $"order {(string.IsNullOrWhiteSpace(source.Number) ? "(no number on the original)" : source.Number)}",
        _ => source.Kind,
    };

    private static string? SourceHref(InvoiceSourceDto source) => source.Kind switch
    {
        "sale" => $"sales/{source.Id:D}",
        "order" => $"orders/sales-orders/{source.Id:D}",
        _ => null,
    };
}
