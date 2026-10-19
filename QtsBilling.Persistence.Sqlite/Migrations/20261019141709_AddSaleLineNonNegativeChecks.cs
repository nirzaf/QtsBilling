using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleLineNonNegativeChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_local_sale_lines_discount_non_negative",
                table: "local_sale_lines",
                sql: "discount_scaled >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_local_sale_lines_tax_non_negative",
                table: "local_sale_lines",
                sql: "tax_scaled >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_local_sale_lines_unit_price_non_negative",
                table: "local_sale_lines",
                sql: "unit_price_scaled >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_local_sale_lines_discount_non_negative",
                table: "local_sale_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_local_sale_lines_tax_non_negative",
                table: "local_sale_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_local_sale_lines_unit_price_non_negative",
                table: "local_sale_lines");
        }
    }
}
