using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Banking;
using QtsBilling.Persistence.Postgres;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>Bank-account registration, statement import, and exact posted-line reconciliation.</summary>
public sealed class BankingEndpointsTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public BankingEndpointsTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Statement_import_is_idempotent_and_only_exact_posted_bank_lines_can_be_matched()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var bankLedgerId = await LedgerAccountIdAsync("1010");
        var revenueLedgerId = await LedgerAccountIdAsync("4000");
        var accountRequest = new CreateBankAccountRequest(
            "Operating account",
            "****1234",
            "QA5888888888888888888888",
            "QAR",
            bankLedgerId);

        var createAccount = await client.PostAsJsonAsync("/api/v1/banking/accounts", accountRequest);
        var createAccountBody = await createAccount.Content.ReadAsStringAsync();
        Assert.True(createAccount.StatusCode == HttpStatusCode.Created,
            $"Bank account registration returned {(int)createAccount.StatusCode}: {createAccountBody}");
        var bankAccount = JsonSerializer.Deserialize<BankAccountDto>(createAccountBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal("••••8888", bankAccount.IbanMasked);
        Assert.DoesNotContain(accountRequest.Iban!, createAccountBody, StringComparison.Ordinal);

        var valueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = $"Date,Description,Reference,Amount\n{valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},\"Card settlement, daily\",SETTLE-1,125.0000\n";
        var import = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-1");
        var importBody = await import.Content.ReadAsStringAsync();
        Assert.True(import.StatusCode == HttpStatusCode.Created,
            $"Statement import returned {(int)import.StatusCode}: {importBody}");
        var detail = JsonSerializer.Deserialize<BankStatementDetailDto>(importBody, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
        Assert.Equal(1, detail.Import.LineCount);
        Assert.Equal("Card settlement, daily", detail.Lines.Single().Description);
        Assert.Equal("SETTLE-1", detail.Lines.Single().Reference);
        Assert.Null(detail.Lines.Single().MatchedJournalLineId);

        var replay = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayedDetail = await replay.Content.ReadFromJsonAsync<BankStatementDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(detail.Import.Id, replayedDetail!.Import.Id);

        var alteredReplay = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_001m, 1_125m, "bank-import-1");
        Assert.Equal(HttpStatusCode.BadRequest, alteredReplay.StatusCode);
        Assert.Contains("bank-statement balances do not reconcile", await alteredReplay.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var duplicateFile = await ImportAsync(client, bankAccount.Id, csv, valueDate, 1_000m, 1_125m, "bank-import-2");
        Assert.Equal(HttpStatusCode.Conflict, duplicateFile.StatusCode);

        var journal = await client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = valueDate,
            memo = "bank reconciliation test",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = bankLedgerId, debit = 125m, credit = 0m, description = "bank receipt", locationId = (Guid?)null },
                new { accountId = revenueLedgerId, debit = 0m, credit = 125m, description = "counter entry", locationId = (Guid?)null },
            },
        });
        var journalBody = await journal.Content.ReadAsStringAsync();
        Assert.True(journal.StatusCode == HttpStatusCode.Created,
            $"Journal posting returned {(int)journal.StatusCode}: {journalBody}");
        using var journalJson = JsonDocument.Parse(journalBody);
        var journalId = journalJson.RootElement.GetProperty("id").GetGuid();
        var journalLineIds = await _fixture.WithFreshContextAsync(async db => await db.JournalLines.AsNoTracking()
            .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.JournalEntryId == journalId)
            .Select(line => new { line.Id, line.AccountId })
            .ToListAsync());

        var wrongAccountLine = journalLineIds.Single(line => line.AccountId == revenueLedgerId);
        var wrongMatch = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(wrongAccountLine.Id));
        Assert.Equal(HttpStatusCode.Conflict, wrongMatch.StatusCode);

        var bankLine = journalLineIds.Single(line => line.AccountId == bankLedgerId);
        var match = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(bankLine.Id));
        var matched = await match.Content.ReadFromJsonAsync<BankStatementLineDto>(QtsBilling.Contracts.Serialization.ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Equal(bankLine.Id, matched!.MatchedJournalLineId);
        Assert.NotNull(matched.ReconciledAtUtc);

        var matchReplay = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(bankLine.Id));
        Assert.Equal(HttpStatusCode.OK, matchReplay.StatusCode);
        var differentMatch = await client.PostAsJsonAsync($"/api/v1/banking/statement-lines/{detail.Lines[0].Id:D}/match",
            new MatchStatementLineRequest(wrongAccountLine.Id));
        Assert.Equal(HttpStatusCode.Conflict, differentMatch.StatusCode);

        var otherBusinessClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var crossTenantRead = await otherBusinessClient.GetAsync($"/api/v1/banking/statements/{detail.Import.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantRead.StatusCode);

        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_lines SET description = 'altered' WHERE id = {detail.Lines[0].Id}")));
        await Assert.ThrowsAsync<PostgresException>(() => _fixture.WithFreshContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bank_statement_imports SET opening_balance = 0 WHERE id = {detail.Import.Id}")));
    }

    private async Task<Guid> LedgerAccountIdAsync(string code)
        => await _fixture.WithFreshContextAsync(async db => await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == code)
            .Select(account => account.Id)
            .SingleAsync());

    private static async Task<HttpResponseMessage> ImportAsync(
        HttpClient client,
        Guid bankAccountId,
        string csv,
        DateOnly statementDate,
        decimal openingBalance,
        decimal closingBalance,
        string idempotencyKey)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(statementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "statementDate");
        form.Add(new StringContent(openingBalance.ToString(CultureInfo.InvariantCulture)), "openingBalance");
        form.Add(new StringContent(closingBalance.ToString(CultureInfo.InvariantCulture)), "closingBalance");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "statement.csv");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/banking/accounts/{bankAccountId:D}/statements")
        {
            Content = form,
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }
}
