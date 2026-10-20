using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

public sealed class MasterDataEndpointTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MasterDataEndpointTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Catalog_master_data_crud_is_business_scoped_and_projects_changes_atomically()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        var unitResponse = await client.PostAsJsonAsync("/api/v1/units", new
        {
            name = "Case",
            shortName = "cs",
            allowDecimals = false,
        });
        var unitBody = await unitResponse.Content.ReadAsStringAsync();
        Assert.True(unitResponse.StatusCode == HttpStatusCode.Created, unitBody);
        using var unitJson = System.Text.Json.JsonDocument.Parse(unitBody);
        var unitId = unitJson.RootElement.GetProperty("id").GetGuid();
        var updateUnit = await client.PutAsJsonAsync($"/api/v1/units/{unitId}", new
        {
            name = "Cases",
            shortName = "case",
            allowDecimals = true,
        });
        Assert.Equal(HttpStatusCode.OK, updateUnit.StatusCode);

        var referencedUnitDelete = await client.DeleteAsync($"/api/v1/units/{_fixture.BusinessA.UnitId}");
        Assert.Equal(HttpStatusCode.Conflict, referencedUnitDelete.StatusCode);
        var unitDelete = await client.DeleteAsync($"/api/v1/units/{unitId}");
        Assert.Equal(HttpStatusCode.NoContent, unitDelete.StatusCode);

        var parentResponse = await client.PostAsJsonAsync("/api/v1/categories", new { name = "Drinks" });
        var parentBody = await parentResponse.Content.ReadAsStringAsync();
        Assert.True(parentResponse.StatusCode == HttpStatusCode.Created, parentBody);
        using var parentJson = System.Text.Json.JsonDocument.Parse(parentBody);
        var parentId = parentJson.RootElement.GetProperty("id").GetGuid();
        var childResponse = await client.PostAsJsonAsync("/api/v1/categories", new { name = "Water", parentId });
        var childBody = await childResponse.Content.ReadAsStringAsync();
        Assert.True(childResponse.StatusCode == HttpStatusCode.Created, childBody);
        using var childJson = System.Text.Json.JsonDocument.Parse(childBody);
        var childId = childJson.RootElement.GetProperty("id").GetGuid();

        var parentDelete = await client.DeleteAsync($"/api/v1/categories/{parentId}");
        Assert.Equal(HttpStatusCode.NoContent, parentDelete.StatusCode);
        var childDelete = await client.DeleteAsync($"/api/v1/categories/{childId}");
        Assert.Equal(HttpStatusCode.NoContent, childDelete.StatusCode);

        var brandResponse = await client.PostAsJsonAsync("/api/v1/brands", new { name = "North Star" });
        Assert.Equal(HttpStatusCode.Created, brandResponse.StatusCode);
        using var brandJson = System.Text.Json.JsonDocument.Parse(await brandResponse.Content.ReadAsStringAsync());
        var brandId = brandJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/brands/{brandId}")).StatusCode);

        var groupResponse = await client.PostAsJsonAsync("/api/v1/product-groups", new { name = "Seasonal" });
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        using var groupJson = System.Text.Json.JsonDocument.Parse(await groupResponse.Content.ReadAsStringAsync());
        var groupId = groupJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/product-groups/{groupId}")).StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var parent = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == parentId);
            Assert.False(parent.IsActive);
            Assert.DoesNotContain(await db.Categories.AsNoTracking().ToListAsync(), category => category.Id == childId);
            Assert.DoesNotContain(await db.Brands.AsNoTracking().ToListAsync(), brand => brand.Id == brandId);
            Assert.DoesNotContain(await db.ProductGroups.AsNoTracking().ToListAsync(), group => group.Id == groupId);
            Assert.True(await db.SyncChanges.AnyAsync(change => change.BusinessId == _fixture.BusinessA.BusinessId
                && change.EntityType == "category" && change.EntityId == parentId));
            Assert.True(await db.SyncChanges.AnyAsync(change => change.BusinessId == _fixture.BusinessA.BusinessId
                && change.EntityType == "unit" && change.EntityId == unitId && change.Kind.ToString() == "Tombstone"));
        });
    }

    [Fact]
    public async Task Supplier_master_data_supports_business_scoped_crud_and_soft_delete()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var suffix = Guid.NewGuid().ToString("N");
        var create = await client.PostAsJsonAsync("/api/v1/suppliers", new
        {
            name = $"Master data supplier {suffix}",
            email = $"master-data-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
            address = "Doha",
        });
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, createBody);
        using var created = System.Text.Json.JsonDocument.Parse(createBody);
        var supplierId = created.RootElement.GetProperty("id").GetGuid();

        var get = await client.GetAsync($"/api/v1/suppliers/{supplierId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var update = await client.PutAsJsonAsync($"/api/v1/suppliers/{supplierId}", new
        {
            name = $"Updated master supplier {suffix}",
            email = $"updated-{suffix}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
            address = "Al Rayyan",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/suppliers/{supplierId}")).StatusCode);

        using var inactive = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync($"/api/v1/suppliers/{supplierId}")).Content.ReadAsStringAsync());
        Assert.False(inactive.RootElement.GetProperty("isActive").GetBoolean());

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.True(await db.Suppliers.AnyAsync(supplier => supplier.BusinessId == _fixture.BusinessA.BusinessId
                && supplier.Id == supplierId && !supplier.IsActive));
            Assert.False(await db.Suppliers.AnyAsync(supplier => supplier.BusinessId == _fixture.BusinessB.BusinessId
                && supplier.Id == supplierId));
        });
    }
}
