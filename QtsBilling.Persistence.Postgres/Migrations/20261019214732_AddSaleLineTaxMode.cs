using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleLineTaxMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tax_mode",
                table: "sale_lines",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Exclusive");

            // The posted lines keep the tax composition they were priced with: it is read back from the
            // product the line refers to, so a historical document is never rewritten and no value is
            // invented (specification D04: additive migrations, no silent repair).
            migrationBuilder.Sql("""
                UPDATE sale_lines AS line
                SET tax_mode = product.tax_type
                FROM products AS product
                WHERE product.id = line.variant_id
                  AND product.business_id = line.business_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_mode",
                table: "sale_lines");
        }
    }
}
