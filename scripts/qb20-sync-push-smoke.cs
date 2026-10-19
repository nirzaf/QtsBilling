// QB-20 live smoke: pushes an offline operation to the server and checks the five documented outcomes.
//
//   dotnet run scripts/qb20-sync-push-smoke.cs <deviceId> <businessId> <locationId>
//
// The script signs in as the development owner, makes sure a product, a cash register and an open drawer
// exist, opens the stock, and then pushes the same operation five times:
//   #1 the operation itself, #2 the replay, #3 the same key with a different body,
//   #4 an operation from a device that is not enrolled, #5 an operation for another business.
// The device id must be enrolled in the business (device_registrations); the enrolment flow itself
// arrives with QB-22.
#:property TargetFramework=net10.0
#:project C:/Users/DELL/repos/QtsBilling/QtsBilling.Contracts/QtsBilling.Contracts.csproj
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QtsBilling.Contracts.Serialization;

var baseAddress = "http://localhost:5188";
var deviceId = Guid.Parse(args.Length > 0 ? args[0] : throw new ArgumentException("device id required"));
var businessId = Guid.Parse(args.Length > 1 ? args[1] : throw new ArgumentException("business id required"));
var locationId = Guid.Parse(args.Length > 2 ? args[2] : throw new ArgumentException("location id required"));

var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
var client = new HttpClient(handler) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

async Task<HttpResponseMessage> PostJson(string route, JsonNode body)
    => await client.PostAsync(route, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

async Task<JsonElement> GetJson(string route)
{
    var response = await client.GetAsync(route);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"GET {route} -> {(int)response.StatusCode} {text}");
    }

    return JsonDocument.Parse(text).RootElement.Clone();
}

// 1. sign in (a fresh development account must complete the setup first)
var password = "QtsBilling!Dev2026";

var login = await PostJson("/api/v1/auth/login", new JsonObject
{
    ["email"] = "owner@qtsbilling.local",
    ["password"] = password,
});

if ((int)login.StatusCode == 403)
{
    var ticketResponse = await PostJson("/api/v1/auth/password-reset/request", new JsonObject
    {
        ["email"] = "owner@qtsbilling.local",
    });
    var ticket = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync()).RootElement.Clone();
    var token = ticket.GetProperty("token").GetString()!;

    var complete = await PostJson("/api/v1/auth/password-reset/complete", new JsonObject
    {
        ["email"] = "owner@qtsbilling.local",
        ["token"] = token,
        ["newPassword"] = password,
    });
    Console.WriteLine($"password setup: {(int)complete.StatusCode}");
    complete.EnsureSuccessStatusCode();

    login = await PostJson("/api/v1/auth/login", new JsonObject
    {
        ["email"] = "owner@qtsbilling.local",
        ["password"] = password,
    });
}

Console.WriteLine($"login: {(int)login.StatusCode}");
login.EnsureSuccessStatusCode();

var session = await GetJson("/api/v1/auth/session");
var userId = session.GetProperty("userId").GetGuid();
Console.WriteLine($"session user={userId} business={session.GetProperty("businessId")}");

// 2. a product with a variant (the catalog the device caches)
var products = await GetJson("/api/v1/products?pageSize=50");
Guid variantId;
decimal unitPrice;

if (products.GetProperty("items").GetArrayLength() == 0 || Environment.GetEnvironmentVariable("QB20_NEW_PRODUCT") == "1")
{
    var created = await PostJson("/api/v1/products", new JsonObject
    {
        ["name"] = "QB-20 offline smoke product " + Guid.NewGuid().ToString("N")[..6],
        ["sku"] = "QB20-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        ["trackStock"] = true,
        ["sellingPrice"] = 30m,
    });
    Console.WriteLine($"create product: {(int)created.StatusCode}");
    created.EnsureSuccessStatusCode();
}

products = await GetJson("/api/v1/products?pageSize=50");
var productId = products.GetProperty("items")[0].GetProperty("id").GetGuid();
var detail = await GetJson($"/api/v1/products/{productId}");
var variant = detail.GetProperty("variants")[0];
variantId = variant.GetProperty("id").GetGuid();
unitPrice = variant.GetProperty("sellingPrice").GetDecimal();

Console.WriteLine($"variant={variantId} price={unitPrice}");

// 3. a cash register for the location
var registers = await GetJson("/api/v1/pos/registers");
Guid registerId;

if (registers.GetArrayLength() == 0)
{
    var created = await PostJson("/api/v1/pos/registers", new JsonObject
    {
        ["name"] = "QB-20 offline register",
        ["locationId"] = locationId.ToString(),
    });
    Console.WriteLine($"create register: {(int)created.StatusCode}");
    created.EnsureSuccessStatusCode();
    var registerBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.Clone();
    registerId = registerBody.GetProperty("id").GetGuid();
}
else
{
    registerId = registers[0].GetProperty("id").GetGuid();
}

