using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave1Sales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_register_movements_sales_sale_id",
                table: "cash_register_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_sales_source_sale_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_sales_sale_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_refund_lines_sale_lines_sale_line_id",
                table: "refund_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_sales_sales_reversal_of_sale_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_sales_reversal_of_sale_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_refund_lines_sale_line_id",
                table: "refund_lines");

            migrationBuilder.DropIndex(
                name: "IX_payments_sale_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_source_sale_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_sale_id",
                table: "cash_register_movements");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_sale_lines_id_business_id",
                table: "sale_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_reversal_of_sale_id_business_id",
                table: "sales",
                columns: new[] { "reversal_of_sale_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_sale_line_id_business_id",
                table: "refund_lines",
                columns: new[] { "sale_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_sale_id_business_id",
                table: "payments",
                columns: new[] { "sale_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_source_sale_id_business_id",
                table: "invoices",
                columns: new[] { "source_sale_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_sale_id_business_id",
                table: "cash_register_movements",
                columns: new[] { "sale_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_cash_register_movements_sales_sale_id_business_id",
                table: "cash_register_movements",
                columns: new[] { "sale_id", "business_id" },
                principalTable: "sales",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_sales_source_sale_id_business_id",
                table: "invoices",
                columns: new[] { "source_sale_id", "business_id" },
                principalTable: "sales",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_sales_sale_id_business_id",
                table: "payments",
                columns: new[] { "sale_id", "business_id" },
                principalTable: "sales",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refund_lines_sale_lines_sale_line_id_business_id",
                table: "refund_lines",
                columns: new[] { "sale_line_id", "business_id" },
                principalTable: "sale_lines",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_sales_reversal_of_sale_id_business_id",
                table: "sales",
                columns: new[] { "reversal_of_sale_id", "business_id" },
                principalTable: "sales",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_register_movements_sales_sale_id_business_id",
                table: "cash_register_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_sales_source_sale_id_business_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_sales_sale_id_business_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_refund_lines_sale_lines_sale_line_id_business_id",
                table: "refund_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_sales_sales_reversal_of_sale_id_business_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_sales_reversal_of_sale_id_business_id",
                table: "sales");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_sale_lines_id_business_id",
                table: "sale_lines");

            migrationBuilder.DropIndex(
                name: "IX_refund_lines_sale_line_id_business_id",
                table: "refund_lines");

            migrationBuilder.DropIndex(
                name: "IX_payments_sale_id_business_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_source_sale_id_business_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_sale_id_business_id",
                table: "cash_register_movements");

            migrationBuilder.CreateIndex(
                name: "IX_sales_reversal_of_sale_id",
                table: "sales",
                column: "reversal_of_sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_sale_line_id",
                table: "refund_lines",
                column: "sale_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_sale_id",
                table: "payments",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_source_sale_id",
                table: "invoices",
                column: "source_sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_sale_id",
                table: "cash_register_movements",
                column: "sale_id");

            migrationBuilder.AddForeignKey(
                name: "FK_cash_register_movements_sales_sale_id",
                table: "cash_register_movements",
                column: "sale_id",
                principalTable: "sales",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_sales_source_sale_id",
                table: "invoices",
                column: "source_sale_id",
                principalTable: "sales",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_sales_sale_id",
                table: "payments",
                column: "sale_id",
                principalTable: "sales",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refund_lines_sale_lines_sale_line_id",
                table: "refund_lines",
                column: "sale_line_id",
                principalTable: "sale_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_sales_reversal_of_sale_id",
                table: "sales",
                column: "reversal_of_sale_id",
                principalTable: "sales",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
