using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Exact reversal of an issued invoice by partial credits (specification task 03, T03.01).
/// </summary>
/// <remarks>
/// Each intermediate credit uses the tax composition the line was issued with; the final credit of a line takes the
/// exact remaining residual, so the credits reverse the original document to the cent.
/// </remarks>
public sealed class InvoiceRecognitionTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public InvoiceRecognitionTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T03_01_three_partial_credits_reverse_the_original_total_exactly()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await EnsureSalesReturnsAccountAsync();

        var customerId = await CreateCustomerAsync(client);
        var (invoiceId, invoiceTotal) = await CreateIssuedDirectGoodsInvoiceAsync(client, customerId);
        var lineId = await GetInvoiceLineIdAsync(invoiceId);

        // 3 x 33.33 at 15% exclusive: quantities that do not divide the line evenly exercise the rounding residual.
        decimal creditedSoFar = 0m;
        foreach (var quantity in new[] { 1m, 1m, 1m })
        {
            var response = await CreditAsync(client, invoiceId, new InvoiceCreditNoteRequest(
                "partial credit reconciliation",
                null,
                [new InvoiceCreditLineRequest(lineId, quantity)]));

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"Partial credit of {quantity} failed with {(int)response.StatusCode}: {body}");

            using var json = JsonDocument.Parse(body);
            creditedSoFar = json.RootElement.GetProperty("invoice").GetProperty("creditedAmount").GetDecimal();
        }

        Assert.Equal(invoiceTotal, creditedSoFar);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
            Assert.Equal(0m, invoice.BalanceDue);
            Assert.Equal(invoiceTotal, invoice.CreditedAmount);

            var creditedLines = await db.CreditNoteLines.AsNoTracking()
                .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.InvoiceLineId == lineId)
                .ToListAsync();
            Assert.Equal(3m, creditedLines.Sum(line => line.QuantityCredited));
            Assert.Equal(invoice.TotalAmount, creditedLines.Sum(line => line.LineTotal));
            Assert.Equal(invoice.TaxAmount, creditedLines.Sum(line => line.TaxAmount));
        });
    }

    private async Task EnsureSalesReturnsAccountAsync()
    {
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new QtsBilling.Persistence.Postgres.Entities.Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
                await db.SaveChangesAsync();
            }
        });
    }

    private async Task<(Guid InvoiceId, decimal Total)> CreateIssuedDirectGoodsInvoiceAsync(HttpClient client, Guid customerId)
    {
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "three partial credits",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 3m, 33.33m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods);

        var create = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Invoice create failed with {(int)create.StatusCode}: {createBody}");

        using var createJson = JsonDocument.Parse(createBody);
        var invoiceId = createJson.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();

        var issue = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"Invoice issue failed with {(int)issue.StatusCode}: {issueBody}");

        using var issuedJson = JsonDocument.Parse(issueBody);
        var total = issuedJson.RootElement.GetProperty("invoice").GetProperty("totalAmount").GetDecimal();
        return (invoiceId, total);
    }

    private Task<Guid> GetInvoiceLineIdAsync(Guid invoiceId)
        => _fixture.WithFreshContextAsync(async db => await db.InvoiceLines.AsNoTracking()
            .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.InvoiceId == invoiceId)
            .Select(line => line.Id)
            .SingleAsync());

    private static async Task<HttpResponseMessage> CreditAsync(HttpClient client, Guid invoiceId, InvoiceCreditNoteRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId}/credit-notes")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", "recognition-credit-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(message);
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Recognition customer",
            email = $"recognition-{Guid.NewGuid():N}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer create failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
