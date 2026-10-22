using System.Globalization;
using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Banking;

public interface IBankingApiClient
{
    Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task<BankAccountDto> CreateAccountAsync(CreateBankAccountRequest request, CancellationToken cancellationToken = default);

    Task<BankStatementDetailDto> ImportStatementAsync(
        Guid bankAccountId,
        string fileName,
        DateOnly statementDate,
        decimal openingBalance,
        decimal closingBalance,
        ReadOnlyMemory<byte> csv,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<BankStatementDetailDto> GetStatementAsync(Guid importId, CancellationToken cancellationToken = default);

    /// <summary>The statements already imported for one bank account, newest first, named by file and date.</summary>
    Task<IReadOnlyList<BankStatementImportDto>> ListStatementsAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<BankStatementLineDto> MatchLineAsync(Guid lineId, MatchStatementLineRequest request, CancellationToken cancellationToken = default);

    /// <summary>The reviewer's position on one statement: arithmetic, booked-bank control, candidates and history.</summary>
    Task<BankStatementReviewDto> GetReviewAsync(Guid importId, CancellationToken cancellationToken = default);

    /// <summary>Releases a mistaken match with a reason, keeping the evidence that it was ever matched.</summary>
    Task<BankStatementLineDto> UnmatchLineAsync(
        Guid lineId, UnmatchStatementLineRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Posts a reviewed fee or interest line and matches the statement line to the bank leg it produced.</summary>
    Task<BankStatementDetailDto> RecordMovementAsync(
        Guid lineId, RecordStatementMovementRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Closes the review of a statement: every line matched, the figures tied, and the reason recorded.</summary>
    Task<BankStatementDetailDto> FinaliseStatementAsync(
        Guid importId, FinaliseStatementRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Reopens a finalised statement for correction. It does not reopen an accounting period.</summary>
    Task<BankStatementDetailDto> ReopenStatementAsync(
        Guid importId, ReopenStatementRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed class BankingApiClient(HttpClient http) : IBankingApiClient
{
    public async Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("/api/v1/banking/accounts", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<BankAccountDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<BankAccountDto> CreateAccountAsync(CreateBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/banking/accounts", request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankAccountDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bank-account response was empty.");
    }

    public async Task<BankStatementDetailDto> ImportStatementAsync(
        Guid bankAccountId,
        string fileName,
        DateOnly statementDate,
        decimal openingBalance,
        decimal closingBalance,
        ReadOnlyMemory<byte> csv,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(statementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "statementDate");
        content.Add(new StringContent(openingBalance.ToString(CultureInfo.InvariantCulture)), "openingBalance");
        content.Add(new StringContent(closingBalance.ToString(CultureInfo.InvariantCulture)), "closingBalance");
        var file = new ByteArrayContent(csv.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", Path.GetFileName(fileName));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/banking/accounts/{bankAccountId:D}/statements") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankStatementDetailDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bank-statement response was empty.");
    }

    public async Task<BankStatementDetailDto> GetStatementAsync(Guid importId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"/api/v1/banking/statements/{importId:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankStatementDetailDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bank-statement response was empty.");
    }

    public async Task<IReadOnlyList<BankStatementImportDto>> ListStatementsAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"/api/v1/banking/accounts/{bankAccountId:D}/statements", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<BankStatementImportDto>>(ContractJson.Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<BankStatementReviewDto> GetReviewAsync(Guid importId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"/api/v1/banking/statements/{importId:D}/review", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankStatementReviewDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bank-review response was empty.");
    }

    public async Task<BankStatementLineDto> MatchLineAsync(Guid lineId, MatchStatementLineRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/banking/statement-lines/{lineId:D}/match", request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankStatementLineDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The statement-line response was empty.");
    }

    public async Task<BankStatementLineDto> UnmatchLineAsync(
        Guid lineId, UnmatchStatementLineRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => await PostWithKeyAsync<UnmatchStatementLineRequest, BankStatementLineDto>(
            $"/api/v1/banking/statement-lines/{lineId:D}/unmatch", request, idempotencyKey, cancellationToken).ConfigureAwait(false);

    public async Task<BankStatementDetailDto> RecordMovementAsync(
        Guid lineId, RecordStatementMovementRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => await PostWithKeyAsync<RecordStatementMovementRequest, BankStatementDetailDto>(
            $"/api/v1/banking/statement-lines/{lineId:D}/movement", request, idempotencyKey, cancellationToken).ConfigureAwait(false);

    public async Task<BankStatementDetailDto> FinaliseStatementAsync(
        Guid importId, FinaliseStatementRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => await PostWithKeyAsync<FinaliseStatementRequest, BankStatementDetailDto>(
            $"/api/v1/banking/statements/{importId:D}/finalise", request, idempotencyKey, cancellationToken).ConfigureAwait(false);

    public async Task<BankStatementDetailDto> ReopenStatementAsync(
        Guid importId, ReopenStatementRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
        => await PostWithKeyAsync<ReopenStatementRequest, BankStatementDetailDto>(
            $"/api/v1/banking/statements/{importId:D}/reopen", request, idempotencyKey, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A reconciliation command carries one stable key: the same body replayed answers the same document, and an
    /// unknown outcome can be re-sent without inventing a second correction or a second journal (specification F04).
    /// </summary>
    private async Task<TResponse> PostWithKeyAsync<TRequest, TResponse>(
        string path, TRequest body, string idempotencyKey, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }
}
