using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleIdempotencyReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "sales",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_hash",
                table: "sales",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_business_idempotency_key",
                table: "sales",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sales_business_idempotency_key",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "payload_hash",
                table: "sales");
        }
    }
}
