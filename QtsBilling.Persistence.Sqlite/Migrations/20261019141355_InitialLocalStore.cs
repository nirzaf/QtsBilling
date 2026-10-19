using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialLocalStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cache_tombstones",
                columns: table => new
                {
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    deleted_revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_tombstones", x => new { x.entity_type, x.id });
                });

            migrationBuilder.CreateTable(
                name: "cached_customers",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    customer_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    fields_needed_offline = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_customers", x => new { x.business_id, x.customer_id });
                });

            migrationBuilder.CreateTable(
                name: "cached_products",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    price_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    tax_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_products", x => new { x.business_id, x.location_id, x.product_id });
                    table.CheckConstraint("ck_cached_products_price_non_negative", "price_scaled >= 0");
                    table.CheckConstraint("ck_cached_products_tax_non_negative", "tax_scaled >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cached_stock",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    variant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    server_qty_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_stock", x => new { x.business_id, x.location_id, x.variant_id });
                });

            migrationBuilder.CreateTable(
                name: "device_profile",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    enrolled_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_profile", x => x.device_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_operations",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    operation_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    payload_version = table.Column<int>(type: "INTEGER", nullable: false),
                    canonical_payload = table.Column<string>(type: "TEXT", nullable: false),
                    sha256_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_operations", x => x.operation_id);
                    table.CheckConstraint("ck_outbox_operations_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_operations_payload_version", "payload_version >= 1");
                    table.CheckConstraint("ck_outbox_operations_state", "state IN ('Pending','Sending','Acknowledged','Rejected')");
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    last_ordinal = table.Column<long>(type: "INTEGER", nullable: false),
                    last_success_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    baseline_version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_state", x => new { x.business_id, x.location_id });
                });

            migrationBuilder.CreateTable(
                name: "inbox_receipts",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    server_result = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    server_sale_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    posted_number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    server_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    applied_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_receipts", x => x.operation_id);
                    table.CheckConstraint("ck_inbox_receipts_server_result", "server_result <> ''");
                    table.ForeignKey(
                        name: "FK_inbox_receipts_outbox_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "outbox_operations",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_cash_movements",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    register_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    amount_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_cash_movements", x => x.operation_id);
                    table.CheckConstraint("ck_local_cash_movements_state", "state IN ('Pending','Sending','Posted','Rejected')");
                    table.ForeignKey(
                        name: "FK_local_cash_movements_outbox_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "outbox_operations",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_sale_drafts",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provisional_reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    register_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    lines_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    payment_method = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    amount_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale_drafts", x => x.operation_id);
                    table.CheckConstraint("ck_local_sale_drafts_amount_non_negative", "amount_scaled >= 0");
                    table.CheckConstraint("ck_local_sale_drafts_cash_only", "payment_method = 'cash'");
                    table.CheckConstraint("ck_local_sale_drafts_state", "state IN ('Pending','Sending','Posted','Rejected')");
                    table.ForeignKey(
                        name: "FK_local_sale_drafts_outbox_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "outbox_operations",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_sale_lines",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    variant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    qty_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    unit_price_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    tax_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    discount_scaled = table.Column<long>(type: "INTEGER", nullable: false),
                    expected_catalog_revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale_lines", x => new { x.operation_id, x.ordinal });
                    table.CheckConstraint("ck_local_sale_lines_ordinal", "ordinal >= 0");
                    table.CheckConstraint("ck_local_sale_lines_qty_positive", "qty_scaled > 0");
                    table.ForeignKey(
                        name: "FK_local_sale_lines_local_sale_drafts_operation_id",
                        column: x => x.operation_id,
                        principalTable: "local_sale_drafts",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cache_tombstones_deleted_revision",
                table: "cache_tombstones",
                column: "deleted_revision");

            migrationBuilder.CreateIndex(
                name: "IX_cached_customers_business_id_revision",
                table: "cached_customers",
                columns: new[] { "business_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "IX_cached_products_business_id_location_id_name",
                table: "cached_products",
                columns: new[] { "business_id", "location_id", "name" });

            migrationBuilder.CreateIndex(
                name: "IX_cached_products_price_scaled",
                table: "cached_products",
                column: "price_scaled");

            migrationBuilder.CreateIndex(
                name: "IX_cached_stock_business_id_location_id_revision",
                table: "cached_stock",
                columns: new[] { "business_id", "location_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_receipts_applied_at_utc",
                table: "inbox_receipts",
                column: "applied_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_local_cash_movements_state",
                table: "local_cash_movements",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_local_sale_drafts_provisional_reference",
                table: "local_sale_drafts",
                column: "provisional_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_sale_drafts_state_created_at_utc",
                table: "local_sale_drafts",
                columns: new[] { "state", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_operations_device_id_operation_id",
                table: "outbox_operations",
                columns: new[] { "device_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_operations_state_next_attempt_utc_created_at_utc",
                table: "outbox_operations",
                columns: new[] { "state", "next_attempt_utc", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cache_tombstones");

            migrationBuilder.DropTable(
                name: "cached_customers");

            migrationBuilder.DropTable(
                name: "cached_products");

            migrationBuilder.DropTable(
                name: "cached_stock");

            migrationBuilder.DropTable(
                name: "device_profile");

            migrationBuilder.DropTable(
                name: "inbox_receipts");

            migrationBuilder.DropTable(
                name: "local_cash_movements");

            migrationBuilder.DropTable(
                name: "local_sale_lines");

            migrationBuilder.DropTable(
                name: "sync_state");

            migrationBuilder.DropTable(
                name: "local_sale_drafts");

            migrationBuilder.DropTable(
                name: "outbox_operations");
        }
    }
}
