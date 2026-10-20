using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave4Parties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expenses_suppliers_supplier_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_customers_customer_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_invoices_invoice_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_purchase_orders_suppliers_supplier_id",
                table: "purchase_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_sales_customers_customer_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_sales_customer_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_supplier_id",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_payments_invoice_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_customer_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_supplier_id",
                table: "expenses");

            migrationBuilder.CreateIndex(
                name: "IX_sales_customer_id_business_id",
                table: "sales",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_supplier_id_business_id",
                table: "purchase_orders",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_invoice_id_business_id",
                table: "payments",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_id_business_id",
                table: "invoices",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_supplier_id_business_id",
                table: "expenses",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_suppliers_supplier_id_business_id",
                table: "expenses",
                columns: new[] { "supplier_id", "business_id" },
                principalTable: "suppliers",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_customers_customer_id_business_id",
                table: "invoices",
                columns: new[] { "customer_id", "business_id" },
                principalTable: "customers",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_invoices_invoice_id_business_id",
                table: "payments",
                columns: new[] { "invoice_id", "business_id" },
                principalTable: "invoices",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchase_orders_suppliers_supplier_id_business_id",
                table: "purchase_orders",
                columns: new[] { "supplier_id", "business_id" },
                principalTable: "suppliers",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_customers_customer_id_business_id",
                table: "sales",
                columns: new[] { "customer_id", "business_id" },
                principalTable: "customers",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expenses_suppliers_supplier_id_business_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_customers_customer_id_business_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_invoices_invoice_id_business_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_purchase_orders_suppliers_supplier_id_business_id",
                table: "purchase_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_sales_customers_customer_id_business_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_sales_customer_id_business_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_supplier_id_business_id",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_payments_invoice_id_business_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_customer_id_business_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_supplier_id_business_id",
                table: "expenses");

            migrationBuilder.CreateIndex(
                name: "IX_sales_customer_id",
                table: "sales",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_supplier_id",
                table: "purchase_orders",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_invoice_id",
                table: "payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_id",
                table: "invoices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_supplier_id",
                table: "expenses",
                column: "supplier_id");

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_suppliers_supplier_id",
                table: "expenses",
                column: "supplier_id",
                principalTable: "suppliers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_customers_customer_id",
                table: "invoices",
                column: "customer_id",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_invoices_invoice_id",
                table: "payments",
                column: "invoice_id",
                principalTable: "invoices",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchase_orders_suppliers_supplier_id",
                table: "purchase_orders",
                column: "supplier_id",
                principalTable: "suppliers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_customers_customer_id",
                table: "sales",
                column: "customer_id",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
