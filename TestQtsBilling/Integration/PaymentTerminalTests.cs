using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sales;
using Xunit;
using InvoiceStatus = QtsBilling.Persistence.Postgres.InvoiceStatus;
using PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus;
using JournalSourceType = QtsBilling.Persistence.Postgres.JournalSourceType;

namespace TestQtsBilling.Integration;

/// <summary>Card tenders require provider-verified captures; the unconfigured production boundary stays closed.</summary>
public sealed class PaymentTerminalTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PaymentTerminalTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Default_host_rejects_card_intents_and_card_sales_without_a_provider()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        using var intentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/card-intents")
        {
            Content = JsonContent.Create(new CreateTerminalPaymentIntentRequest(_fixture.BusinessA.LocationId, 115m, "QAR")),
        };
        intentRequest.Headers.Add("Idempotency-Key", "disabled-intent-" + Guid.NewGuid().ToString("N"));
        var intent = await client.SendAsync(intentRequest);
        Assert.Equal(HttpStatusCode.Conflict, intent.StatusCode);
        Assert.Contains("PAYMENT_METHOD_DISABLED", await intent.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var capture = new TerminalCaptureEnvelope("signature", "{}");
        using var saleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new SaleCaptureRequest(
                _fixture.BusinessA.LocationId,
                null,
                "QAR",
                [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
                new PaymentCaptureRequest("Card", 115m, null, "disabled-provider-ref", capture),
                "disabled provider test",
                null)),
        };
        saleRequest.Headers.Add("Idempotency-Key", "disabled-sale-" + Guid.NewGuid().ToString("N"));
        var sale = await client.SendAsync(saleRequest);
        Assert.Equal(HttpStatusCode.Conflict, sale.StatusCode);
        Assert.Contains("PAYMENT_METHOD_DISABLED", await sale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verified_provider_capture_posts_card_sales_and_invoice_payments_atomically()
    {
        using var factory = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentTerminalProvider>();
            services.AddSingleton<IPaymentTerminalProvider, FixturePaymentTerminalProvider>();
        }));
        using var client = await CreateAuthenticatedClientAsync(factory, _fixture.Users.OwnerA);

        var intent = await CreateIntentAsync(client, _fixture.BusinessA.LocationId, 115m, "terminal-intent-1");
        Assert.Equal(115m, intent.Amount);
        Assert.Equal("QAR", intent.Currency);
        Assert.Equal(_fixture.BusinessA.LocationId, intent.LocationId);

        var invalidCapture = Capture(intent.PspReference, _fixture.BusinessA.LocationId, 114m, "invalid-proof");
        var rejectedSale = await PostCardSaleAsync(client, intent.PspReference, invalidCapture, "terminal-sale-1");
        Assert.Equal(HttpStatusCode.Conflict, rejectedSale.StatusCode);
        Assert.Contains("PAYMENT_UNCONFIRMED", await rejectedSale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var capture = Capture(intent.PspReference, _fixture.BusinessA.LocationId, 115m, "sale-proof");
        var saleResponse = await PostCardSaleAsync(client, intent.PspReference, capture, "terminal-sale-1");
        var saleBody = await saleResponse.Content.ReadAsStringAsync();
        Assert.True(saleResponse.StatusCode == HttpStatusCode.Created, $"Verified card sale returned {(int)saleResponse.StatusCode}: {saleBody}");
        using var saleJson = JsonDocument.Parse(saleBody);
        var saleId = saleJson.RootElement.GetProperty("saleId").GetGuid();

        var saleState = await _fixture.WithFreshContextAsync(async db =>
        {
            var sale = await db.Sales.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Id == saleId);
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.SaleId == saleId);
            var bankAccountId = await db.Accounts.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Code == "1010")
                .Select(row => row.Id).SingleAsync();
            var journal = await db.JournalEntries.AsNoTracking()
                .SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.SourceType == JournalSourceType.Sale && row.SourceId == saleId);
            var bankDebit = await db.JournalLines.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.JournalEntryId == journal.Id && row.AccountId == bankAccountId)
                .SumAsync(row => row.DebitAmount);
            return (sale.PaymentStatus, payment.Status, payment.PspReference, payment.CardBrand, payment.CardLast4, bankDebit);
        });
        Assert.Equal(PaymentStatus.Confirmed, saleState.PaymentStatus);
        Assert.Equal(PaymentStatus.Confirmed, saleState.Status);
        Assert.Equal(intent.PspReference, saleState.PspReference);
        Assert.Equal("Visa", saleState.CardBrand);
        Assert.Equal("4242", saleState.CardLast4);
        Assert.Equal(115m, saleState.bankDebit);

        var customerId = await CreateCustomerAsync(client);
        var invoiceId = await CreateIssuedInvoiceAsync(client, customerId);
        var invoiceReference = "invoice-" + Guid.NewGuid().ToString("N");
        var invoiceCapture = Capture(invoiceReference, _fixture.BusinessA.LocationId, 115m, "invoice-proof");
        var invoicePayment = await PostInvoiceCardPaymentAsync(client, invoiceId, invoiceReference, invoiceCapture, "terminal-invoice-1");
        var invoicePaymentBody = await invoicePayment.Content.ReadAsStringAsync();
        Assert.True(invoicePayment.StatusCode == HttpStatusCode.Created, $"Verified invoice card payment returned {(int)invoicePayment.StatusCode}: {invoicePaymentBody}");
        using var paymentJson = JsonDocument.Parse(invoicePaymentBody);
        Assert.Equal("Confirmed", paymentJson.RootElement.GetProperty("payment").GetProperty("status").GetString());
        Assert.Equal(invoiceReference, paymentJson.RootElement.GetProperty("payment").GetProperty("pspReference").GetString());
        Assert.Equal("Visa", paymentJson.RootElement.GetProperty("payment").GetProperty("cardBrand").GetString());
        Assert.Equal("4242", paymentJson.RootElement.GetProperty("payment").GetProperty("cardLast4").GetString());
        Assert.Equal("paid", paymentJson.RootElement.GetProperty("invoice").GetProperty("status").GetString());

        var replay = await PostInvoiceCardPaymentAsync(client, invoiceId, invoiceReference, invoiceCapture, "terminal-invoice-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(paymentJson.RootElement.GetProperty("payment").GetProperty("id").GetGuid(),
            replayJson.RootElement.GetProperty("payment").GetProperty("id").GetGuid());

        await _fixture.WithFreshContextAsync(async db =>
        {
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.Id == invoiceId);
            var payment = await db.Payments.AsNoTracking().SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.InvoiceId == invoiceId);
            var summaries = await db.AuditEvents.AsNoTracking()
                .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId)
                .Select(row => row.Summary)
                .ToListAsync();
            var rawMarkerOccurrences = summaries.Count(summary => summary?.Contains("invoice-proof", StringComparison.Ordinal) == true);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(PaymentStatus.Confirmed, payment.Status);
            Assert.Equal("Visa", payment.CardBrand);
            Assert.Equal("4242", payment.CardLast4);
            Assert.Equal(0, rawMarkerOccurrences);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, _fixture.BusinessA.BusinessId);
        });
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, FixtureUser user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["password"] = FixtureUsers.Password,
        });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<TerminalPaymentIntentDto> CreateIntentAsync(HttpClient client, Guid locationId, decimal amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/card-intents")
        {
            Content = JsonContent.Create(new CreateTerminalPaymentIntentRequest(locationId, amount, "QAR")),
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Intent creation returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<TerminalPaymentIntentDto>(body, QtsBilling.Contracts.Serialization.ContractJson.Options)!;
    }

    private async Task<HttpResponseMessage> PostCardSaleAsync(HttpClient client, string reference, TerminalCaptureEnvelope capture, string key)
    {
        var request = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(_fixture.BusinessA.VariantId, 1m, 100m, 0m, 0.15m, null)],
            new PaymentCaptureRequest("Card", 115m, null, reference, capture),
            "verified terminal sale",
            null);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }

    private async Task<HttpResponseMessage> PostInvoiceCardPaymentAsync(
        HttpClient client, Guid invoiceId, string reference, TerminalCaptureEnvelope capture, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invoices/{invoiceId:D}/payments")
        {
            Content = JsonContent.Create(new InvoicePaymentRequest("Card", 115m, reference, null, capture)),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static TerminalCaptureEnvelope Capture(string pspReference, Guid locationId, decimal amount, string proof)
        => new("fixture-signature", JsonSerializer.Serialize(new FixtureCapturePayload(pspReference, locationId, amount, "QAR", proof)));

    private async Task<Guid> CreateIssuedInvoiceAsync(HttpClient client, Guid customerId)
    {
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "verified terminal invoice",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 100m, 0m, 0.15m)]);
        var create = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Invoice creation returned {(int)create.StatusCode}: {createBody}");
        using var createJson = JsonDocument.Parse(createBody);
        var id = createJson.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issue = await client.PostAsync($"/api/v1/invoices/{id:D}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.StatusCode == HttpStatusCode.Created, $"Invoice issue returned {(int)issue.StatusCode}: {issueBody}");
        return id;
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Terminal test customer",
            email = $"terminal-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer creation returned {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record FixtureCapturePayload(string PspReference, Guid LocationId, decimal Amount, string Currency, string Proof);

    private sealed class FixturePaymentTerminalProvider : IPaymentTerminalProvider
    {
        public Task<TerminalPaymentIntentDto> CreateIntentAsync(Guid businessId, Guid locationId, decimal amount, string currency, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new TerminalPaymentIntentDto("fixture-" + idempotencyKey, locationId, amount, currency, "requires_terminal_capture", null, DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<TerminalCaptureVerification> VerifyCaptureAsync(Guid businessId, string pspReference, string signatureHeader, string rawPayload, CancellationToken ct)
        {
            var payload = JsonSerializer.Deserialize<FixtureCapturePayload>(rawPayload);
            if (signatureHeader != "fixture-signature" || payload is null)
                return Task.FromResult(new TerminalCaptureVerification(false, pspReference, Guid.Empty, 0m, "", null, null));

            return Task.FromResult(new TerminalCaptureVerification(true, payload.PspReference, payload.LocationId,
                payload.Amount, payload.Currency, "Visa", "4242"));
        }
    }
}
