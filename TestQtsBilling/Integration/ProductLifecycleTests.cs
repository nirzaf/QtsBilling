using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Product and variant maintenance as an operator leaves it (specification task 17, T17.01 - T17.03), through the
/// typed client the catalog screens use: a variant can be added and retired, an archive is an explicit act with a
/// reason, a stale or cross-tenant write changes nothing at all, and the keys that must not duplicate are decided
/// by the database rather than by which of two concurrent requests happened to read first.
/// </summary>
public sealed class ProductLifecycleTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T17_01_two_concurrent_variants_sharing_a_key_leave_exactly_one_of_them()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var product = await catalog.CreateProductAsync(new SaveProductRequest(
            $"T17 race product {suffix}", $"T17-RACE-{suffix}", _fixture.BusinessA.UnitId, null,
            TrackStock: false, "Single", 40m, 20m));
        var version = product.Product.Version;

        // 1. Two creates that both pass their own pre-check: the database decides, and the loser is refused.
        var skuRace = await Task.WhenAll(
            TryVariant(catalog, product.Product.Id, new CreateProductVariantRequest(
                $"T17 twin one {suffix}", $"T17-SKU-{suffix}", null, 41m, null, version)),
            TryVariant(catalog, product.Product.Id, new CreateProductVariantRequest(
                $"T17 twin two {suffix}", $"T17-SKU-{suffix}", null, 42m, null, version)));

        Assert.Equal(1, skuRace.Count(result => result.Ok));
        var skuLosers = skuRace.Where(result => !result.Ok).ToArray();
        Assert.Single(skuLosers);

        // Which refusal the loser meets is a matter of interleaving, not of what the database guarantees: a loser
        // that read before the winner committed is refused by the SKU index, and a loser that read after it is
        // refused by the version the winner advanced. Both say the same thing - the variant was not added - so the
        // race is asserted on that, and the SKU-specific code is proved below without a race in it.
        Assert.True(skuLosers[0].Failure!.IsConflict, $"the losing create answered {skuLosers[0].Failure!.Code}");
        Assert.Contains(skuLosers[0].Failure!.Code, new[] { ApiErrorCodes.DuplicateSku, ApiErrorCodes.OptimisticConcurrencyFailure });

        await _fixture.WithFreshContextAsync(async db =>
            Assert.Equal(1, await db.ProductVariants.AsNoTracking().CountAsync(
                variant => variant.BusinessId == _fixture.BusinessA.BusinessId && variant.Sku == $"T17-SKU-{suffix}")));

        // And the refusal the race may not have had time to produce is proved against a settled catalog: the SKU is
        // taken, so a create read at the current version names that key rather than a scheduling accident.
        var settled = await catalog.GetProductAsync(product.Product.Id);
        var repeat = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateVariantAsync(
            product.Product.Id, new CreateProductVariantRequest(
                $"T17 repeat {suffix}", $"T17-SKU-{suffix}", null, 45m, null, settled.Product.Version)));
        Assert.Equal(ApiErrorCodes.DuplicateSku, repeat.Code);
        await _fixture.WithFreshContextAsync(async db =>
            Assert.Equal(1, await db.ProductVariants.AsNoTracking().CountAsync(
                variant => variant.BusinessId == _fixture.BusinessA.BusinessId && variant.Sku == $"T17-SKU-{suffix}")));

        // 2. The same race on the barcode, which is the key a scanner actually reads.
        var fresh = await catalog.GetProductAsync(product.Product.Id);
        var barcodeRace = await Task.WhenAll(
            TryVariant(catalog, fresh.Product.Id, new CreateProductVariantRequest(
                $"T17 bar one {Guid.NewGuid():N}"[..18], null, $"T17-BC-{suffix}", 43m, null, fresh.Product.Version)),
            TryVariant(catalog, fresh.Product.Id, new CreateProductVariantRequest(
                $"T17 bar two {Guid.NewGuid():N}"[..18], null, $"T17-BC-{suffix}", 44m, null, fresh.Product.Version)));

        Assert.Equal(1, barcodeRace.Count(result => result.Ok));
        Assert.Equal(1, barcodeRace.Count(result => !result.Ok && result.Failure!.IsConflict));

        await _fixture.WithFreshContextAsync(async db =>
            Assert.Equal(1, await db.ProductVariants.AsNoTracking().CountAsync(
                variant => variant.BusinessId == _fixture.BusinessA.BusinessId && variant.Barcode == $"T17-BC-{suffix}")));

        // 3. A retry of the same create is not a second variant: the name is unique within the product, so an
        //    ambiguous attempt is refused and the operator can read the list and see the one that landed.
        var afterFirst = await catalog.GetProductAsync(product.Product.Id);
        var variantCount = afterFirst.Variants.Count;

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateVariantAsync(
            product.Product.Id, new CreateProductVariantRequest("Single", null, null, 40m, null, afterFirst.Product.Version)));
        Assert.True(duplicate.IsConflict);
        Assert.Contains("name", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var unchanged = await catalog.GetProductAsync(product.Product.Id);
        Assert.Equal(variantCount, unchanged.Variants.Count);
    }

    [Fact]
    public async Task T17_02_a_stale_or_foreign_write_changes_nothing_in_the_product_or_the_feed()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        using var otherBusiness = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var created = await catalog.CreateProductAsync(new SaveProductRequest(
            $"T17 versioned {suffix}", null, _fixture.BusinessA.UnitId, null,
            TrackStock: false, "Standard", 25m, null));
        var productId = created.Product.Id;
        var readVersion = created.Product.Version;

        var revisionsBefore = await FeedCountAsync(productId);

        // 1. Two operators read the same product. The first save lands; the second is refused and stores nothing,
        //    so the screen can show the failure without the record having moved underneath it.
        var first = await catalog.UpdateProductAsync(productId, new UpdateProductRequest(
            $"T17 renamed by one {suffix}", null, null, _fixture.BusinessA.UnitId, null, null, null, "Exclusive", readVersion));
        Assert.Equal($"T17 renamed by one {suffix}", first.Product.Name);

        var stale = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateProductAsync(productId, new UpdateProductRequest(
            $"T17 renamed by two {suffix}", null, null, _fixture.BusinessA.UnitId, null, null, null, "Exclusive", readVersion)));
        Assert.True(stale.IsConflict);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, stale.Code);

        var stored = await catalog.GetProductAsync(productId);
        Assert.Equal($"T17 renamed by one {suffix}", stored.Product.Name);
        Assert.False(stored.Product.Name.Contains("by two", StringComparison.Ordinal));

        // The refused write emitted nothing: the feed moves only with a change that committed.
        var auditRows = await _fixture.WithFreshContextAsync(db => db.AuditEvents.AsNoTracking()
            .CountAsync(entry => entry.EntityType == "product" && entry.EntityId == productId));
        Assert.Equal(2, auditRows); // created + the one accepted update

        // 2. A category belonging to another business cannot be referenced, and the attempt changes nothing.
        //    The other business creates its own category, because a foreign key is proven with a real row.
        var foreignCategory = await CreateCategoryAsync(otherBusiness, $"T17 foreign category {suffix}", retire: false);

        var foreign = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateProductAsync(productId, new UpdateProductRequest(
            stored.Product.Name, null, foreignCategory, _fixture.BusinessA.UnitId, null, null, null, "Exclusive", stored.Product.Version)));
        Assert.Equal(400, foreign.StatusCode);
        Assert.True(foreign.Errors.ContainsKey("categoryId"), $"the refusal did not name the field: {foreign.Message}");

        var unchanged = await catalog.GetProductAsync(productId);
        Assert.Null(unchanged.Product.CategoryId);
        Assert.Equal(stored.Product.Version, unchanged.Product.Version);

        // 3. A reference that is this business's but retired is refused too, so a product cannot be filed under a
        //    category the counter no longer uses.
        var retiredCategory = await CreateCategoryAsync(owner, $"T17 retired category {suffix}", retire: true);
        var inactiveReference = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateProductAsync(productId, new UpdateProductRequest(
            stored.Product.Name, null, retiredCategory, _fixture.BusinessA.UnitId, null, null, null, "Exclusive", unchanged.Product.Version)));
        Assert.Equal(400, inactiveReference.StatusCode);
        Assert.True(inactiveReference.Errors.ContainsKey("categoryId"));

        // 4. The same product id in another business's hands is invisible, not a permission to edit.
        var crossTenant = await Assert.ThrowsAsync<ApiException>(() => new ProductApiClient(otherBusiness).UpdateProductAsync(
            productId, new UpdateProductRequest(
                " hijacked", null, null, null, null, null, null, "Exclusive", stored.Product.Version)));
        Assert.True(crossTenant.IsNotFound, $"a foreign business's update answered {crossTenant.StatusCode}");

        var untouched = await catalog.GetProductAsync(productId);
        Assert.Equal($"T17 renamed by one {suffix}", untouched.Product.Name);

        //    Nothing but the one accepted rename reached the devices.
        Assert.Equal(1, await FeedCountAsync(productId) - revisionsBefore);
    }

    [Fact]
    public async Task T17_03_protected_semantics_survive_an_archive_and_future_selection_follows_active_status()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new ProductApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sku = $"T17-UNIT-{suffix}";

        var created = await catalog.CreateProductAsync(new SaveProductRequest(
            $"T17 tracked {suffix}", sku, _fixture.BusinessA.UnitId, null,
            TrackStock: true, "Case", 60m, 30m));
        var productId = created.Product.Id;
        var variantId = created.Variants.Single().Id;

        // 1. Once quantity has moved, the unit that quantity was stated in is history: the product edit is
        //    refused rather than silently redefining a "case" (specification task 17, recipe step 2).
        var otherUnit = await CreateUnitAsync(owner, $"T17 other unit {suffix}", "T17U");

        await RecordMovementAsync(owner, variantId);

        var movements = await _fixture.WithFreshContextAsync(db => db.InventoryMovements.AsNoTracking()
            .CountAsync(movement => movement.BusinessId == _fixture.BusinessA.BusinessId && movement.VariantId == variantId));
        Assert.Equal(1, movements);

        var current = await catalog.GetProductAsync(productId);
        var unitChange = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateProductAsync(
            productId, new UpdateProductRequest(
                current.Product.Name, sku, null, otherUnit, null, null, null, "Exclusive", current.Product.Version)));
        Assert.True(unitChange.IsConflict);
        Assert.Equal(ApiErrorCodes.ApprovalRequired, unitChange.Code);
        Assert.Contains("unit", unitChange.Message, StringComparison.OrdinalIgnoreCase);

        var stillSame = await catalog.GetProductAsync(productId);
        Assert.Equal(_fixture.BusinessA.UnitId, stillSame.Product.UnitId);
        Assert.True(stillSame.Product.TrackStock);

        // A description-only correction still works: the guard is about the protected semantics, not about the record.
        var renamed = await catalog.UpdateProductAsync(productId, new UpdateProductRequest(
            $"T17 tracked renamed {suffix}", sku, null, _fixture.BusinessA.UnitId, null, null, null, "Exclusive", stillSame.Product.Version));
        Assert.Equal($"T17 tracked renamed {suffix}", renamed.Product.Name);
        Assert.Equal(_fixture.BusinessA.UnitId, renamed.Product.UnitId);

        // 2. A posted document that priced this variant keeps its snapshot after the archive.
        var customerId = await CreateCustomerAsync(owner, $"T17 archive customer {suffix}");
        var draft = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "T17 archive history",
            [new InvoiceLineRequest(variantId, null, 1m, 60m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods), ContractJson.Options);
        var draftBody = await draft.Content.ReadAsStringAsync();
        Assert.True(draft.StatusCode == HttpStatusCode.Created, $"the draft could not be created: {(int)draft.StatusCode} {draftBody}");

        var invoiceId = JsonDocument.Parse(draftBody).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issued = await owner.PostAsJsonAsync($"/api/v1/invoices/{invoiceId}/issue", new { });
        Assert.True(issued.IsSuccessStatusCode, $"the invoice could not be issued: {await issued.Content.ReadAsStringAsync()}");

        var beforeIssue = await ReadInvoiceAsync(owner, invoiceId);

        //    The stock ledger the adjustment and the issued document left behind, captured so that the archive can
        //    be shown not to have rewritten it.
        var ledgerBeforeArchive = await LedgerRowsAsync(variantId);
        Assert.Equal(2, ledgerBeforeArchive.Count);

        // 3. Archiving is its own operation: it needs the version and a reason, and it is idempotent.
        var noReason = await Assert.ThrowsAsync<ApiException>(() => catalog.ArchiveProductAsync(
            productId, new ArchiveProductRequest(renamed.Product.Version, " ")));
        Assert.Equal(400, noReason.StatusCode);

        var staleArchive = await Assert.ThrowsAsync<ApiException>(() => catalog.ArchiveProductAsync(
            productId, new ArchiveProductRequest(1, "attempt with an old version")));
        Assert.True(staleArchive.IsConflict);

        var archived = await catalog.ArchiveProductAsync(productId, new ArchiveProductRequest(renamed.Product.Version, "range withdrawn by the buyer"));

        Assert.False(archived.Product.IsActive);
        Assert.All(archived.Variants, variant => Assert.False(variant.IsActive));

        var secondArchive = await catalog.ArchiveProductAsync(
            productId, new ArchiveProductRequest(archived.Product.Version, "same decision repeated"));
        Assert.False(secondArchive.Product.IsActive);

        // 4. History is intact and future selection follows the active flag.
        var afterIssue = await ReadInvoiceAsync(owner, invoiceId);
        Assert.Equal(beforeIssue.Invoice.TotalAmount, afterIssue.Invoice.TotalAmount);
        Assert.Equal(beforeIssue.Invoice.Status, afterIssue.Invoice.Status);
        Assert.Equal(beforeIssue.Lines[0].VariantId, afterIssue.Lines[0].VariantId);
        Assert.Equal(beforeIssue.Lines[0].UnitPrice, afterIssue.Lines[0].UnitPrice);
        Assert.Equal(ledgerBeforeArchive, await LedgerRowsAsync(variantId));

        var newLine = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "T17 after the archive",
            [new InvoiceLineRequest(variantId, null, 1m, 60m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, newLine.StatusCode);
        Assert.Contains("inactive", (await newLine.Content.ReadAsStringAsync()).ToLowerInvariant(), StringComparison.Ordinal);

        // The record is reachable for history but gone from the default list, exactly as a retired customer is.
        var activeOnly = await catalog.GetProductsAsync(search: sku, includeInactive: false);
        Assert.DoesNotContain(activeOnly.Items, row => row.Id == productId);

        var withInactive = await catalog.GetProductsAsync(search: sku, includeInactive: true);
        Assert.Contains(withInactive.Items, row => row.Id == productId && !row.IsActive);

        // 5. The devices were told: the last change for this product is a tombstone, so a cached price list drops it.
        var kinds = await _fixture.WithFreshContextAsync(db => db.SyncChanges.AsNoTracking()
            .Where(change => change.BusinessId == _fixture.BusinessA.BusinessId
                && change.EntityType == "product" && change.EntityId == productId)
            .OrderByDescending(change => change.Revision)
            .Select(change => change.Kind)
            .Take(1)
            .ToListAsync());

        Assert.Equal(SyncChangeKind.Tombstone, Assert.Single(kinds));

        // 6. Restoring the product does not invent a price: the retired default comes back, nothing else does.
        var restored = await catalog.RestoreProductAsync(
            productId, new RestoreProductRequest(secondArchive.Product.Version, "range reinstated for this season"));

        Assert.True(restored.Product.IsActive);
        var activeVariants = restored.Variants.Where(variant => variant.IsActive).ToArray();
        Assert.Single(activeVariants);
        Assert.True(activeVariants[0].IsDefault, "the restored variant must be the default the counter sells");
    }

    // ------------------------------------------------------------------ helpers

    private sealed record VariantAttempt(bool Ok, ProductDetailDto? Created, ApiException? Failure);

    /// <summary>One stock ledger row, in the shape the archive must never rewrite.</summary>
    private sealed record LedgerRow(MovementType Kind, decimal Quantity, decimal Value, string ReferenceType);

    private async Task<IReadOnlyList<LedgerRow>> LedgerRowsAsync(Guid variantId)
        => await _fixture.WithFreshContextAsync(db => db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.VariantId == variantId)
            .OrderBy(movement => movement.OccurredAtUtc)
            .ThenBy(movement => movement.Id)
            .Select(movement => new LedgerRow(movement.MovementType, movement.QuantityDelta, movement.ValueDelta, movement.ReferenceType!))
            .ToListAsync());

    private static async Task<VariantAttempt> TryVariant(
        ProductApiClient catalog, Guid productId, CreateProductVariantRequest request)
    {
        try
        {
            return new VariantAttempt(true, await catalog.CreateVariantAsync(productId, request), null);
        }
        catch (ApiException failure)
        {
            return new VariantAttempt(false, null, failure);
        }
    }

    private async Task<int> FeedCountAsync(Guid productId)
        => await _fixture.WithFreshContextAsync(db => db.SyncChanges.AsNoTracking().CountAsync(
            change => change.BusinessId == _fixture.BusinessA.BusinessId
                && change.EntityType == "product" && change.EntityId == productId));

    private async Task<Guid> CreateUnitAsync(HttpClient client, string name, string shortName)
    {
        var response = await client.PostAsJsonAsync("/api/v1/units", new SaveMasterDataRequest(name, shortName, AllowDecimals: false));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the unit could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCategoryAsync(HttpClient client, string name, bool retire)
    {
        var response = await client.PostAsJsonAsync("/api/v1/categories", new SaveMasterDataRequest(name));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the category could not be created: {(int)response.StatusCode} {body}");

        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        if (retire)
        {
            var retired = await client.DeleteAsync($"/api/v1/categories/{id}");
            Assert.True(retired.IsSuccessStatusCode, $"the category could not be retired: {(int)retired.StatusCode}");
        }

        return id;
    }

    /// <summary>
    /// The protected state this task must respect, recorded the way the product records it: one movement posted
    /// through the inventory API, which moves the balance row in the same transaction (the database forbids any
    /// other order).
    /// </summary>
    private async Task RecordMovementAsync(HttpClient client, Guid variantId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/inventory/movements", new RecordMovementRequest(
            _fixture.BusinessA.LocationId,
            VariantId: variantId,
            MovementType: StockMovementKind.StockAdjustment,
            QuantityDelta: 24m,
            UnitCost: 30m,
            ReferenceType: "t17_setup",
            ReferenceId: Guid.NewGuid(),
            Memo: "task 17 protected-semantics setup"), ContractJson.Options);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the movement could not be recorded: {(int)response.StatusCode} {body}");
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers",
            new QtsBilling.Contracts.Customers.SaveCustomerRequest(name, null, null, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"the customer could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<InvoiceDetailDto> ReadInvoiceAsync(HttpClient client, Guid invoiceId)
    {
        var response = await client.GetAsync($"/api/v1/invoices/{invoiceId}");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InvoiceDetailDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the invoice answered without a body");
    }
}
