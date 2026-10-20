using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_order_id",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_payload_hash",
                table: "invoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_request_key",
                table: "invoices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "revenue_base_amount",
                table: "invoice_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "source_delivery_line_id",
                table: "invoice_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_mode",
                table: "invoice_lines",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Exclusive");

            migrationBuilder.AddColumn<Guid>(
                name: "delivered_not_invoiced_account_id",
                table: "business_accounting_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inventory_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reservations", x => x.id);
                    table.UniqueConstraint("AK_inventory_reservations_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_inventory_reservations_key", "length(idempotency_key) BETWEEN 1 AND 128 AND length(payload_hash) = 64");
                    table.ForeignKey(
                        name: "FK_inventory_reservations_business_locations_location_id_busin~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservations_sales_orders_order_id_business_id",
                        columns: x => new { x.order_id, x.business_id },
                        principalTable: "sales_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivery_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_deliveries", x => x.id);
                    table.UniqueConstraint("AK_sales_order_deliveries_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_order_deliveries_key", "length(delivery_number) BETWEEN 1 AND 64 AND length(idempotency_key) BETWEEN 1 AND 128 AND length(payload_hash) = 64");
                    table.ForeignKey(
                        name: "FK_sales_order_deliveries_business_locations_location_id_busin~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_deliveries_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_deliveries_journal_entries_journal_entry_id_bus~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_deliveries_sales_orders_order_id_business_id",
                        columns: x => new { x.order_id, x.business_id },
                        principalTable: "sales_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_deliveries_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_reservation_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity_reserved = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_consumed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_released = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reservation_lines", x => x.id);
                    table.UniqueConstraint("AK_inventory_reservation_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_inventory_reservation_lines_quantities", "quantity_reserved > 0 AND quantity_consumed >= 0 AND quantity_released >= 0 AND quantity_consumed + quantity_released <= quantity_reserved");
                    table.ForeignKey(
                        name: "FK_inventory_reservation_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservation_lines_inventory_reservations_reservat~",
                        columns: x => new { x.reservation_id, x.business_id },
                        principalTable: "inventory_reservations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservation_lines_product_variants_variant_id_bus~",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_reservation_lines_sales_order_lines_order_line_id~",
                        columns: x => new { x.order_line_id, x.business_id },
                        principalTable: "sales_order_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_delivery_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    cost_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    track_stock_snapshot = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_delivery_lines", x => x.id);
                    table.UniqueConstraint("AK_sales_order_delivery_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_order_delivery_lines_values", "quantity > 0 AND unit_cost >= 0 AND cost_value >= 0 AND (track_stock_snapshot OR cost_value = 0)");
                    table.ForeignKey(
                        name: "FK_sales_order_delivery_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_delivery_lines_inventory_reservation_lines_rese~",
                        columns: x => new { x.reservation_line_id, x.business_id },
                        principalTable: "inventory_reservation_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_delivery_lines_product_variants_variant_id_busi~",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_delivery_lines_sales_order_deliveries_delivery_~",
                        columns: x => new { x.delivery_id, x.business_id },
                        principalTable: "sales_order_deliveries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_delivery_lines_sales_order_lines_order_line_id_~",
                        columns: x => new { x.order_line_id, x.business_id },
                        principalTable: "sales_order_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_invoice_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    cost_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_invoice_allocations", x => x.id);
                    table.UniqueConstraint("AK_delivery_invoice_allocations_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_delivery_invoice_allocations_values", "quantity > 0 AND cost_value >= 0");
                    table.ForeignKey(
                        name: "FK_delivery_invoice_allocations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_invoice_allocations_invoice_lines_invoice_line_id_~",
                        columns: x => new { x.invoice_line_id, x.business_id },
                        principalTable: "invoice_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_invoice_allocations_journal_entries_journal_entry_~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_invoice_allocations_sales_order_delivery_lines_del~",
                        columns: x => new { x.delivery_line_id, x.business_id },
                        principalTable: "sales_order_delivery_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO accounts (id, business_id, code, name, account_type, is_active, created_at_utc, updated_at_utc, version)
                SELECT gen_random_uuid(), businesses.id, '1250', 'Delivered Goods Not Invoiced', 'Asset', TRUE, now(), now(), 1
                FROM businesses
                WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE accounts.business_id = businesses.id AND accounts.code = '1250');

                UPDATE business_accounting_settings AS settings
                SET delivered_not_invoiced_account_id = accounts.id
                FROM accounts
                WHERE accounts.business_id = settings.business_id AND accounts.code = '1250';

                UPDATE invoice_lines
                SET revenue_base_amount = ROUND(quantity * unit_price - discount_amount, 4), tax_mode = 'Exclusive';
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "delivered_not_invoiced_account_id",
                table: "business_accounting_settings",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_business_id_source_request_key",
                table: "invoices",
                columns: new[] { "business_id", "source_request_key" },
                unique: true,
                filter: "source_request_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_source_order_id_business_id",
                table: "invoices",
                columns: new[] { "source_order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_business_id_source_delivery_line_id",
                table: "invoice_lines",
                columns: new[] { "business_id", "source_delivery_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_source_delivery_line_id_business_id",
                table: "invoice_lines",
                columns: new[] { "source_delivery_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_delivered_not_invoiced_account~",
                table: "business_accounting_settings",
                columns: new[] { "delivered_not_invoiced_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_invoice_allocations_business_id_delivery_line_id",
                table: "delivery_invoice_allocations",
                columns: new[] { "business_id", "delivery_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_invoice_allocations_business_id_invoice_line_id",
                table: "delivery_invoice_allocations",
                columns: new[] { "business_id", "invoice_line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_invoice_allocations_delivery_line_id_business_id",
                table: "delivery_invoice_allocations",
                columns: new[] { "delivery_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_invoice_allocations_invoice_line_id_business_id",
                table: "delivery_invoice_allocations",
                columns: new[] { "invoice_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_invoice_allocations_journal_entry_id_business_id",
                table: "delivery_invoice_allocations",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_business_id_order_line_id",
                table: "inventory_reservation_lines",
                columns: new[] { "business_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_business_id_variant_id",
                table: "inventory_reservation_lines",
                columns: new[] { "business_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_order_line_id_business_id",
                table: "inventory_reservation_lines",
                columns: new[] { "order_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_reservation_id_business_id",
                table: "inventory_reservation_lines",
                columns: new[] { "reservation_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_reservation_id_order_line_id",
                table: "inventory_reservation_lines",
                columns: new[] { "reservation_id", "order_line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_lines_variant_id_business_id",
                table: "inventory_reservation_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_business_id_idempotency_key",
                table: "inventory_reservations",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_business_id_order_id_status_expires_~",
                table: "inventory_reservations",
                columns: new[] { "business_id", "order_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_created_by_user_id",
                table: "inventory_reservations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_location_id_business_id",
                table: "inventory_reservations",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_order_id_business_id",
                table: "inventory_reservations",
                columns: new[] { "order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_business_id_delivery_number",
                table: "sales_order_deliveries",
                columns: new[] { "business_id", "delivery_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_business_id_idempotency_key",
                table: "sales_order_deliveries",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_business_id_order_id_delivery_date",
                table: "sales_order_deliveries",
                columns: new[] { "business_id", "order_id", "delivery_date" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_created_by_user_id",
                table: "sales_order_deliveries",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_journal_entry_id_business_id",
                table: "sales_order_deliveries",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_location_id_business_id",
                table: "sales_order_deliveries",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_deliveries_order_id_business_id",
                table: "sales_order_deliveries",
                columns: new[] { "order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_business_id_order_line_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "business_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_delivery_id_business_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "delivery_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_delivery_id_order_line_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "delivery_id", "order_line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_order_line_id_business_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "order_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_reservation_line_id_business_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "reservation_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_delivery_lines_variant_id_business_id",
                table: "sales_order_delivery_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_business_accounting_settings_accounts_delivered_not_invoice~",
                table: "business_accounting_settings",
                columns: new[] { "delivered_not_invoiced_account_id", "business_id" },
                principalTable: "accounts",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_lines_sales_order_delivery_lines_source_delivery_li~",
                table: "invoice_lines",
                columns: new[] { "source_delivery_line_id", "business_id" },
                principalTable: "sales_order_delivery_lines",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_sales_orders_source_order_id_business_id",
                table: "invoices",
                columns: new[] { "source_order_id", "business_id" },
                principalTable: "sales_orders",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_sales_order_deliveries_immutable
                    BEFORE UPDATE OR DELETE ON sales_order_deliveries
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_sales_order_delivery_lines_immutable
                    BEFORE UPDATE OR DELETE ON sales_order_delivery_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_delivery_invoice_allocations_immutable
                    BEFORE UPDATE OR DELETE ON delivery_invoice_allocations
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_sales_order_deliveries_no_truncate
                    BEFORE TRUNCATE ON sales_order_deliveries
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_order_delivery_lines_no_truncate
                    BEFORE TRUNCATE ON sales_order_delivery_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_delivery_invoice_allocations_no_truncate
                    BEFORE TRUNCATE ON delivery_invoice_allocations
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_business_accounting_settings_accounts_delivered_not_invoice~",
                table: "business_accounting_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_lines_sales_order_delivery_lines_source_delivery_li~",
                table: "invoice_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_sales_orders_source_order_id_business_id",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "delivery_invoice_allocations");

            migrationBuilder.DropTable(
                name: "sales_order_delivery_lines");

            migrationBuilder.DropTable(
                name: "inventory_reservation_lines");

            migrationBuilder.DropTable(
                name: "sales_order_deliveries");

            migrationBuilder.DropTable(
                name: "inventory_reservations");

            migrationBuilder.DropIndex(
                name: "IX_invoices_business_id_source_request_key",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_source_order_id_business_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_business_id_source_delivery_line_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_source_delivery_line_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_business_accounting_settings_delivered_not_invoiced_account~",
                table: "business_accounting_settings");

            migrationBuilder.DropColumn(
                name: "source_order_id",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "source_payload_hash",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "source_request_key",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "revenue_base_amount",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "source_delivery_line_id",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "tax_mode",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "delivered_not_invoiced_account_id",
                table: "business_accounting_settings");
        }
    }
}
