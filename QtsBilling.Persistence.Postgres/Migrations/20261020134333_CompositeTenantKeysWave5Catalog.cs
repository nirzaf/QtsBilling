using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave5Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_categories_categories_parent_id",
                table: "categories");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_lines_product_variants_variant_id",
                table: "invoice_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_product_variants_products_product_id",
                table: "product_variants");

            migrationBuilder.DropForeignKey(
                name: "FK_products_brands_brand_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_categories_category_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_product_groups_product_group_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_tax_rates_tax_rate_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_units_unit_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_brand_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_category_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_product_group_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_tax_rate_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_unit_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_variant_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_categories_parent_id",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "IX_products_brand_id_business_id",
                table: "products",
                columns: new[] { "brand_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_category_id_business_id",
                table: "products",
                columns: new[] { "category_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_product_group_id_business_id",
                table: "products",
                columns: new[] { "product_group_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_tax_rate_id_business_id",
                table: "products",
                columns: new[] { "tax_rate_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_unit_id_business_id",
                table: "products",
                columns: new[] { "unit_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_product_id_business_id",
                table: "product_variants",
                columns: new[] { "product_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_variant_id_business_id",
                table: "invoice_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_parent_id_business_id",
                table: "categories",
                columns: new[] { "parent_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_categories_categories_parent_id_business_id",
                table: "categories",
                columns: new[] { "parent_id", "business_id" },
                principalTable: "categories",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_lines_product_variants_variant_id_business_id",
                table: "invoice_lines",
                columns: new[] { "variant_id", "business_id" },
                principalTable: "product_variants",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_variants_products_product_id_business_id",
                table: "product_variants",
                columns: new[] { "product_id", "business_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_brands_brand_id_business_id",
                table: "products",
                columns: new[] { "brand_id", "business_id" },
                principalTable: "brands",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_categories_category_id_business_id",
                table: "products",
                columns: new[] { "category_id", "business_id" },
                principalTable: "categories",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_groups_product_group_id_business_id",
                table: "products",
                columns: new[] { "product_group_id", "business_id" },
                principalTable: "product_groups",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_tax_rates_tax_rate_id_business_id",
                table: "products",
                columns: new[] { "tax_rate_id", "business_id" },
                principalTable: "tax_rates",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_units_unit_id_business_id",
                table: "products",
                columns: new[] { "unit_id", "business_id" },
                principalTable: "units",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_categories_categories_parent_id_business_id",
                table: "categories");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_lines_product_variants_variant_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_product_variants_products_product_id_business_id",
                table: "product_variants");

            migrationBuilder.DropForeignKey(
                name: "FK_products_brands_brand_id_business_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_categories_category_id_business_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_product_groups_product_group_id_business_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_tax_rates_tax_rate_id_business_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "FK_products_units_unit_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_brand_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_category_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_product_group_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_tax_rate_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_unit_id_business_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_product_variants_product_id_business_id",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_variant_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_categories_parent_id_business_id",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "IX_products_brand_id",
                table: "products",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_product_group_id",
                table: "products",
                column: "product_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_tax_rate_id",
                table: "products",
                column: "tax_rate_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_unit_id",
                table: "products",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_variant_id",
                table: "invoice_lines",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "IX_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.AddForeignKey(
                name: "FK_categories_categories_parent_id",
                table: "categories",
                column: "parent_id",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_lines_product_variants_variant_id",
                table: "invoice_lines",
                column: "variant_id",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_variants_products_product_id",
                table: "product_variants",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_brands_brand_id",
                table: "products",
                column: "brand_id",
                principalTable: "brands",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_categories_category_id",
                table: "products",
                column: "category_id",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_groups_product_group_id",
                table: "products",
                column: "product_group_id",
                principalTable: "product_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_tax_rates_tax_rate_id",
                table: "products",
                column: "tax_rate_id",
                principalTable: "tax_rates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_units_unit_id",
                table: "products",
                column: "unit_id",
                principalTable: "units",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
