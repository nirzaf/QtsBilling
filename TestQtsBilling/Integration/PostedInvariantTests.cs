using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Posted-document immutability and final-state guards (specification task 09, findings F17/F19/F32).
/// </summary>
/// <remarks>
/// The probes below are raw SQL, not API calls: the database itself must refuse a posted invoice,
/// sale, payment or journal entry from being rewritten by any path, including an operator session.
/// A guard that only lives in a handler is not a guard.
/// </remarks>
public sealed class PostedInvariantTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PostedInvariantTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T09_01_an_issued_invoice_rejects_headers_lines_moves_and_deletes()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var issued = await CreateAndIssueInvoiceAsync(client, "issued guard");
        var draft = await CreateInvoiceAsync(client, "move target");

        // 1. The financial terms of the header cannot change.
        await AssertRefusedAsync(
            $"update invoices set total_amount = total_amount + 1 where id = '{issued.InvoiceId}';");

        // 2. An existing line cannot change.
        await AssertRefusedAsync(
            $"update invoice_lines set quantity = quantity + 1 where invoice_id = '{issued.InvoiceId}';");

        // 3. No new line can be added after issue.
        await AssertRefusedAsync($"""
            insert into invoice_lines
                (id, business_id, invoice_id, line_no, variant_id, product_name_snapshot, sku_snapshot,
                 quantity, unit_price, discount_amount, tax_rate, tax_amount, line_total, created_at_utc, updated_at_utc)
            select gen_random_uuid(), business_id, invoice_id, line_no + 100, variant_id, product_name_snapshot,
                   sku_snapshot, quantity, unit_price, discount_amount, tax_rate, tax_amount, line_total, now(), now()
              from invoice_lines
             where invoice_id = '{issued.InvoiceId}';
            """);

        // 4. A line cannot be moved out of the issued invoice into a draft (the old parent is checked,
        //    not only the new one).
        await AssertRefusedAsync(
            $"update invoice_lines set invoice_id = '{draft}' where invoice_id = '{issued.InvoiceId}';");

        // 5. The invoice itself cannot be deleted.
        await AssertRefusedAsync($"delete from invoices where id = '{issued.InvoiceId}';");

        // The refused attempts left the document exactly as issued.
        var (lines, total) = await _fixture.WithFreshContextAsync(async db =>
        {
            var lineCount = await db.InvoiceLines.CountAsync(line => line.InvoiceId == issued.InvoiceId);
            var headerTotal = await db.Invoices
                .Where(invoice => invoice.Id == issued.InvoiceId)
                .Select(invoice => invoice.TotalAmount)
                .SingleAsync();
            return (lineCount, headerTotal);
        });

        Assert.Equal(1, lines);
        Assert.Equal(issued.Total, total);
    }

    [Fact]
    public async Task T09_04_posted_money_and_journal_facts_are_immutable()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        // ---------------------------------------------------------------- a posted sale and its cash
        var saleId = await PostSaleAsync(client);

        await AssertRefusedAsync($"update sales set total_amount = total_amount + 1 where id = '{saleId}';");
        await AssertRefusedAsync($"update payments set amount = amount + 1 where sale_id = '{saleId}';");
        await AssertRefusedAsync($"delete from payments where sale_id = '{saleId}';");
        await AssertRefusedAsync($"delete from sales where id = '{saleId}';");

        // ---------------------------------------------------------------- a posted journal entry
        var (cashAccount, revenueAccount) = await _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;
            var cash = await db.Accounts.Where(account => account.BusinessId == businessId && account.Code == "1000")
                .Select(account => account.Id).SingleAsync();
            var revenue = await db.Accounts.Where(account => account.BusinessId == businessId && account.Code == "4000")
                .Select(account => account.Id).SingleAsync();
            return (cash, revenue);
        });

        var entryId = await PostJournalAsync(client, cashAccount, revenueAccount);

        await AssertRefusedAsync($"update journal_entries set total_debit = total_debit + 1 where id = '{entryId}';");
        await AssertRefusedAsync($"update journal_entries set entry_date = entry_date - 1 where id = '{entryId}';");
        await AssertRefusedAsync($"""
            insert into journal_lines
                (id, business_id, journal_entry_id, line_no, account_id, debit_amount, credit_amount, currency, created_at_utc, updated_at_utc)
            select gen_random_uuid(), business_id, journal_entry_id, line_no + 100, account_id, 1, 0, currency, now(), now()
              from journal_lines
             where journal_entry_id = '{entryId}'
             limit 1;
            """);

        // The balance check still sees a balanced entry: the refused probes did not change it.
        var balanced = await _fixture.WithFreshContextAsync(async db =>
        {
            var entry = await db.JournalEntries.AsNoTracking().SingleAsync(candidate => candidate.Id == entryId);
            return entry.TotalDebit == entry.TotalCredit && entry.TotalDebit == 10m;
        });

        Assert.True(balanced);
    }

    [Fact]
    public async Task T09_05_append_only_history_cannot_be_truncated()
    {
        var refused = await AssertRefusedAsync("truncate table audit_events;");
        Assert.Contains("append-only", refused.MessageText, StringComparison.OrdinalIgnoreCase);

        // The journal and sale history tables are protected the same way even when no foreign key
        // happens to block a TRUNCATE first.
        await AssertRefusedAsync("truncate table journal_entries cascade;");
    }

    [Fact]
    public async Task T09_03_the_issue_transition_requires_lines_and_a_matching_total()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var draft = await CreateInvoiceAsync(client, "final state");

        // A stale header total cannot be smuggled in with the issue transition.
        await AssertSequenceRefusedAsync(
            $"update invoices set total_amount = total_amount + 1, balance_due = balance_due + 1 where id = '{draft}';",
            $"update invoices set status = 'Issued' where id = '{draft}';");

        // A draft's lines may be removed while it is still draft; it then has nothing to issue.
        await ExecuteAsync($"delete from invoice_lines where invoice_id = '{draft}';");

        // A draft cannot jump to issued without lines.
        await AssertRefusedAsync($"update invoices set status = 'Issued' where id = '{draft}';");

        // The API refuses to issue an empty draft with a typed conflict rather than a database error.
        var issue = await client.PostAsync($"/api/v1/invoices/{draft}/issue", content: null);
        Assert.Equal(HttpStatusCode.Conflict, issue.StatusCode);

        // An issued invoice can never be rewound to draft, and a stale financial total cannot be
        // smuggled in with the transition.
        var real = await CreateAndIssueInvoiceAsync(client, "issued final state");

        await AssertRefusedAsync($"update invoices set status = 'Draft' where id = '{real.InvoiceId}';");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<IssuedInvoice> CreateAndIssueInvoiceAsync(HttpClient client, string note)
    {
        var id = await CreateInvoiceAsync(client, note);

        var issue = await client.PostAsync($"/api/v1/invoices/{id}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();

        Assert.True(
            issue.IsSuccessStatusCode,
            $"The invoice could not be issued with {(int)issue.StatusCode}: {issueBody}");

        var total = await _fixture.WithFreshContextAsync(async db =>
            await db.Invoices.Where(invoice => invoice.Id == id).Select(invoice => invoice.TotalAmount).SingleAsync());

        return new IssuedInvoice(id, total);
    }

    private async Task<Guid> CreateInvoiceAsync(HttpClient client, string note)
    {
        var customerId = await CreateCustomerAsync(client);
        var response = await client.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            note,
            [
                new InvoiceLineRequest(
                    _fixture.BusinessA.VariantId,
                    ProductName: null,
                    Quantity: 1m,
                    UnitPrice: 115m,
                    DiscountAmount: 0m,
                    TaxRate: 0.15m),
            ]));

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"The draft invoice could not be created with {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var email = $"guard-{Guid.NewGuid():N}@fixture.test";
        var phone = $"+9745{Guid.NewGuid():N}".Substring(0, 12);

        var response = await client.PostAsJsonAsync(
            "/api/v1/customers",
            new { name = "Invoice guard customer", email, phone });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"The customer could not be created with {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostSaleAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                customerId = (Guid?)null,
                currency = "QAR",
                lines = new[]
                {
                    new
                    {
                        variantId = _fixture.BusinessA.VariantId,
                        quantity = 1,
                        unitPrice = 100m,
                        discountAmount = 0m,
                        taxRate = 0.15m,
                    },
                },
                payment = new { method = "Cash", amount = 115m, cashReceived = 115m, reference = (string?)null },
                note = "posted invariant",
            }),
        };

        request.Headers.Add("Idempotency-Key", "t09-" + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"The sale was refused with {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("saleId").GetGuid();
    }

    private async Task<Guid> PostJournalAsync(HttpClient client, Guid debitAccount, Guid creditAccount)
    {
        var response = await client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = DateOnly.FromDateTime(DateTime.UtcNow),
            memo = "posted invariant",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new[]
            {
                new { accountId = debitAccount, debit = 10m, credit = 0m, description = "debit", locationId = (Guid?)null },
                new { accountId = creditAccount, debit = 0m, credit = 10m, description = "credit", locationId = (Guid?)null },
            },
        });

        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"The journal entry was refused with {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<PostgresException> AssertRefusedAsync(string sql)
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
            return refused;
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs setup statements and asserts the final statement is refused, all in one rolled-back transaction.</summary>
    private async Task AssertSequenceRefusedAsync(params string[] statements)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var sql in statements.Take(statements.Length - 1))
            {
                await using var setup = connection.CreateCommand();
                setup.Transaction = transaction;
                setup.CommandText = sql;
                await setup.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statements[^1];
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

    private sealed record IssuedInvoice(Guid InvoiceId, decimal Total);
}
