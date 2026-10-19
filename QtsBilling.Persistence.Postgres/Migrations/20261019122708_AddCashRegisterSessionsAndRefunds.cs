using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCashRegisterSessionsAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "cash_register_session_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cash_register_session_id",
                table: "cash_register_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cash_register_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    opened_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    variance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    closing_note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_register_sessions", x => x.id);
                    table.UniqueConstraint("AK_cash_register_sessions_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_cash_register_sessions_amounts", "opening_float >= 0 AND (counted_cash IS NULL OR counted_cash >= 0)");
                    table.ForeignKey(
                        name: "FK_cash_register_sessions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_register_sessions_cash_registers_cash_register_id_busi~",
                        columns: x => new { x.cash_register_id, x.business_id },
                        principalTable: "cash_registers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_register_sessions_users_closed_by_user_id",
                        column: x => x.closed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_register_sessions_users_opened_by_user_id",
                        column: x => x.opened_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "text", maxLength: 400, nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    cash_register_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refunds", x => x.id);
                    table.UniqueConstraint("AK_refunds_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_refunds_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "FK_refunds_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refunds_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refunds_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refunds_sales_sale_id_business_id",
                        columns: x => new { x.sale_id, x.business_id },
                        principalTable: "sales",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refunds_users_posted_by_user_id",
                        column: x => x.posted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_lines", x => x.id);
                    table.CheckConstraint("ck_refund_lines_amounts", "quantity > 0 AND unit_price >= 0");
                    table.ForeignKey(
                        name: "FK_refund_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refund_lines_refunds_refund_id_business_id",
                        columns: x => new { x.refund_id, x.business_id },
                        principalTable: "refunds",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_refund_lines_sale_lines_sale_line_id",
                        column: x => x.sale_line_id,
                        principalTable: "sale_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payments_business_id_cash_register_session_id",
                table: "payments",
                columns: new[] { "business_id", "cash_register_session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_cash_register_session_id",
                table: "payments",
                column: "cash_register_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_business_id_cash_register_session_id",
                table: "cash_register_movements",
                columns: new[] { "business_id", "cash_register_session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_cash_register_session_id",
                table: "cash_register_movements",
                column: "cash_register_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_sessions_business_id_cash_register_id_opened_~",
                table: "cash_register_sessions",
                columns: new[] { "business_id", "cash_register_id", "opened_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_sessions_cash_register_id_business_id",
                table: "cash_register_sessions",
                columns: new[] { "cash_register_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_sessions_closed_by_user_id",
                table: "cash_register_sessions",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_sessions_opened_by_user_id",
                table: "cash_register_sessions",
                column: "opened_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_cash_register_sessions_open",
                table: "cash_register_sessions",
                columns: new[] { "business_id", "cash_register_id" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_business_id",
                table: "refund_lines",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_refund_id_business_id",
                table: "refund_lines",
                columns: new[] { "refund_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_refund_id_line_no",
                table: "refund_lines",
                columns: new[] { "refund_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refund_lines_sale_line_id",
                table: "refund_lines",
                column: "sale_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_business_id_refund_number",
                table: "refunds",
                columns: new[] { "business_id", "refund_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refunds_journal_entry_id",
                table: "refunds",
                column: "journal_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_location_id_business_id",
                table: "refunds",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refunds_posted_by_user_id",
                table: "refunds",
                column: "posted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_sale_id_business_id",
                table: "refunds",
                columns: new[] { "sale_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_refunds_business_idempotency_key",
                table: "refunds",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_refunds_business_sale",
                table: "refunds",
                columns: new[] { "business_id", "sale_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements",
                column: "cash_register_session_id",
                principalTable: "cash_register_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id",
                table: "payments",
                column: "cash_register_session_id",
                principalTable: "cash_register_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id",
                table: "payments");

            migrationBuilder.DropTable(
                name: "cash_register_sessions");

            migrationBuilder.DropTable(
                name: "refund_lines");

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropIndex(
                name: "IX_payments_business_id_cash_register_session_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_cash_register_session_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_business_id_cash_register_session_id",
                table: "cash_register_movements");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_cash_register_session_id",
                table: "cash_register_movements");

            migrationBuilder.DropColumn(
                name: "cash_register_session_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "cash_register_session_id",
                table: "cash_register_movements");
        }
    }
}
