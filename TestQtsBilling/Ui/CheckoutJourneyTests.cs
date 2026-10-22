using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The checkout journey as an operator works it, against the real host (specification task 20, T20.01 - T20.04).
/// A sale whose answer was lost stays one sale; a refused checkout commits nothing and has to be reviewed before
/// it is tried again; a cash sale completes against a local server and shows the server's own receipt; and a card
/// claim without a verified provider produces no payment and no sale. Every call is the typed contract the browser
/// POS uses, with the same key and body the screen retains.
/// </summary>
public sealed class CheckoutJourneyTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T20_01_a_retry_of_a_lost_answer_returns_the_same_sale_and_commits_it_exactly_once()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T20 replay {suffix}");
        await OpenDrawerAsync(owner, registerId, 100m);
        var (variantId, revision) = await ReadVariantAsync(catalog);

        var key = "t20-lost-" + suffix;
        var request = CashSale(variantId, revision, quantity: 2m, unitPrice: 100m, taxRate: 0.15m, tender: 230m, registerId);

        var first = await PostSaleAsync(owner, request, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstSaleId = JsonDocument.Parse(firstBody).RootElement.GetProperty("saleId").GetGuid();

        // The answer never arrived, so the operator retries the same sale. The server replays its stored receipt.
        var retry = await PostSaleAsync(owner, request, key);
        var retryBody = await retry.Content.ReadAsStringAsync();
        Assert.True(retry.IsSuccessStatusCode, $"the retry was refused: {(int)retry.StatusCode} {retryBody}");
        Assert.Equal(firstSaleId, JsonDocument.Parse(retryBody).RootElement.GetProperty("saleId").GetGuid());
        Assert.Equal(
            JsonDocument.Parse(firstBody).RootElement.GetProperty("saleNumber").GetString(),
            JsonDocument.Parse(retryBody).RootElement.GetProperty("saleNumber").GetString());

        // A third attempt with a changed basket under the same key is a conflict, not a second sale.
        var changed = await PostSaleAsync(owner, CashSale(variantId, revision, 3m, 100m, 0.15m, 345m, registerId), key);
        var changedBody = await changed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains(ApiErrorCodes.SameKeyDifferentPayload, changedBody, StringComparison.Ordinal);

        var effects = await _fixture.WithFreshContextAsync(async db => (
            Sales: await db.Sales.AsNoTracking().CountAsync(sale => sale.BusinessId == _fixture.BusinessA.BusinessId
                && sale.IdempotencyKey == key),
            Payments: await db.Payments.AsNoTracking().CountAsync(payment => payment.SaleId == firstSaleId),
            Movements: await db.InventoryMovements.AsNoTracking().CountAsync(movement => movement.ReferenceType == "sale_line"
                && db.SaleLines.Any(line => line.Id == movement.ReferenceId && line.SaleId == firstSaleId)),
            Journals: await db.JournalEntries.AsNoTracking().CountAsync(entry => entry.SourceType == JournalSourceType.Sale
                && entry.SourceId == firstSaleId),
            OnHand: (await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.VariantId == variantId && balance.BusinessId == _fixture.BusinessA.BusinessId)
                .Select(balance => (decimal?)balance.OnHand)
                .FirstOrDefaultAsync()) ?? 0m));

        // Exactly one of every business effect, and stock moved for two units once.
        Assert.Equal(1, effects.Sales);
        Assert.Equal(1, effects.Payments);
        Assert.Equal(1, effects.Movements);
        Assert.Equal(1, effects.Journals);
    }

    [Fact]
    public async Task T20_02_a_refused_checkout_commits_nothing_and_the_operator_has_to_review_before_retrying()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T20 refusal {suffix}");
        var (variantId, revision) = await ReadVariantAsync(catalog);
        await OpenDrawerAsync(owner, registerId, 10m);

        // 3a. The owner withdraws the counter's discount allowance through the real administration path, so the
        //     refusal below tests a configured business fact and not a seed artefact (specification task 20
        //     recipe step 3; task 7 gives a new cashier no allowance at all). Authority changes rotate the
        //     revision, so the counter signs in again afterwards - an older session is refused outright.
        var users = new AdministrationUserApiClient(owner);
        var cashierRow = (await users.ListUsersAsync(search: _fixture.Users.Cashier.Email))
            .Items.Single(candidate => candidate.UserId == _fixture.Users.Cashier.UserId);
        var allowanceSet = await users.UpdateMembershipAsync(
            cashierRow.UserId,
            new UpdateMembershipRequest(
                cashierRow.MembershipVersion,
                cashierRow.Role,
                cashierRow.AllLocations,
                cashierRow.AllLocations ? null : cashierRow.LocationIds,
                0m,
                "the counter may not discount without a supervisor"));
        Assert.Equal(0m, allowanceSet.MaxDiscountRate);

        using var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);

        var before = await ReadDrawerAsync(owner, registerId);
        var stockBefore = await StockAsync(variantId);

        // 1. A basket priced away from the catalog is refused: the server never trusts a client price
        //    (specification task 20, recipe step 1).
        var stale = await PostSaleAsync(owner, CashSale(variantId, revision, 1m, 90m, 0.15m, 103.5m, registerId), "t20-price-" + suffix);
        var staleBody = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(ApiErrorCodes.InvalidPrice, staleBody, StringComparison.Ordinal);

        // 2. More stock than the branch holds is refused by the ledger, and the roll-back is total.
        var tooMuch = await PostSaleAsync(owner, CashSale(variantId, revision, 100_000m, 100m, 0.15m, 11_500_000m, registerId), "t20-stock-" + suffix);
        var tooMuchBody = await tooMuch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Contains(ApiErrorCodes.InsufficientStock, tooMuchBody, StringComparison.Ordinal);

        // 3b. A discount beyond what this membership may give away is refused as an authority problem.
        var overDiscounted = await PostSaleAsync(cashier, new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(variantId, 1m, 100m, 10m, 0.15m, revision) { TaxMode = QtsModels.ValueObjects.CommercialTaxMode.Exclusive }],
            new PaymentCaptureRequest("Cash", 103.5m, 103.5m, null),
            "t20 discount beyond allowance",
            registerId), "t20-discount-" + suffix);
        var discountBody = await overDiscounted.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, overDiscounted.StatusCode);
        Assert.Contains(ApiErrorCodes.PermissionDenied, discountBody, StringComparison.Ordinal);
        Assert.Contains("allowance", discountBody, StringComparison.OrdinalIgnoreCase);

        // None of the three committed anything: no sale for those keys, no payment, no movement, and the drawer
        // has not moved by a fils.
        var committed = await _fixture.WithFreshContextAsync(db => db.Sales.AsNoTracking().CountAsync(sale => sale.BusinessId == _fixture.BusinessA.BusinessId
            && (sale.IdempotencyKey == "t20-price-" + suffix
                || sale.IdempotencyKey == "t20-stock-" + suffix
                || sale.IdempotencyKey == "t20-discount-" + suffix)));
        Assert.Equal(0, committed);

        var afterRefusals = await ReadDrawerAsync(owner, registerId);
        Assert.Equal(before.ExpectedCash, afterRefusals.ExpectedCash);
        Assert.Equal(stockBefore, await StockAsync(variantId));

        // 4. Reviewed and corrected, the same basket posts once - and only that once moves the drawer.
        var corrected = await PostSaleAsync(owner, CashSale(variantId, revision, 1m, 100m, 0.15m, 115m, registerId), "t20-fixed-" + suffix);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);

        var afterPost = await ReadDrawerAsync(owner, registerId);
        Assert.Equal(125m, afterPost.ExpectedCash);
        Assert.True((await StockAsync(variantId)) < stockBefore);
    }

    [Fact]
    public async Task T20_03_a_cash_sale_completes_against_the_local_server_and_shows_the_servers_own_receipt()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T20 receipt {suffix}");
        await OpenDrawerAsync(owner, registerId, 100m);
        var (variantId, revision) = await ReadVariantAsync(catalog);

        var stockBefore = await StockAsync(variantId);
        var response = await PostSaleAsync(owner, CashSale(variantId, revision, 2m, 100m, 0.15m, 245m, registerId), "t20-receipt-" + suffix);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saleId = JsonDocument.Parse(body).RootElement.GetProperty("saleId").GetGuid();
        var saleNumber = JsonDocument.Parse(body).RootElement.GetProperty("saleNumber").GetString();

        // The posted result is never presented as provisional money: an online cash sale is a posted one.
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("provisional").GetBoolean());

        var receipt = await owner.GetFromJsonAsync<SaleReceiptDto>($"/api/v1/pos/sales/{saleId:D}/receipt", QtsBilling.Contracts.Serialization.ContractJson.Options);

        Assert.NotNull(receipt);
        Assert.Equal(saleNumber, receipt!.SaleNumber);
        Assert.Contains("post", receipt.ReceiptState, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(230m, receipt.TotalAmount);

        // The line, the payment and the change are the server's own snapshots, at its settlement scale.
        var line = Assert.Single(receipt.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal(30m, line.TaxAmount);

        var payment = Assert.Single(receipt.Payments);
        Assert.Equal("Cash", payment.Method);
        Assert.Contains("confirm", payment.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(245m, payment.CashReceived);
        Assert.Equal(15m, payment.Change);

        // The printable receipt is rendered locally from the same snapshot and fetches nothing public.
        var html = await owner.GetStringAsync($"/api/v1/pos/sales/{saleId:D}/receipt?format=html");
        Assert.Contains(saleNumber!, html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);

        // Stock moved exactly once, by the sold quantity, in the branch that sold it.
        Assert.Equal(stockBefore - 2m, await StockAsync(variantId));

        // Provisional capture belongs to an enrolled terminal only: the device host is the one that registers the
        // local service, and the browser host does not, so a browser cannot claim cash it never confirmed.
        var root = InvoiceEditorTests.RepositoryRoot();
        var deviceStartup = await File.ReadAllTextAsync(Path.Combine(root, @"QtsBilling.Maui\MauiProgram.cs"));
        var browserStartup = await File.ReadAllTextAsync(Path.Combine(root, @"QtsWebApp\QtsWebApp.Client\Program.cs"));
        Assert.Contains("ILocalPosService, DeviceLocalPosService", deviceStartup, StringComparison.Ordinal);
        Assert.DoesNotContain("ILocalPosService", browserStartup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T20_04_a_forged_card_reference_creates_no_confirmed_payment_and_no_sale()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registerId = await CreateRegisterAsync(owner, $"T20 card {suffix}");
        await OpenDrawerAsync(owner, registerId, 100m);
        var (variantId, revision) = await ReadVariantAsync(catalog);

        // The core has no provider, so an intent cannot even be created - and no provider setup is required to
        // finish the project (specification task 20, recipe step 4).
        var intent = await PostJsonAsync(owner, "/api/v1/pos/card-intents",
            new CreateTerminalPaymentIntentRequest(_fixture.BusinessA.LocationId, 115m, "QAR"),
            "t20-intent-" + suffix);
        var intentBody = await intent.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, intent.StatusCode);
        Assert.Contains(ApiErrorCodes.PaymentMethodDisabled, intentBody, StringComparison.Ordinal);

        // A sale carrying a hand-written provider reference is refused: without a signed capture there is nothing
        // to confirm against, so the money stays unconfirmed and the sale is not written.
        var forged = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(variantId, 1m, 100m, 0m, 0.15m, revision) { TaxMode = QtsModels.ValueObjects.CommercialTaxMode.Exclusive }],
            new PaymentCaptureRequest("Card", 115m, null, "PSP-FORGED-REFERENCE-1"),
            "t20 forged card reference",
            registerId);

        var forgedResponse = await PostSaleAsync(owner, forged, "t20-forged-" + suffix);
        var forgedBody = await forgedResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, forgedResponse.StatusCode);
        Assert.Contains(ApiErrorCodes.PaymentUnconfirmed, forgedBody, StringComparison.Ordinal);

        // A forged signature envelope is refused too, and by the core rule: no provider is configured, so nothing
        // can be verified and nothing is acquired (specification task 20, recipe step 4).
        var forgedEnvelope = new SaleCaptureRequest(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(variantId, 1m, 100m, 0m, 0.15m, revision) { TaxMode = QtsModels.ValueObjects.CommercialTaxMode.Exclusive }],
            new PaymentCaptureRequest("Card", 115m, null, "PSP-FORGED-REFERENCE-2", new TerminalCaptureEnvelope("forged-signature-header", "{\"forged\":true}")),
            "t20 forged card envelope",
            registerId);

        var envelopeResponse = await PostSaleAsync(owner, forgedEnvelope, "t20-forged-envelope-" + suffix);
        var envelopeBody = await envelopeResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, envelopeResponse.StatusCode);
        Assert.Contains(ApiErrorCodes.PaymentMethodDisabled, envelopeBody, StringComparison.Ordinal);

        // Nothing was recorded: no sale for either key, no payment, no journal, and the drawer unmoved.
        var rows = await _fixture.WithFreshContextAsync(async db => (
            Sales: await db.Sales.AsNoTracking().CountAsync(sale => sale.BusinessId == _fixture.BusinessA.BusinessId
                && (sale.IdempotencyKey == "t20-forged-" + suffix || sale.IdempotencyKey == "t20-forged-envelope-" + suffix)),
            // StartsWith with an ordinal comparison is not translatable, so the existence check uses LIKE.
            Payments: await db.Payments.AsNoTracking().CountAsync(payment => payment.PspReference != null
                && EF.Functions.Like(payment.PspReference, "PSP-FORGED%")),
            CardPayments: await db.Payments.AsNoTracking().CountAsync(payment => payment.BusinessId == _fixture.BusinessA.BusinessId
                && payment.Method == PaymentMethod.Card)));

        Assert.Equal(0, rows.Sales);
        Assert.Equal(0, rows.Payments);
        Assert.Equal(0, rows.CardPayments);

        // The same basket as a cash sale is accepted, so the refusal is about the unverified card claim and not
        // about the payload.
        var cash = await PostSaleAsync(owner, CashSale(variantId, revision, 1m, 100m, 0.15m, 115m, registerId), "t20-cash-after-" + suffix);
        Assert.Equal(HttpStatusCode.Created, cash.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private SaleCaptureRequest CashSale(
        Guid variantId, string revision, decimal quantity, decimal unitPrice, decimal taxRate, decimal tender, Guid registerId)
        => new(
            _fixture.BusinessA.LocationId,
            null,
            "QAR",
            [new SaleLineRequest(variantId, quantity, unitPrice, 0m, taxRate, revision)
            {
                TaxMode = QtsModels.ValueObjects.CommercialTaxMode.Exclusive,
            }],
            new PaymentCaptureRequest("Cash", quantity * unitPrice * (1m + taxRate), tender, null),
            "task 20 checkout journey",
            registerId);

    private async Task<(Guid VariantId, string Revision)> ReadVariantAsync(ProductApiClient catalog)
    {
        var detail = await catalog.GetProductAsync(_fixture.BusinessA.ProductId);
        var variant = detail.Variants.Single(candidate => candidate.Id == _fixture.BusinessA.VariantId);

        // What the counter caches as the observed catalog revision is the variant's own version.
        return (variant.Id, variant.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<decimal> StockAsync(Guid variantId)
        => await _fixture.WithFreshContextAsync(async db =>
            (await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == _fixture.BusinessA.BusinessId && balance.VariantId == variantId)
                .SumAsync(balance => (decimal?)balance.OnHand)) ?? 0m);

    private async Task<CashSessionDetailDto> ReadDrawerAsync(HttpClient client, Guid registerId)
    {
        var response = await client.GetAsync($"/api/v1/pos/registers/{registerId:D}/sessions/current");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the drawer could not be read: {(int)response.StatusCode} {body}");

        return await response.Content.ReadFromJsonAsync<CashSessionDetailDto>(QtsBilling.Contracts.Serialization.ContractJson.Options)
            ?? throw new InvalidOperationException("the drawer answered without a body");
    }

    private async Task<HttpResponseMessage> PostSaleAsync(HttpClient client, SaleCaptureRequest request, string key)
        => await PostJsonAsync(client, "/api/v1/pos/sales", request, key);

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string route, object payload, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: QtsBilling.Contracts.Serialization.ContractJson.Options),
        };
        message.Headers.Add("Idempotency-Key", key);

        return await client.SendAsync(message);
    }

    private async Task<Guid> CreateRegisterAsync(HttpClient client, string name)
    {
        var response = await PostJsonAsync(client, "/api/v1/pos/registers",
            new CreateCashRegisterRequest(_fixture.BusinessA.LocationId, name), "t20-register-" + Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the register could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> OpenDrawerAsync(HttpClient client, Guid registerId, decimal floatAmount)
    {
        var response = await PostJsonAsync(client, $"/api/v1/pos/registers/{registerId:D}/sessions",
            new OpenCashRegisterSessionRequest(floatAmount, "T20 float"), "t20-open-" + Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the drawer could not be opened: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }
}
