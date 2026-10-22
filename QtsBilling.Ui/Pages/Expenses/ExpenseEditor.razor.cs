using Microsoft.AspNetCore.Components;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Expenses;

/// <summary>
/// Record a new expense, or read one that already posted and reverse it by a reasoned mirror entry
/// (specification task 28). Posting and reversal each hold one stable key until the server answers, so a lost
/// answer is resolved by re-sending the same intention rather than inventing a second expense.
/// </summary>
public partial class ExpenseEditor : IAsyncDisposable
{
    [Inject] private IExpenseApiClient ExpensesApi { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Parameter] public Guid Id { get; set; }
    [SupplyParameterFromQuery] public string? Reverse { get; set; }

    private ExpenseDto? _expense;
    private IReadOnlyList<ExpenseCategoryDto> _categories = [];

    private DateTime? _occurredOn;
    private Guid? _categoryId;
    private string _description = string.Empty;
    private string? _reference;
    private decimal _amount;
    private decimal _taxPercent;
    private decimal _taxAmount;
    private string _paidVia = "cash";
    private string _taxMode = "Exclusive";

    private bool _reversing;
    private string _reverseReason = string.Empty;
    private string? _reverseNote;

    private string? _error;
    private string? _notice;
    private bool _errorRetry;
    private bool _busy;
    private bool _canManage = true;
    private bool _initialized;
    private ShellStateKind _state = ShellStateKind.Loading;
    private string? _message;

    private string? _createKey;
    private string? _reverseKey;

    private bool IsNew => Id == Guid.Empty;

    private (decimal Net, decimal Tax, decimal Paid) _preview => ExpenseRules.Preview(_amount, _taxPercent, _taxMode);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _categories = await ExpensesApi.ListCategoriesAsync().ConfigureAwait(false);
        }
        catch
        {
            _categories = [];
        }

        if (IsNew)
        {
            _occurredOn = InvoiceRules.BusinessClock().ToDateTime(TimeOnly.MinValue);
            _state = ShellStateKind.Content;
        }
        else
        {
            await ReloadAsync().ConfigureAwait(false);
            if (string.Equals(Reverse, "1", StringComparison.Ordinal))
            {
                _reversing = true;
            }
        }

        StateHasChanged();
    }

    private async Task ReloadAsync()
    {
        _error = null;
        _notice = null;
        _state = ShellStateKind.Loading;
        try
        {
            _expense = await ExpensesApi.GetExpenseAsync(Id).ConfigureAwait(false);
            _state = ShellStateKind.Content;
        }
        catch (ApiException exception)
        {
            _expense = null;
            _state = exception.StatusCode switch
            {
                401 => ShellStateKind.Unauthenticated,
                403 => ShellStateKind.Forbidden,
                404 => ShellStateKind.Empty,
                _ => ShellStateKind.Failure,
            };
            _message = exception.StatusCode == 404
                ? "That expense is not in this business."
                : exception.Message;
        }
        catch (Exception exception)
        {
            _expense = null;
            _state = ShellStateKind.Failure;
            _message = exception.Message;
        }
    }

    private Task CategoryIdChanged(Guid? id)
    {
        _categoryId = id;
        return Task.CompletedTask;
    }

    private Task PaidViaChanged(string method)
    {
        _paidVia = method;
        return Task.CompletedTask;
    }

    private Task TaxModeChanged(string mode)
    {
        _taxMode = mode;
        if (mode == "Exempt")
        {
            _taxPercent = 0m;
            _taxAmount = 0m;
        }

        return Task.CompletedTask;
    }

    private async Task SubmitAsync()
    {
        _error = null;
        _notice = null;
        _errorRetry = false;

        if (_occurredOn is not { } occurred)
        {
            _error = "A date is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_description))
        {
            _error = "A description is required.";
            return;
        }

        if (_amount <= 0m)
        {
            _error = "A positive amount is required.";
            return;
        }

        _busy = true;
        try
        {
            _createKey ??= ExpenseRules.NewKey("expense", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            var request = new SaveExpenseRequest(
                _categoryId,
                LocationId: null,
                SupplierId: null,
                _description.Trim(),
                _amount,
                DateOnly.FromDateTime(occurred),
                _paidVia,
                string.IsNullOrWhiteSpace(_reference) ? null : _reference.Trim(),
                _taxPercent / 100m,
                _taxAmount,
                _taxMode);

            var created = await ExpensesApi.CreateExpenseAsync(request, _createKey).ConfigureAwait(false);
            _expense = created;
            _createKey = null;
            _notice = $"Posted as {created.JournalEntryNumber ?? "one balanced entry"}. The original stays on the record.";
            _state = ShellStateKind.Content;
            Navigation.NavigateTo($"expenses/{created.Id:D}");
        }
        catch (ApiException exception)
        {
            _error = ExpenseRules.RefusalSentence(exception, "posting");
            _errorRetry = ExpenseRules.CommittedNothing(exception.Code);
            if (!_errorRetry && exception.StatusCode >= 500)
            {
                // An internal failure may still have committed: keep the key so the same intention is re-sent.
                _errorRetry = true;
            }
        }
        catch (Exception exception)
        {
            _error = $"The expense could not be posted: {exception.Message}";
            _errorRetry = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void BeginReverse() => _reversing = true;

    private void CancelReverse()
    {
        _reversing = false;
        _reverseReason = string.Empty;
        _reverseNote = null;
    }

    private async Task ReverseAsync()
    {
        _error = null;
        _notice = null;
        _errorRetry = false;

        if (string.IsNullOrWhiteSpace(_reverseReason))
        {
            _error = "A reason is required for every reversal - it is audited with the actor and time.";
            return;
        }

        _busy = true;
        try
        {
            _reverseKey ??= ExpenseRules.NewKey("reverse", Id.ToString("D")[..8]);
            var request = new ExpenseActionRequest(_reverseReason.Trim(), string.IsNullOrWhiteSpace(_reverseNote) ? null : _reverseNote.Trim());
            var reversed = await ExpensesApi.ReverseExpenseAsync(Id, request, _reverseKey).ConfigureAwait(false);
            _expense = reversed;
            _reverseKey = null;
            _reversing = false;
            _notice = $"Reversed by mirror entry {reversed.ReversalEntryNumber}. The original expense and its journal both stay on the record.";
        }
        catch (ApiException exception)
        {
            _error = ExpenseRules.RefusalSentence(exception, "the reversal");
            _errorRetry = ExpenseRules.CommittedNothing(exception.Code) || exception.StatusCode >= 500;
        }
        catch (Exception exception)
        {
            _error = $"The reversal could not be posted: {exception.Message}";
            _errorRetry = true;
        }
        finally
        {
            _busy = false;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
