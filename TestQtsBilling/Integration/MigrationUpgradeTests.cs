using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The upgrade of a database that already holds posted sales (specification task 01, D17 "migrations").
/// </summary>
/// <remarks>
/// A migration that adds a snapshot column to an append-only table must not guess the historical
/// value. The test writes the rows of the *previous* schema first - including a tax-inclusive sale
/// whose product and variant have different identifiers - and only then runs the migration. A backfill
/// that reads the current catalog (or that joins the wrong table) fails here.
/// </remarks>
public sealed class MigrationUpgradeTests : IClassFixture<PostgresApiFixture>
{
    /// <summary>The migration that was the head before the tax composition was recorded.</summary>
    private const string PreviousHead = "20261019131237_HardenPurchaseOrderLineReceiptGuard";

    private readonly PostgresApiFixture _fixture;

    public MigrationUpgradeTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_existing_tax_inclusive_sale_keeps_its_composition_after_the_upgrade()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(PreviousHead);

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var inclusiveLineId = Guid.CreateVersion7();
        var exclusiveLineId = Guid.CreateVersion7();

        // The product and the variant carry different identifiers on purpose: a backfill that joins the
        // sale line to the product table matches nothing.
        await database.ExecuteAsync($"""
            insert into businesses (id, name, legal_name, base_currency, time_zone, accounting_method, is_active, version, created_at_utc, updated_at_utc)
            values ('{businessId}', 'Upgrade business', 'Upgrade business', 'QAR', 'Asia/Qatar', 'Fifo', true, 1, now(), now());

            insert into business_locations (id, business_id, name, code, is_active, version, created_at_utc, updated_at_utc)
            values ('{locationId}', '{businessId}', 'Main', 'MAIN', true, 1, now(), now());

            insert into products (id, business_id, name, sku, tax_type, product_type, track_stock, is_active, version, created_at_utc, updated_at_utc)
            values ('{productId}', '{businessId}', 'Inclusive product', 'UPG-INCL', 'Inclusive', 'Single', false, true, 1, now(), now());

            insert into product_variants (id, business_id, product_id, name, sku, selling_price, purchase_price, is_default, is_active, version, created_at_utc, updated_at_utc)
            values ('{variantId}', '{businessId}', '{productId}', 'Inclusive variant', 'UPG-INCL-V', 115.0000, 60.0000, true, true, 1, now(), now());

            insert into sales (id, business_id, location_id, sale_number, status, currency, subtotal_amount, discount_amount, tax_amount, total_amount, payment_status, posted_at_utc, version, created_at_utc, updated_at_utc)
            values ('{saleId}', '{businessId}', '{locationId}', 'S-UPG-1', 'Posted', 'QAR', 215.0000, 0.0000, 15.0000, 230.0000, 'Confirmed', now(), 1, now(), now());

            -- The inclusive line: 115 at 15% inclusive is 15 of tax inside the 115.
            insert into sale_lines (id, business_id, sale_id, line_no, variant_id, product_name_snapshot, quantity, unit_price, discount_amount, tax_rate, tax_amount, line_total, unit_cost_snapshot, created_at_utc, updated_at_utc)
            values ('{inclusiveLineId}', '{businessId}', '{saleId}', 1, '{variantId}', 'Inclusive variant', 1.0000, 115.0000, 0.0000, 0.1500, 15.0000, 115.0000, 60.0000, now(), now());

            -- The exclusive line: 100 at 15% exclusive is 15 of tax on top of the 100.
            insert into sale_lines (id, business_id, sale_id, line_no, variant_id, product_name_snapshot, quantity, unit_price, discount_amount, tax_rate, tax_amount, line_total, unit_cost_snapshot, created_at_utc, updated_at_utc)
            values ('{exclusiveLineId}', '{businessId}', '{saleId}', 2, '{variantId}', 'Inclusive variant', 1.0000, 100.0000, 0.0000, 0.1500, 15.0000, 115.0000, 60.0000, now(), now());
            """);

        await database.MigrateToLatestAsync();

        // The composition is derived from the line's own numbers: the inclusive line is inclusive and
        // the exclusive line is exclusive, whatever the catalog says today.
        var inclusive = await database.ScalarAsync<string>(
            $"select tax_mode from sale_lines where id = '{inclusiveLineId}';");

        var exclusive = await database.ScalarAsync<string>(
            $"select tax_mode from sale_lines where id = '{exclusiveLineId}';");

        Assert.Equal("Inclusive", inclusive);
        Assert.Equal("Exclusive", exclusive);

        // The posted amounts are untouched: the upgrade never rewrites a financial fact.
        var amounts = await database.ScalarAsync<string>(
            $"select tax_amount || '/' || line_total from sale_lines where id = '{inclusiveLineId}';");

        Assert.Equal("15.0000/115.0000", amounts);
    }

    [Fact]
    public async Task An_upgraded_database_never_leaves_a_line_unclassified_without_saying_so()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(PreviousHead);

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var oddLineId = Guid.CreateVersion7();

        await database.ExecuteAsync($"""
            insert into businesses (id, name, legal_name, base_currency, time_zone, accounting_method, is_active, version, created_at_utc, updated_at_utc)
            values ('{businessId}', 'Upgrade business', 'Upgrade business', 'QAR', 'Asia/Qatar', 'Fifo', true, 1, now(), now());

            insert into business_locations (id, business_id, name, code, is_active, version, created_at_utc, updated_at_utc)
            values ('{locationId}', '{businessId}', 'Main', 'MAIN', true, 1, now(), now());

            insert into products (id, business_id, name, sku, tax_type, product_type, track_stock, is_active, version, created_at_utc, updated_at_utc)
            values ('{productId}', '{businessId}', 'Product', 'UPG-ODD', 'Exclusive', 'Single', false, true, 1, now(), now());

            insert into product_variants (id, business_id, product_id, name, sku, selling_price, purchase_price, is_default, is_active, version, created_at_utc, updated_at_utc)
            values ('{variantId}', '{businessId}', '{productId}', 'Variant', 'UPG-ODD-V', 100.0000, 60.0000, true, true, 1, now(), now());

            insert into sales (id, business_id, location_id, sale_number, status, currency, subtotal_amount, discount_amount, tax_amount, total_amount, payment_status, posted_at_utc, version, created_at_utc, updated_at_utc)
            values ('{saleId}', '{businessId}', '{locationId}', 'S-UPG-2', 'Posted', 'QAR', 100.0000, 0.0000, 7.0000, 107.0000, 'Confirmed', now(), 1, now(), now());

            -- The stored numbers match neither composition: the line must not be classified by guesswork.
            insert into sale_lines (id, business_id, sale_id, line_no, variant_id, product_name_snapshot, quantity, unit_price, discount_amount, tax_rate, tax_amount, line_total, unit_cost_snapshot, created_at_utc, updated_at_utc)
            values ('{oddLineId}', '{businessId}', '{saleId}', 1, '{variantId}', 'Variant', 1.0000, 100.0000, 0.0000, 0.1500, 7.0000, 107.0000, 60.0000, now(), now());
            """);

        await database.MigrateToLatestAsync();

        var mode = await database.ScalarAsync<string>(
            $"select tax_mode from sale_lines where id = '{oddLineId}';");

        // The line matches neither composition, so it is not classified: it says so instead of being
        // labelled by guesswork.
        Assert.Equal("Unspecified", mode);
    }
}
