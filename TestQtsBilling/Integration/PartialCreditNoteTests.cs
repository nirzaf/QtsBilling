using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;
using InvoiceStatus = QtsBilling.Persistence.Postgres.InvoiceStatus;

namespace TestQtsBilling.Integration;

public sealed class PartialCreditNoteTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PartialCreditNoteTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Partial_credit_preserves_issued_invoice_arithmetic_at_100_percent_tax()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
                await db.SaveChangesAsync();
            }
        });

        var customerId = await CreateCustomerAsync(client);
        var invoiceId = await CreateIssuedInvoiceAsync(client, customerId, taxRate: 1m);
        var lineId = await GetInvoiceLineIdAsync(invoiceId);
        var response = await CreditAsync(client, invoiceId, new InvoiceCreditNoteRequest(
            "partial credit at the issued tax rate",
            null,
            [new InvoiceCreditLineRequest(lineId, 0.5m)]));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Partial credit failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(100m, json.RootElement.GetProperty("invoice").GetProperty("creditedAmount").GetDecimal());
        Assert.Equal(100m, json.RootElement.GetProperty("invoice").GetProperty("balanceDue").GetDecimal());
        var creditedLine = json.RootElement.GetProperty("creditNote").GetProperty("lines")[0];
        Assert.Equal(50m, creditedLine.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(100m, creditedLine.GetProperty("lineTotal").GetDecimal());
    }

    [Fact]
    public async Task Partial_credit_notes_reconcile_quantities_amounts_stock_and_paid_invoice_state()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await _fixture.WithFreshContextAsync(async db =>
        {
            if (!await db.Accounts.AnyAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "4010"))
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Code = "4010",
                    Name = "Sales returns",
                    AccountType = AccountType.Revenue,
                });
                await db.SaveChangesAsync();
            }
        });

        var customerId = await CreateCustomerAsync(client);
        var firstInvoiceId = await CreateIssuedInvoiceAsync(client, customerId);
        var firstLineId = await GetInvoiceLineIdAsync(firstInvoiceId);

        var firstRequest = new InvoiceCreditNoteRequest(
            "partial credit",
            null,
            [new InvoiceCreditLineRequest(firstLineId, 0.4m, RestockInventory: true)]);
        var firstKey = "partial-credit-first-" + Guid.NewGuid().ToString("N");
        var first = await CreditAsync(client, firstInvoiceId, firstRequest, firstKey);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"First credit failed with {(int)first.StatusCode}: {firstBody}");
        using (var firstJson = JsonDocument.Parse(firstBody))
        {
            var invoice = firstJson.RootElement.GetProperty("invoice");
            Assert.Equal(69m, invoice.GetProperty("balanceDue").GetDecimal());
            Assert.Equal(46m, invoice.GetProperty("creditedAmount").GetDecimal());
            Assert.Single(firstJson.RootElement.GetProperty("creditNotes").EnumerateArray());
            Assert.Equal(0.4m, firstJson.RootElement.GetProperty("creditNote").GetProperty("lines")[0].GetProperty("quantityCredited").GetDecimal());
        }
        var replay = await CreditAsync(client, firstInvoiceId, firstRequest, firstKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await _fixture.WithFreshContextAsync(async db =>
            Assert.Equal(1, await db.CreditNotes.CountAsync(note => note.InvoiceId == firstInvoiceId)));

        var overCredit = await CreditAsync(client, firstInvoiceId, new InvoiceCreditNoteRequest(
            "over credit should be rejected",
            null,
            [new InvoiceCreditLineRequest(firstLineId, 0.7m)]));
        Assert.Equal(HttpStatusCode.BadRequest, overCredit.StatusCode);

        var second = await CreditAsync(client, firstInvoiceId, new InvoiceCreditNoteRequest(
            "finish partial credit",
            null,
            [new InvoiceCreditLineRequest(firstLineId, 0.6m)]));
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.True(second.StatusCode == HttpStatusCode.Created, $"Second credit failed with {(int)second.StatusCode}: {secondBody}");

        var paidInvoiceId = await CreateIssuedInvoiceAsync(client, customerId);
        var pay = new InvoicePaymentRequest("Cash", 115m, null, null);
        using var paymentMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{paidInvoiceId}/payments")
        {
            Content = JsonContent.Create(pay),
        };
        paymentMessage.Headers.Add("Idempotency-Key", "credit-paid-payment-" + Guid.NewGuid().ToString("N"));
        var paymentResponse = await client.SendAsync(paymentMessage);
        Assert.Equal(HttpStatusCode.Created, paymentResponse.StatusCode);

        var paidLineId = await GetInvoiceLineIdAsync(paidInvoiceId);
        var paidCredit = await CreditAsync(client, paidInvoiceId, new InvoiceCreditNoteRequest(
            "credit paid invoice",
            null,
            [new InvoiceCreditLineRequest(paidLineId, 0.5m)]));
        var paidCreditBody = await paidCredit.Content.ReadAsStringAsync();
        Assert.True(paidCredit.StatusCode == HttpStatusCode.Created, $"Paid invoice credit failed with {(int)paidCredit.StatusCode}: {paidCreditBody}");
        using (var paidJson = JsonDocument.Parse(paidCreditBody))
        {
            var invoice = paidJson.RootElement.GetProperty("invoice");
            Assert.Equal("paid", invoice.GetProperty("status").GetString());
            Assert.Equal(0m, invoice.GetProperty("balanceDue").GetDecimal());
            Assert.Equal(57.5m, invoice.GetProperty("creditedAmount").GetDecimal());
        }

        await _fixture.WithFreshContextAsync(async db =>
        {
            var firstInvoice = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == firstInvoiceId);
            Assert.Equal(InvoiceStatus.Void, firstInvoice.Status);
            Assert.Equal(115m, firstInvoice.CreditedAmount);
            Assert.Equal(0m, firstInvoice.BalanceDue);
            Assert.Equal(2, await db.CreditNotes.CountAsync(note => note.InvoiceId == firstInvoiceId));
            Assert.Equal(2, await db.CreditNoteLines.CountAsync(line => db.CreditNotes.Any(note => note.Id == line.CreditNoteId && note.InvoiceId == firstInvoiceId)));
            Assert.Equal(0.4m, await db.InventoryMovements.Where(movement => movement.BusinessId == _fixture.BusinessA.BusinessId
                && movement.ReferenceType == "credit_note_line" && movement.MovementType == MovementType.SaleReversal).SumAsync(movement => movement.QuantityDelta));

            var paidInvoice = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == paidInvoiceId);
            Assert.Equal(InvoiceStatus.Paid, paidInvoice.Status);
            Assert.Equal(115m, paidInvoice.AmountPaid);
            Assert.Equal(57.5m, paidInvoice.CreditedAmount);
            var customerDeposits = await db.Accounts.AsNoTracking().SingleAsync(account => account.BusinessId == _fixture.BusinessA.BusinessId && account.Code == "2105");
            var paidCreditNote = await db.CreditNotes.AsNoTracking().SingleAsync(note => note.InvoiceId == paidInvoiceId);
            var paidEntry = await db.JournalEntries.AsNoTracking().SingleAsync(entry => entry.SourceType == JournalSourceType.CreditNote && entry.SourceId == paidCreditNote.Id);
            Assert.Equal(57.5m, await db.JournalLines.Where(line => line.JournalEntryId == paidEntry.Id && line.AccountId == customerDeposits.Id).SumAsync(line => line.CreditAmount));
        });
    }

    private async Task<Guid> CreateIssuedInvoiceAsync(HttpClient client, Guid customerId, decimal taxRate = 0.15m)
    {
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "partial credit test invoice",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 100m, 0m, taxRate)]);
        var create = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Invoice create failed with {(int)create.StatusCode}: {createBody}");
        using var createJson = JsonDocument.Parse(createBody);
        var invoiceId = createJson.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issue = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.StatusCode == HttpStatusCode.Created, $"Invoice issue failed with {(int)issue.StatusCode}: {issueBody}");
        return invoiceId;
    }

    private Task<Guid> GetInvoiceLineIdAsync(Guid invoiceId)
        => _fixture.WithFreshContextAsync(async db => await db.InvoiceLines.AsNoTracking()
            .Where(line => line.BusinessId == _fixture.BusinessA.BusinessId && line.InvoiceId == invoiceId)
            .Select(line => line.Id)
            .SingleAsync());

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}";
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Partial credit customer",
            email = $"partial-credit-{suffix}@fixture.test",
            phone,
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer create failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> CreditAsync(
        HttpClient client,
        Guid invoiceId,
        InvoiceCreditNoteRequest request,
        string? idempotencyKey = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId}/credit-notes")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey ?? "partial-credit-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(message);
    }
}
