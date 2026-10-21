using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVariantSkuUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The backstop is only safe if it is true already. Two variants of one product can share an SKU in a
            // database written before this rule existed, and creating a unique index would then fail with a
            // constraint name an operator cannot act on - so the duplicates are named first and the migration
            // refuses, leaving the data untouched for a deliberate resolution (D10: nothing here rewrites history).
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicates text;
                BEGIN
                    SELECT string_agg(named.line, '; ' ORDER BY named.business_id, named.sku)
                    INTO duplicates
                    FROM (
                        SELECT format(
                                   'business %s SKU %s (%s variants)',
                                   business_id, sku, count(*)) AS line,
                               business_id,
                               sku
                        FROM product_variants
                        WHERE sku IS NOT NULL
                        GROUP BY business_id, sku
                        HAVING count(*) > 1
                    ) named;

                    IF duplicates IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Refusing to make variant SKUs unique: duplicate SKUs exist -> %',
                            duplicates
                            USING HINT = 'Resolve each duplicate SKU on the variants themselves, or give one of them a new SKU, then migrate again.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_business_id_sku",
                table: "product_variants",
                columns: new[] { "business_id", "sku" },
                unique: true,
                filter: "\"sku\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_product_variants_business_id_sku",
                table: "product_variants");
        }
    }
}
