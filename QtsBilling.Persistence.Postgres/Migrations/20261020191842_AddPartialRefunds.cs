using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_refunds_business_sale",
                table: "refunds");

            migrationBuilder.AlterColumn<decimal>(
                name: "quantity",
                table: "refund_lines",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AddColumn<string>(
                name: "disposition",
                table: "refund_lines",
                type: "text",
                maxLength: 32,
                nullable: false,
                defaultValue: "Restock");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_business_sale",
                table: "refunds",
                columns: new[] { "business_id", "sale_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_lines_disposition",
                table: "refund_lines",
                sql: "disposition IN ('Restock', 'DamagedWriteOff')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_refunds_business_sale",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_lines_disposition",
                table: "refund_lines");

            migrationBuilder.DropColumn(
                name: "disposition",
                table: "refund_lines");

            migrationBuilder.AlterColumn<decimal>(
                name: "quantity",
                table: "refund_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.CreateIndex(
                name: "ux_refunds_business_sale",
                table: "refunds",
                columns: new[] { "business_id", "sale_id" },
                unique: true);
        }
    }
}
