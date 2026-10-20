using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Accounting-period posting and close consistency (specification task 10, finding F20).
/// </summary>
/// <remarks>
/// A posted fact must always belong to exactly one open period. The tests prove the fail-closed
/// edge (no period), the closed edge, the overlap/reopen guards and that posting and closing
/// serialize on the period row instead of racing.
/// </remarks>
public sealed class PeriodPostingTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PeriodPostingTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T10_01_a_posting_outside_every_period_is_refused()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var accounts = await AccountsAsync(_fixture.BusinessA.BusinessId);

        // The fixture's only period starts in 2000; 1990 is before all accounting.
        var response = await PostJournalAsync(client, new DateOnly(1990, 1, 1), accounts);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodMissing, CodeOf(body));
    }

    [Fact]
    public async Task T10_02_a_closed_period_refuses_posting()
    {
        // Business B is only used by this class, so closing its period cannot affect another test.
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var accounts = await AccountsAsync(_fixture.BusinessB.BusinessId);

        var close = await client.PostAsync(
            $"/api/v1/accounting-periods/{_fixture.BusinessB.AccountingPeriodId}/close",
            content: null);
        var closeBody = await close.Content.ReadAsStringAsync();
        Assert.True(close.IsSuccessStatusCode, $"The period could not be closed with {(int)close.StatusCode}: {closeBody}");

        var response = await PostJournalAsync(
            client,
            DateOnly.FromDateTime(DateTime.UtcNow),
            accounts);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodClosed, CodeOf(body));

        // Closing twice is refused rather than silently succeeding.
        var again = await client.PostAsync(
            $"/api/v1/accounting-periods/{_fixture.BusinessB.AccountingPeriodId}/close",
            content: null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task T10_03_overlapping_periods_and_reopening_are_refused_by_the_database()
    {
        var businessId = _fixture.BusinessA.BusinessId;

        // 2050 is inside the fixture period (2000-01-01..2100-12-31).
        await AssertRefusedAsync($"""
            insert into accounting_periods
                (id, name, start_date, end_date, status, created_at_utc, updated_at_utc, business_id, version)
            values (gen_random_uuid(), 'Overlapping 2050', '2050-01-01', '2050-12-31', 'Open', now(), now(), '{businessId}', 0);
            """);

        // The API refuses the same overlap with its stable code before changing anything.
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var overlap = await client.PostAsJsonAsync("/api/v1/accounting-periods", new
        {
            name = "Overlapping 2050",
            startDate = new DateOnly(2050, 1, 1),
            endDate = new DateOnly(2050, 12, 31),
        });
        var overlapBody = await overlap.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountingPeriodOverlap, CodeOf(overlapBody));

        // A closed period cannot be reopened by an ordinary update.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await using (var close = connection.CreateCommand())
            {
                close.Transaction = transaction;
                close.CommandText =
                    $"update accounting_periods set status = 'Closed' where id = '{_fixture.BusinessA.AccountingPeriodId}';";
                await close.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var reopen = connection.CreateCommand();
                reopen.Transaction = transaction;
                reopen.CommandText =
                    $"update accounting_periods set status = 'Open' where id = '{_fixture.BusinessA.AccountingPeriodId}';";
                await reopen.ExecuteNonQueryAsync();
            });

            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Contains("reopened", refused.MessageText, StringComparison.OrdinalIgnoreCase);
            await transaction.RollbackAsync();
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
    }

    [Fact]
    public async Task T10_04_posting_serializes_on_the_period_row_with_closing()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var accounts = await AccountsAsync(_fixture.BusinessA.BusinessId);

        // Hold the exclusive lock the close path takes (SELECT ... FOR UPDATE) and prove the posting
        // path's SELECT ... FOR SHARE waits instead of reading a stale open status.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.Transaction = transaction;
                lockCommand.CommandText =
                    $"select id from accounting_periods where id = '{_fixture.BusinessA.AccountingPeriodId}' for update;";
                await lockCommand.ExecuteScalarAsync();
            }

            var posting = PostJournalAsync(
                client,
                DateOnly.FromDateTime(DateTime.UtcNow),
                accounts);

            var blocked = await WaitUntilBlockedAsync(posting);
            Assert.True(blocked, "Posting did not wait for the period row lock; a close could race it.");

            await transaction.RollbackAsync();

            var response = await posting;
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Posting after the lock was released was refused with {(int)response.StatusCode}: {body}");
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(Guid Debit, Guid Credit)> AccountsAsync(Guid businessId)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var cash = await db.Accounts.Where(account => account.BusinessId == businessId && account.Code == "1000")
                .Select(account => account.Id).SingleAsync();
            var revenue = await db.Accounts.Where(account => account.BusinessId == businessId && account.Code == "4000")
                .Select(account => account.Id).SingleAsync();
            return (cash, revenue);
        });

    private static Task<HttpResponseMessage> PostJournalAsync(
        HttpClient client,
        DateOnly entryDate,
        (Guid Debit, Guid Credit) accounts)
        => client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate,
            memo = "period posting",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = accounts.Debit, debit = 10m, credit = 0m, description = "debit", locationId = (Guid?)null },
                new { accountId = accounts.Credit, debit = 0m, credit = 10m, description = "credit", locationId = (Guid?)null },
            },
        });

    private static string? CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>True when the task is still pending after giving it time to reach the database.</summary>
    private static async Task<bool> WaitUntilBlockedAsync(Task task)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (task.IsCompleted)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return !task.IsCompleted;
    }

    private async Task AssertRefusedAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            });

            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            await transaction.RollbackAsync();
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
    }
}
