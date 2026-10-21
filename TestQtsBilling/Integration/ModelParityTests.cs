using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using QtsBilling.Contracts.Invoices;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Schema and posted-document parity after the invoice snapshot migration (specification task 01, T01.03).
/// </summary>
/// <remarks>
/// The database must hold the invoice recognition source and sale-link uniqueness that the EF model declares, and it
/// must refuse to rewrite the recognition source of an invoice that has left draft, even through raw SQL. The
/// "no pending model change" half of T01.03 is checked by <c>dotnet ef migrations has-pending-model-changes</c>,
/// which is recorded as a separate verification command.
/// </remarks>
public sealed class ModelParityTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public ModelParityTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T01_03_a_an_issued_invoice_cannot_have_its_recognition_source_rewritten_by_raw_sql()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoiceId = await CreateIssuedServiceInvoiceAsync(client);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            $"update invoices set recognition_mode = 'DirectGoods' where id = '{invoiceId}';"));

        Assert.Contains("immutable", Flatten(exception), StringComparison.OrdinalIgnoreCase);

        var stored = await ScalarAsync<string>($"select recognition_mode from invoices where id = '{invoiceId}';");
        Assert.Equal(nameof(InvoiceRecognitionMode.Service), stored);
    }

    [Fact]
    public async Task T01_03_b_the_one_invoice_per_sale_rule_is_a_unique_database_index()
    {
        // One economic invoice per sale: a retried conversion cannot recognise the same sale a second time.
        var definition = await ScalarAsync<string>(
            "select indexdef from pg_indexes where indexname = 'ux_invoices_business_source_sale';");

        Assert.StartsWith("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("source_sale_id IS NOT NULL", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T01_03_c_the_recognition_mode_check_rejects_an_unknown_source()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var draftId = await CreateDraftServiceInvoiceAsync(client);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            $"update invoices set recognition_mode = 'Guessed' where id = '{draftId}';"));

        Assert.Contains("ck_invoices_recognition_mode", Flatten(exception), StringComparison.OrdinalIgnoreCase);
    }

    private Task ExecuteAsync(string sql)
        => WithConnectionAsync<int>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync();
        });

    private Task<T> ScalarAsync<T>(string sql)
        => WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)(await command.ExecuteScalarAsync())!;
        });

    private async Task<T> WithConnectionAsync<T>(Func<NpgsqlConnection, Task<T>> action)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        return await action(connection);
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
            if (current is PostgresException postgres)
            {
                parts.Add(postgres.MessageText);
                parts.Add(postgres.ConstraintName ?? string.Empty);
            }
        }

        return string.Join(" | ", parts);
    }

    private async Task<Guid> CreateIssuedServiceInvoiceAsync(HttpClient client)
    {
        var invoiceId = await CreateDraftServiceInvoiceAsync(client);

        var issue = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"Invoice issue failed with {(int)issue.StatusCode}: {issueBody}");

        return invoiceId;
    }

    private async Task<Guid> CreateDraftServiceInvoiceAsync(HttpClient client)
    {
        var customerId = await CreateCustomerAsync(client);
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "model parity service invoice",
            [new InvoiceLineRequest(null, "Consulting", 1m, 100m, 0m, 0.15m)],
            InvoiceRecognitionMode.Service);

        var create = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Invoice create failed with {(int)create.StatusCode}: {createBody}");

        using var json = JsonDocument.Parse(createBody);
        return json.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Model parity customer",
            email = $"model-parity-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer create failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
