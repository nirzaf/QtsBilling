using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Expenses;

/// <summary>
/// Typed access to the expense and category contract (specification task 28). The routes already exist
/// server-side; this is the screen-facing shape of them. Posting and reversal carry a client-generated key
/// because each one is a document with a journal behind it, so a lost answer is re-sent under the same key
/// rather than inventing a second expense.
/// </summary>
public interface IExpenseApiClient
{
    Task<IReadOnlyList<ExpenseCategoryDto>> ListCategoriesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryRequest request, CancellationToken cancellationToken = default);
    Task<ExpenseCategoryDto> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<ExpenseDto>> ListExpensesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? categoryId = null,
        Guid? supplierId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    Task<ExpenseDto> GetExpenseAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ExpenseDto> CreateExpenseAsync(SaveExpenseRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>One reasoned, keyed reversal per posted expense; the original entry and its mirror both stay in the ledger.</summary>
    Task<ExpenseDto> ReverseExpenseAsync(Guid id, ExpenseActionRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation of <see cref="IExpenseApiClient"/>. Carries no persistence or caching concern.</summary>
public sealed class ExpenseApiClient(HttpClient http) : IExpenseApiClient
{
    private const string CategoryRoute = "/api/v1/expense-categories";
    private const string ExpenseRoute = "/api/v1/expenses";

    public async Task<IReadOnlyList<ExpenseCategoryDto>> ListCategoriesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var query = includeInactive ? "?includeInactive=true" : string.Empty;
        using var response = await http.GetAsync($"{CategoryRoute}{query}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ExpenseCategoryDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ExpenseCategoryDto>(HttpMethod.Post, CategoryRoute, request, null, cancellationToken);

    public Task<ExpenseCategoryDto> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ExpenseCategoryDto>(HttpMethod.Put, $"{CategoryRoute}/{id:D}", request, null, cancellationToken);

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"{CategoryRoute}/{id:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<ExpenseDto>> ListExpensesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? categoryId = null,
        Guid? supplierId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var query = new List<string> { $"pageSize={size}" };
        if (from is { } start) query.Add($"from={start:yyyy-MM-dd}");
        if (to is { } end) query.Add($"to={end:yyyy-MM-dd}");
        if (categoryId is { } category) query.Add($"categoryId={category:D}");
        if (supplierId is { } supplier) query.Add($"supplierId={supplier:D}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        using var response = await http.GetAsync($"{ExpenseRoute}?{string.Join('&', query)}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PagedResult<ExpenseDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? PagedResult<ExpenseDto>.Empty(size);
    }

    public Task<ExpenseDto> GetExpenseAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ExpenseDto>(HttpMethod.Get, $"{ExpenseRoute}/{id:D}", null, null, cancellationToken);

    public Task<ExpenseDto> CreateExpenseAsync(SaveExpenseRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(idempotencyKey);
        return SendAsync<ExpenseDto>(HttpMethod.Post, ExpenseRoute, request, idempotencyKey, cancellationToken);
    }

    public Task<ExpenseDto> ReverseExpenseAsync(Guid id, ExpenseActionRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(idempotencyKey);
        return SendAsync<ExpenseDto>(HttpMethod.Post, $"{ExpenseRoute}/{id:D}/reverse", request, idempotencyKey, cancellationToken);
    }

    private static void ValidateKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
            throw new ArgumentException("An idempotency key of 1-64 characters is required.", nameof(idempotencyKey));
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object? body, string? idempotencyKey, CancellationToken cancellationToken)
        where T : class
    {
        using var message = new HttpRequestMessage(method, uri);
        if (body is not null) message.Content = JsonContent.Create(body, options: ContractJson.Options);
        if (idempotencyKey is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