Console.WriteLine($"register={registerId}");

// the drawer must be open for a cash payment
var sessionResponse = await PostJson($"/api/v1/pos/registers/{registerId}/sessions", new JsonObject
{
    ["openedAtUtc"] = DateTimeOffset.UtcNow,
    ["note"] = "QB-20 offline smoke",
});
Console.WriteLine($"open drawer: {(int)sessionResponse.StatusCode} {await sessionResponse.Content.ReadAsStringAsync()}");

// 4. opening stock for the variant
if (Environment.GetEnvironmentVariable("QB20_NO_STOCK") == "1") { Console.WriteLine("opening stock: skipped"); } else { var stock = await PostJson("/api/v1/inventory/movements", new JsonObject
{
    ["variantId"] = variantId.ToString(),
    ["locationId"] = locationId.ToString(),
    ["quantityDelta"] = 10m,
    ["referenceType"] = "opening",
    ["referenceId"] = Guid.NewGuid(),
    ["unitCost"] = 10m,
    ["note"] = "QB-20 offline smoke",
});
Console.WriteLine($"opening stock: {(int)stock.StatusCode} {await stock.Content.ReadAsStringAsync()}"); }

// 4. the canonical payload the device stored
var operationId = Guid.CreateVersion7();

JsonObject BuildPayload(decimal cashReceived, decimal total)
{
    return new JsonObject
    {
        ["businessId"] = businessId.ToString(),
        ["cashReceived"] = cashReceived,
        ["deviceId"] = deviceId.ToString(),
        ["lines"] = new JsonArray(new JsonObject
        {
            ["discount"] = 0m,
            ["expectedCatalogRevision"] = 1,
            ["ordinal"] = 0,
            ["quantity"] = 1m,
            ["tax"] = 0m,
            ["taxRate"] = 0m,
            ["unitPrice"] = unitPrice,
            ["variantId"] = variantId.ToString(),
        }),
        ["locationId"] = locationId.ToString(),
        ["note"] = null,
        ["operationId"] = operationId,
        ["paymentMethod"] = "cash",
        ["registerId"] = registerId.ToString(),
        ["total"] = total,
        ["type"] = "pos.sale.capture.v1",
        ["userId"] = userId.ToString(),
        ["version"] = 1,
    };
}

async Task<HttpResponseMessage> Push(JsonObject payload, string payloadHash, Guid operation, Guid? pushDevice = null, Guid? pushBusiness = null)
{

    return await PostJson("/api/v1/sync/operations", new JsonObject
    {
        ["deviceId"] = (pushDevice ?? deviceId).ToString(),
        ["operationId"] = operation,
        ["businessId"] = (pushBusiness ?? businessId).ToString(),
        ["locationId"] = locationId.ToString(),
        ["type"] = "pos.sale.capture.v1",
        ["payloadVersion"] = 1,
        ["createdAtUtc"] = DateTimeOffset.UtcNow,
        ["payloadHash"] = payloadHash,
        ["payload"] = payload.DeepClone(),
    });
}

var payload = BuildPayload(50m, 30m);
var canonical = CanonicalJson.ToCanonicalString(JsonDocument.Parse(payload.ToJsonString()).RootElement);
var fingerprint = CanonicalJson.ComputeFingerprint(canonical);
Console.WriteLine($"canonical fingerprint: {fingerprint}");

var first = await Push(payload, fingerprint, operationId);
Console.WriteLine($"push #1 -> {(int)first.StatusCode} {await first.Content.ReadAsStringAsync()}");

var second = await Push(payload, fingerprint, operationId);
Console.WriteLine($"push #2 (replay) -> {(int)second.StatusCode} {await second.Content.ReadAsStringAsync()}");

var divergent = BuildPayload(60m, 30m);
var divergentCanonical = CanonicalJson.ToCanonicalString(JsonDocument.Parse(divergent.ToJsonString()).RootElement);
var divergentFingerprint = CanonicalJson.ComputeFingerprint(divergentCanonical);
var third = await Push(divergent, divergentFingerprint, operationId);
Console.WriteLine($"push #3 (same key, different body) -> {(int)third.StatusCode} {await third.Content.ReadAsStringAsync()}");

var fourth = await Push(payload, fingerprint, Guid.CreateVersion7(), Guid.CreateVersion7());
Console.WriteLine($"push #4 (unknown device) -> {(int)fourth.StatusCode} {await fourth.Content.ReadAsStringAsync()}");

var fifth = await Push(payload, fingerprint, Guid.CreateVersion7(), null, Guid.CreateVersion7());
Console.WriteLine($"push #5 (other business) -> {(int)fifth.StatusCode} {await fifth.Content.ReadAsStringAsync()}");

Console.WriteLine($"OPERATION_ID={operationId:D}");
