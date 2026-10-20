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

    Task<BankStatementLineDto> MatchLineAsync(Guid lineId, MatchStatementLineRequest request, CancellationToken cancellationToken = default);
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

    public async Task<BankStatementLineDto> MatchLineAsync(Guid lineId, MatchStatementLineRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/banking/statement-lines/{lineId:D}/match", request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BankStatementLineDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The statement-line response was empty.");
    }
}
