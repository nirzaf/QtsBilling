using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class FiscalYearCloseTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public FiscalYearCloseTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fiscal_year_close_posts_retained_earnings_transfer_and_locks_periods_idempotently()
    {
        const int fiscalYear = 2025;
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var revenueAndReceivable = await _fixture.WithFreshContextAsync(async db =>
        {
            var accounts = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId
                    && (account.Code == "1100" || account.Code == "4000"))
                .ToDictionaryAsync(account => account.Code);
            return (Receivable: accounts["1100"].Id, Revenue: accounts["4000"].Id);
        });

        var journal = await client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = new DateOnly(fiscalYear, 6, 30),
            memo = "fixture revenue before year-end",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = revenueAndReceivable.Receivable, debit = 100m, credit = 0m, description = "Receivable", locationId = (Guid?)null },
                new { accountId = revenueAndReceivable.Revenue, debit = 0m, credit = 100m, description = "Revenue", locationId = (Guid?)null },
            },
        });
        var journalBody = await journal.Content.ReadAsStringAsync();
        Assert.True(journal.StatusCode == HttpStatusCode.Created, journalBody);

        var closePeriod = await client.PostAsync($"/api/v1/accounting-periods/{_fixture.BusinessA.AccountingPeriodId}/close", content: null);
        Assert.True(closePeriod.IsSuccessStatusCode, await closePeriod.Content.ReadAsStringAsync());

        var idempotencyKey = "year-close-" + Guid.NewGuid().ToString("N");
        var firstClose = await PostYearCloseAsync(client, fiscalYear, idempotencyKey);
        var firstBody = await firstClose.Content.ReadAsStringAsync();
        Assert.True(firstClose.StatusCode == HttpStatusCode.OK, firstBody);
        using var first = System.Text.Json.JsonDocument.Parse(firstBody);
        Assert.Equal(fiscalYear, first.RootElement.GetProperty("fiscalYear").GetInt32());
        Assert.Equal(100m, first.RootElement.GetProperty("netIncome").GetDecimal());

        var replay = await PostYearCloseAsync(client, fiscalYear, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var period = await db.AccountingPeriods.AsNoTracking().SingleAsync(item => item.Id == _fixture.BusinessA.AccountingPeriodId);
            var closeEntry = await db.JournalEntries.AsNoTracking().SingleAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                && entry.SourceType.ToString() == "FiscalYearClose");
            var revenueBalance = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == revenueAndReceivable.Revenue)
                .SumAsync(line => line.CreditAmount - line.DebitAmount);
            var retainedEarnings = await db.Accounts.AsNoTracking().SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "3000");
            var retainedBalance = await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.AccountId == retainedEarnings.Id)
                .SumAsync(line => line.CreditAmount - line.DebitAmount);

            Assert.Equal(QtsBilling.Persistence.Postgres.AccountingPeriodStatus.Locked, period.Status);
            Assert.Equal(new DateOnly(fiscalYear, 12, 31), closeEntry.EntryDate);
            Assert.Equal(QtsBilling.Persistence.Postgres.EntryStatus.Posted, closeEntry.Status);
            Assert.Equal(0m, revenueBalance);
            Assert.Equal(100m, retainedBalance);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });

        var duplicateWithNewKey = await PostYearCloseAsync(client, fiscalYear, "year-close-again-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Conflict, duplicateWithNewKey.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostYearCloseAsync(HttpClient client, int fiscalYear, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/accounting/periods/{fiscalYear}/close-year");
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
