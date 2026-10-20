using System.Net;
using System.Net.Http.Json;
using QtsBilling.Contracts.Sales;
using QtsBilling.Contracts.Serialization;
using Xunit;

namespace TestQtsBilling.Contracts;

public sealed class CashRegisterApiClientTests
{
    [Fact]
    public async Task OpenSessionAsync_posts_the_float_to_the_selected_register()
    {
        var registerId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var session = new CashRegisterSessionDto(
            Guid.CreateVersion7(), registerId, "USD", CashRegisterSessionState.Open, 75m,
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, null, null, 75m, null, null);
        var expected = new CashSessionDetailDto(
            session,
            "Front till",
            locationId,
            75m,
            0m,
            new CashSessionBreakdownDto(75m, 0m, 0m, 0m, 0m, 0m),
            []);
        var handler = new CaptureHandler(expected);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://billing.test") };
        var client = new CashRegisterApiClient(http);

        var result = await client.OpenSessionAsync(registerId, new OpenCashRegisterSessionRequest(75m, "Morning float"));

        Assert.Equal(session.Id, result.Session.Id);
        Assert.Equal(75m, result.ExpectedCash);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"/api/v1/pos/registers/{registerId:D}/sessions", handler.PathAndQuery);
        Assert.Contains("\"openingFloat\":75", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"Morning float\"", handler.Body, StringComparison.Ordinal);
    }

    private sealed class CaptureHandler(CashSessionDetailDto result) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(result, options: ContractJson.Options),
            };
        }
    }
}
