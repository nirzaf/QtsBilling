using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Serialization;
using QtsModels.ValueObjects;
using Xunit;

namespace TestQtsBilling.Contracts;

public sealed class SaleApiClientTests
{
    [Fact]
    public async Task PostSaleAsync_sends_the_idempotency_key_and_reads_the_posted_result()
    {
        var expected = new SaleCaptureResult(
            Guid.CreateVersion7(),
            "S-1001",
            "USD",
            115m,
            15m,
            DateTimeOffset.Parse("2026-10-20T10:00:00Z"),
            Provisional: false);
        var handler = new CaptureHandler(expected);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://billing.test") };
        var client = new SaleApiClient(http);
        var request = new SaleCaptureRequest(
            Guid.CreateVersion7(),
            null,
            "USD",
            [new SaleLineRequest(Guid.CreateVersion7(), 1m, 100m, 0m, 0.15m, "7")
            {
                TaxMode = CommercialTaxMode.Exclusive,
                PricingPolicyVersion = "3",
            }],
            new PaymentCaptureRequest("Cash", 115m, 120m, null),
            null,
            Guid.CreateVersion7());

        var result = await client.PostSaleAsync(request, "checkout-operation-key");

        Assert.Equal(expected, result);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/v1/pos/sales/", handler.PathAndQuery);
        Assert.Equal("checkout-operation-key", handler.IdempotencyKey);
        Assert.Contains("\"locationId\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"taxMode\":\"exclusive\"", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task PostSaleAsync_rejects_an_empty_idempotency_key(string key)
    {
        using var http = new HttpClient(new CaptureHandler(null)) { BaseAddress = new Uri("https://billing.test") };
        var client = new SaleApiClient(http);
        var request = new SaleCaptureRequest(Guid.NewGuid(), null, "USD", [], new PaymentCaptureRequest("Cash", 0m, 0m, null), null, null);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PostSaleAsync(request, key));
    }

    private sealed class CaptureHandler(SaleCaptureResult? result) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Content = JsonContent.Create(result, options: ContractJson.Options);
            return response;
        }
    }
}
