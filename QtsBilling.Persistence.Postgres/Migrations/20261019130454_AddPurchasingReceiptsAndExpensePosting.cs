using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchasingReceiptsAndExpensePosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "expenses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_hash",
                table: "expenses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "account_id",
                table: "expense_categories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_purchase_order_lines_id_business_id",
                table: "purchase_order_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateTable(
                name: "expense_reversals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", maxLength: 400, nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reversed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    reversed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_reversals", x => x.id);
                    table.UniqueConstraint("AK_expense_reversals_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "FK_expense_reversals_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expense_reversals_expenses_expense_id_business_id",
                        columns: x => new { x.expense_id, x.business_id },
                        principalTable: "expenses",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expense_reversals_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expense_reversals_users_reversed_by_user_id",
                        column: x => x.reversed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_receipts", x => x.id);
                    table.UniqueConstraint("AK_purchase_receipts_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "FK_purchase_receipts_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipts_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipts_purchase_orders_purchase_order_id_busines~",
                        columns: x => new { x.purchase_order_id, x.business_id },
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipts_users_received_by_user_id",
                        column: x => x.received_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_receipt_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    purchase_order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_receipt_lines", x => x.id);
                    table.CheckConstraint("ck_purchase_receipt_lines_quantities", "quantity > 0 AND unit_cost >= 0");
                    table.ForeignKey(
                        name: "FK_purchase_receipt_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipt_lines_product_variants_variant_id_business~",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipt_lines_purchase_order_lines_purchase_order_~",
                        columns: x => new { x.purchase_order_line_id, x.business_id },
                        principalTable: "purchase_order_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_receipt_lines_purchase_receipts_purchase_receipt_i~",
                        columns: x => new { x.purchase_receipt_id, x.business_id },
                        principalTable: "purchase_receipts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_expenses_business_idempotency_key",
                table: "expenses",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_expense_categories_account_id",
                table: "expense_categories",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reversals_expense_id_business_id",
                table: "expense_reversals",
                columns: new[] { "expense_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_reversals_journal_entry_id",
                table: "expense_reversals",
                column: "journal_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reversals_reversed_by_user_id",
                table: "expense_reversals",
                column: "reversed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_expense_reversals_business_expense",
                table: "expense_reversals",
                columns: new[] { "business_id", "expense_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_expense_reversals_business_idempotency_key",
                table: "expense_reversals",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipt_lines_business_id_variant_id",
                table: "purchase_receipt_lines",
                columns: new[] { "business_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipt_lines_purchase_order_line_id_business_id",
                table: "purchase_receipt_lines",
                columns: new[] { "purchase_order_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipt_lines_purchase_receipt_id_business_id",
                table: "purchase_receipt_lines",
                columns: new[] { "purchase_receipt_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipt_lines_purchase_receipt_id_line_no",
                table: "purchase_receipt_lines",
                columns: new[] { "purchase_receipt_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipt_lines_variant_id_business_id",
                table: "purchase_receipt_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_business_id_purchase_order_id",
                table: "purchase_receipts",
                columns: new[] { "business_id", "purchase_order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_business_id_receipt_number",
                table: "purchase_receipts",
                columns: new[] { "business_id", "receipt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_location_id_business_id",
                table: "purchase_receipts",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_purchase_order_id_business_id",
                table: "purchase_receipts",
                columns: new[] { "purchase_order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_received_by_user_id",
                table: "purchase_receipts",
                column: "received_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_purchase_receipts_business_idempotency_key",
                table: "purchase_receipts",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_expense_categories_accounts_account_id",
                table: "expense_categories",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expense_categories_accounts_account_id",
                table: "expense_categories");

            migrationBuilder.DropTable(
                name: "expense_reversals");

            migrationBuilder.DropTable(
                name: "purchase_receipt_lines");

            migrationBuilder.DropTable(
                name: "purchase_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_purchase_order_lines_id_business_id",
                table: "purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "ux_expenses_business_idempotency_key",
                table: "expenses");

            migrationBuilder.DropIndex(
                name: "IX_expense_categories_account_id",
                table: "expense_categories");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "payload_hash",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "expense_categories");
        }
    }
}
