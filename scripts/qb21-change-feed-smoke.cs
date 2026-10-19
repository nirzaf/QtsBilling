// QB-21 live smoke: writes master data, then walks the change feed.
//
//   dotnet run scripts/qb21-change-feed-smoke.cs
//
// It creates a product and a customer, deactivates the customer (a tombstone) and records an opening
// stock, then reads GET /api/v1/sync/changes from the beginning, from a cursor in the middle and from a
// cursor that points past the server (the resync case).

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
{
    BaseAddress = new Uri("http://localhost:5188"),
    Timeout = TimeSpan.FromSeconds(60),
};

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

var login = await PostJson("/api/v1/auth/login", new JsonObject
{
    ["email"] = "owner@qtsbilling.local",
    ["password"] = "QtsBilling!Dev2026",
});
Console.WriteLine($"login: {(int)login.StatusCode}");
login.EnsureSuccessStatusCode();

var stamp = Guid.NewGuid().ToString("N")[..6];

// 1. a product
var product = await PostJson("/api/v1/products", new JsonObject
{
    ["name"] = "QB-21 feed product " + stamp,
    ["sku"] = "QB21-" + stamp.ToUpperInvariant(),
    ["trackStock"] = true,
    ["sellingPrice"] = 12.5m,
});
var productBody = JsonDocument.Parse(await product.Content.ReadAsStringAsync()).RootElement.Clone();
Console.WriteLine($"create product: {(int)product.StatusCode} keys={string.Join(",", productBody.EnumerateObject().Select(p => p.Name))}");
var productId = productBody.TryGetProperty("product", out var productNode)
    ? productNode.GetProperty("id").GetGuid()
    : productBody.GetProperty("id").GetGuid();

// 2. a customer
var customer = await PostJson("/api/v1/customers", new JsonObject
{
    ["name"] = "QB-21 feed customer " + stamp,
    ["email"] = $"qb21-{stamp}@example.test",
    ["phone"] = "+974" + (40000000 + Math.Abs(stamp.GetHashCode() % 9999999)),
    ["address"] = "Doha",
});
var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync()).RootElement.Clone();
Console.WriteLine($"create customer: {(int)customer.StatusCode}");

// 3. deactivate the customer (a tombstone)
var deactivate = await client.DeleteAsync($"/api/v1/customers/{customerBody.GetProperty("id").GetGuid()}");
Console.WriteLine($"deactivate customer: {(int)deactivate.StatusCode}");

// 4. opening stock
var variantId = await GetJson($"/api/v1/products/{productId}");
Console.WriteLine($"product detail keys: {string.Join(",", variantId.EnumerateObject().Select(p => p.Name))}");
var firstVariant = variantId.GetProperty("variants")[0].GetProperty("id").GetGuid();
var balances = await GetJson("/api/v1/inventory/balances?pageSize=1");
var locationId = balances.GetProperty("items")[0].GetProperty("locationId").GetGuid();
var movement = await PostJson("/api/v1/inventory/movements", new JsonObject
{
    ["variantId"] = firstVariant,
    ["locationId"] = locationId,
    ["quantityDelta"] = 5m,
    ["referenceType"] = "opening",
    ["referenceId"] = Guid.NewGuid(),
    ["unitCost"] = 5m,
});
Console.WriteLine($"opening stock: {(int)movement.StatusCode}");

// 5. walk the feed
var feed = await GetJson("/api/v1/sync/changes?afterRevision=0&afterOrdinal=0&limit=100");
Console.WriteLine("feed from the beginning:");
Console.WriteLine($"feed keys: {string.Join(",", feed.EnumerateObject().Select(p => p.Name))}");
foreach (var change in feed.GetProperty("changes").EnumerateArray())
{
    Console.WriteLine($"   keys={string.Join(",", change.EnumerateObject().Select(p => p.Name))}");
    var hasPayload = change.TryGetProperty("payload", out var payloadElement);
    Console.WriteLine($"   revision={change.GetProperty("revision")} ordinal={change.GetProperty("ordinal")} " +
        $"type={change.GetProperty("entityType")} kind={change.GetProperty("kind")} " +
        $"payload={(hasPayload && payloadElement.ValueKind != System.Text.Json.JsonValueKind.Null ? "present" : "<none>")}");
}

Console.WriteLine($"nextCursor: revision={feed.GetProperty("nextRevision")} ordinal={feed.GetProperty("nextOrdinal")} " +
    $"hasMore={feed.GetProperty("hasMore")} resyncRequired={feed.GetProperty("resyncRequired")}");

// 6. a cursor in the middle: only the changes after it
var middle = await GetJson("/api/v1/sync/changes?afterRevision=1&afterOrdinal=0&limit=100");
Console.WriteLine($"feed after revision 1: {middle.GetProperty("changes").GetArrayLength()} change(s)");

// 7. a cursor past the server: the device must re-pull the baseline
var ahead = await GetJson("/api/v1/sync/changes?afterRevision=9999&afterOrdinal=0");
Console.WriteLine($"feed with a cursor past the server: resyncRequired={ahead.GetProperty("resyncRequired")} " +
    $"changes={ahead.GetProperty("changes").GetArrayLength()}");
