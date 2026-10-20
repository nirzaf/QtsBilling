using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseTaxSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "expenses",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "tax_mode",
                table: "expenses",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Exclusive");

            migrationBuilder.AddColumn<decimal>(
                name: "tax_rate",
                table: "expenses",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "tax_mode",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "tax_rate",
                table: "expenses");
        }
    }
}
