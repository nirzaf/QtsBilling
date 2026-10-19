using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedProductTaxMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tax_mode",
                table: "cached_products",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Exclusive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_mode",
                table: "cached_products");
        }
    }
}
