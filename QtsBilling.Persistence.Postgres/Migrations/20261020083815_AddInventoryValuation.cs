using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryValuation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The perpetual valuation of the stock: the value on hand, the moving average cost and the
            // authoritative value of every movement (specification D06, finding F15).
            migrationBuilder.AddColumn<decimal>(
                name: "value_on_hand",
                table: "inventory_balances",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "average_unit_cost",
                table: "inventory_balances",
                type: "numeric(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "value_delta",
                table: "inventory_movements",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            // The stock that already exists carries no value: the catalog purchase price is not a costing
            // engine, and the value of the historical stock has to be approved and opened explicitly.
            // Until then, consuming it is refused with VALUATION_REQUIRED instead of being costed at
            // zero (specification D06: reject an unvalued tracked-stock issue).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "value_on_hand",
                table: "inventory_balances");

            migrationBuilder.DropColumn(
                name: "average_unit_cost",
                table: "inventory_balances");

            migrationBuilder.DropColumn(
                name: "value_delta",
                table: "inventory_movements");
        }
    }
}
