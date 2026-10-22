using Microsoft.AspNetCore.Components;
using MudBlazor;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Contracts.Identity;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Invoices;

namespace QtsBilling.Ui.Pages.Expenses;

/// <summary>
/// The expense list (specification task 28). Search and paging run on the server, so a filed expense is one
/// keystroke away rather than a page number to guess. Reversal is offered only when the server would answer
/// it: a posted journal and no existing reversal.
/// </summary>
public partial class Expenses : IAsyncDisposable
{
    [Inject] private IExpenseApiClient ExpensesApi { get; set; } = default!;
    [Inject] private IAccountApiClient Accounts { get; set; } = default!;

    private const int PageSize = 25;

    private readonly PagedListState<ExpenseDto> _list;
    private IReadOnlyList<ExpenseCategoryDto> _categories = [];

    private bool _initialized;
    private bool _canManage;
    private string? _searchText;
    private Guid? _categoryId;
    private DateTime? _from;
    private DateTime? _to;
    private string? _problem;

    public Expenses()
    {
        _list = new PagedListState<ExpenseDto>(LoadPageAsync, StateHasChanged, PageSize);
    }

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

        var session = await Accounts.GetSessionAsync().ConfigureAwait(false);

        if (session is not null)
        {
            _canManage = session.Permissions.Contains(Permissions.ExpensesManage, StringComparer.Ordinal);
        }

        await _list.ReloadAsync().ConfigureAwait(false);
        StateHasChanged();
    }

    private async Task<PagedRows<ExpenseDto>> LoadPageAsync(string? search, string? cursor, int pageSize, CancellationToken token)
    {
        var from = _from is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = _to is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
        var page = await ExpensesApi.ListExpensesAsync(
            from, to, _categoryId, supplierId: null, search, cursor, pageSize, token).ConfigureAwait(false);

        return new PagedRows<ExpenseDto>(page.Items, page.NextCursor);
    }

    private Task SearchChanged(string? term)
    {
        _searchText = term;
        _problem = null;
        return _list.SearchAsync(term);
    }

    private Task CategoryChanged(Guid? id)
    {
        _categoryId = id;
        _problem = null;
        return _list.FirstPageAsync();
    }

    private Task ReloadAsync()
    {
        _problem = null;
        return _list.ReloadAsync();
    }

    public async ValueTask DisposeAsync() => await _list.DisposeAsync().ConfigureAwait(false);
}
