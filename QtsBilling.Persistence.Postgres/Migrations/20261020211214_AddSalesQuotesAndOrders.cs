using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesQuotesAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sales_document_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    business_year = table.Column<int>(type: "integer", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false),
                    prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    padding = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_document_sequences", x => x.id);
                    table.UniqueConstraint("AK_sales_document_sequences_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_document_sequences_values", "business_year BETWEEN 2000 AND 2200 AND next_number > 0 AND padding BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "FK_sales_document_sequences_business_locations_location_id_bus~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_document_sequences_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quote_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    quote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    promised_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rounding_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
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
                    table.PrimaryKey("PK_sales_quotes", x => x.id);
                    table.UniqueConstraint("AK_sales_quotes_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_quotes_amounts_non_negative", "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND current_revision >= 0");
                    table.ForeignKey(
                        name: "FK_sales_quotes_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quotes_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quotes_customers_customer_id_business_id",
                        columns: x => new { x.customer_id, x.business_id },
                        principalTable: "customers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quotes_users_accepted_by_user_id",
                        column: x => x.accepted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quotes_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quotes_users_sent_by_user_id",
                        column: x => x.sent_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_quote_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_key = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    revenue_base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_payable_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pricing_policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_quote_lines", x => x.id);
                    table.UniqueConstraint("AK_sales_quote_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_quote_lines_values", "line_no > 0 AND quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_quote_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quote_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quote_lines_sales_quotes_quote_id_business_id",
                        columns: x => new { x.quote_id, x.business_id },
                        principalTable: "sales_quotes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_quote_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    quote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    promised_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rounding_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_quote_revisions", x => x.id);
                    table.UniqueConstraint("AK_sales_quote_revisions_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_quote_revisions_values", "revision_number > 0 AND subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_quote_revisions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quote_revisions_sales_quotes_quote_id_business_id",
                        columns: x => new { x.quote_id, x.business_id },
                        principalTable: "sales_quotes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quote_revisions_users_changed_by_user_id",
                        column: x => x.changed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_quote_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_quote_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    promised_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rounding_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    remainder_cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    remainder_cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remainder_cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_sales_orders", x => x.id);
                    table.UniqueConstraint("AK_sales_orders_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_orders_amounts_non_negative", "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND current_revision >= 0");
                    table.ForeignKey(
                        name: "FK_sales_orders_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_customers_customer_id_business_id",
                        columns: x => new { x.customer_id, x.business_id },
                        principalTable: "customers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_sales_quote_revisions_source_quote_revision_id~",
                        columns: x => new { x.source_quote_revision_id, x.business_id },
                        principalTable: "sales_quote_revisions",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_sales_quotes_source_quote_id_business_id",
                        columns: x => new { x.source_quote_id, x.business_id },
                        principalTable: "sales_quotes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_users_confirmed_by_user_id",
                        column: x => x.confirmed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_orders_users_remainder_cancelled_by_user_id",
                        column: x => x.remainder_cancelled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_quote_revision_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_key = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    revenue_base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_payable_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pricing_policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_quote_revision_lines", x => x.id);
                    table.UniqueConstraint("AK_sales_quote_revision_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_quote_revision_lines_values", "line_no > 0 AND quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_quote_revision_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_quote_revision_lines_sales_quote_revisions_quote_revi~",
                        columns: x => new { x.quote_revision_id, x.business_id },
                        principalTable: "sales_quote_revisions",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    promised_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rounding_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_revisions", x => x.id);
                    table.UniqueConstraint("AK_sales_order_revisions_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_order_revisions_values", "revision_number > 0 AND subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_order_revisions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_revisions_sales_orders_order_id_business_id",
                        columns: x => new { x.order_id, x.business_id },
                        principalTable: "sales_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_revisions_users_changed_by_user_id",
                        column: x => x.changed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_key = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity_ordered = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_fulfilled = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_cancelled = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    revenue_base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_payable_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pricing_policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_quote_revision_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_lines", x => x.id);
                    table.UniqueConstraint("AK_sales_order_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_order_lines_quantities", "line_no > 0 AND quantity_ordered > 0 AND quantity_fulfilled >= 0 AND quantity_cancelled >= 0 AND quantity_fulfilled + quantity_cancelled <= quantity_ordered AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_order_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_lines_sales_orders_order_id_business_id",
                        columns: x => new { x.order_id, x.business_id },
                        principalTable: "sales_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_lines_sales_quote_revision_lines_source_quote_r~",
                        columns: x => new { x.source_quote_revision_line_id, x.business_id },
                        principalTable: "sales_quote_revision_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_revision_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_key = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sku_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity_ordered = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_fulfilled = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity_cancelled = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    revenue_base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_payable_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pricing_policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_revision_lines", x => x.id);
                    table.UniqueConstraint("AK_sales_order_revision_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_order_revision_lines_values", "line_no > 0 AND quantity_ordered > 0 AND quantity_fulfilled >= 0 AND quantity_cancelled >= 0 AND quantity_fulfilled + quantity_cancelled <= quantity_ordered AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0");
                    table.ForeignKey(
                        name: "FK_sales_order_revision_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_revision_lines_product_variants_variant_id_busi~",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_order_revision_lines_sales_order_revisions_order_revi~",
                        columns: x => new { x.order_revision_id, x.business_id },
                        principalTable: "sales_order_revisions",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_document_sequences_business_id_location_id_document_k~",
                table: "sales_document_sequences",
                columns: new[] { "business_id", "location_id", "document_kind", "business_year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_document_sequences_location_id_business_id",
                table: "sales_document_sequences",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_business_id_line_key",
                table: "sales_order_lines",
                columns: new[] { "business_id", "line_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_business_id_variant_id",
                table: "sales_order_lines",
                columns: new[] { "business_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_order_id_business_id",
                table: "sales_order_lines",
                columns: new[] { "order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_order_id_line_no",
                table: "sales_order_lines",
                columns: new[] { "order_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_source_quote_revision_line_id_business_id",
                table: "sales_order_lines",
                columns: new[] { "source_quote_revision_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_variant_id_business_id",
                table: "sales_order_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revision_lines_business_id",
                table: "sales_order_revision_lines",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revision_lines_order_revision_id_business_id",
                table: "sales_order_revision_lines",
                columns: new[] { "order_revision_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revision_lines_order_revision_id_line_no",
                table: "sales_order_revision_lines",
                columns: new[] { "order_revision_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revision_lines_variant_id_business_id",
                table: "sales_order_revision_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revisions_business_id_order_id_revision_number",
                table: "sales_order_revisions",
                columns: new[] { "business_id", "order_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revisions_changed_by_user_id",
                table: "sales_order_revisions",
                column: "changed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_revisions_order_id_business_id",
                table: "sales_order_revisions",
                columns: new[] { "order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_business_id_customer_id_status",
                table: "sales_orders",
                columns: new[] { "business_id", "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_business_id_idempotency_key",
                table: "sales_orders",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_business_id_order_number",
                table: "sales_orders",
                columns: new[] { "business_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_business_id_source_quote_id",
                table: "sales_orders",
                columns: new[] { "business_id", "source_quote_id" },
                unique: true,
                filter: "source_quote_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_business_id_status_order_date",
                table: "sales_orders",
                columns: new[] { "business_id", "status", "order_date" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_confirmed_by_user_id",
                table: "sales_orders",
                column: "confirmed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_created_by_user_id",
                table: "sales_orders",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_customer_id_business_id",
                table: "sales_orders",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_location_id_business_id",
                table: "sales_orders",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_remainder_cancelled_by_user_id",
                table: "sales_orders",
                column: "remainder_cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_source_quote_id_business_id",
                table: "sales_orders",
                columns: new[] { "source_quote_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_source_quote_revision_id_business_id",
                table: "sales_orders",
                columns: new[] { "source_quote_revision_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_lines_business_id_line_key",
                table: "sales_quote_lines",
                columns: new[] { "business_id", "line_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_lines_quote_id_business_id",
                table: "sales_quote_lines",
                columns: new[] { "quote_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_lines_quote_id_line_no",
                table: "sales_quote_lines",
                columns: new[] { "quote_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_lines_variant_id_business_id",
                table: "sales_quote_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revision_lines_business_id",
                table: "sales_quote_revision_lines",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revision_lines_quote_revision_id_business_id",
                table: "sales_quote_revision_lines",
                columns: new[] { "quote_revision_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revision_lines_quote_revision_id_line_no",
                table: "sales_quote_revision_lines",
                columns: new[] { "quote_revision_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revisions_business_id_quote_id_revision_number",
                table: "sales_quote_revisions",
                columns: new[] { "business_id", "quote_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revisions_changed_by_user_id",
                table: "sales_quote_revisions",
                column: "changed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quote_revisions_quote_id_business_id",
                table: "sales_quote_revisions",
                columns: new[] { "quote_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_accepted_by_user_id",
                table: "sales_quotes",
                column: "accepted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_business_id_customer_id_quote_date",
                table: "sales_quotes",
                columns: new[] { "business_id", "customer_id", "quote_date" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_business_id_idempotency_key",
                table: "sales_quotes",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_business_id_quote_number",
                table: "sales_quotes",
                columns: new[] { "business_id", "quote_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_business_id_status_quote_date",
                table: "sales_quotes",
                columns: new[] { "business_id", "status", "quote_date" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_created_by_user_id",
                table: "sales_quotes",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_customer_id_business_id",
                table: "sales_quotes",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_location_id_business_id",
                table: "sales_quotes",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotes_sent_by_user_id",
                table: "sales_quotes",
                column: "sent_by_user_id");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_sales_document_sequence_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Sales document sequences cannot be deleted.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.id IS NOT DISTINCT FROM OLD.id
                       AND NEW.business_id IS NOT DISTINCT FROM OLD.business_id
                       AND NEW.location_id IS NOT DISTINCT FROM OLD.location_id
                       AND NEW.document_kind IS NOT DISTINCT FROM OLD.document_kind
                       AND NEW.business_year IS NOT DISTINCT FROM OLD.business_year
                       AND NEW.prefix IS NOT DISTINCT FROM OLD.prefix
                       AND NEW.padding IS NOT DISTINCT FROM OLD.padding
                       AND NEW.next_number = OLD.next_number + 1
                       AND NEW.version = OLD.version + 1 THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'A sales document sequence can only advance by one.' USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_document_sequences_guard
                    BEFORE UPDATE OR DELETE ON sales_document_sequences
                    FOR EACH ROW EXECUTE FUNCTION qts_sales_document_sequence_guard();

                CREATE OR REPLACE FUNCTION qts_sales_quote_header_guard() RETURNS trigger AS $$
                DECLARE v_amendment boolean;
                        v_terms_unchanged boolean;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Sales quotes are retained as commercial history.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.location_id IS DISTINCT FROM OLD.location_id
                       OR NEW.quote_number IS DISTINCT FROM OLD.quote_number
                       OR NEW.created_by_user_id IS DISTINCT FROM OLD.created_by_user_id
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash IS DISTINCT FROM OLD.payload_hash THEN
                        RAISE EXCEPTION 'Sales quote identity is immutable.' USING ERRCODE = 'check_violation';
                    END IF;

                    v_terms_unchanged := NEW.customer_id IS NOT DISTINCT FROM OLD.customer_id
                        AND NEW.quote_date IS NOT DISTINCT FROM OLD.quote_date
                        AND NEW.expiry_date IS NOT DISTINCT FROM OLD.expiry_date
                        AND NEW.promised_delivery_date IS NOT DISTINCT FROM OLD.promised_delivery_date
                        AND NEW.currency IS NOT DISTINCT FROM OLD.currency
                        AND NEW.subtotal_amount IS NOT DISTINCT FROM OLD.subtotal_amount
                        AND NEW.discount_amount IS NOT DISTINCT FROM OLD.discount_amount
                        AND NEW.tax_amount IS NOT DISTINCT FROM OLD.tax_amount
                        AND NEW.total_amount IS NOT DISTINCT FROM OLD.total_amount
                        AND NEW.rounding_amount IS NOT DISTINCT FROM OLD.rounding_amount
                        AND NEW.note IS NOT DISTINCT FROM OLD.note;
                    v_amendment := OLD.status IN ('Sent', 'Accepted') AND NEW.status = 'Sent'
                        AND NEW.current_revision = OLD.current_revision + 1;

                    IF OLD.status = 'Draft' AND NEW.status = 'Draft' AND NEW.current_revision = OLD.current_revision THEN
                        RETURN NEW;
                    ELSIF OLD.status = 'Draft' AND NEW.status = 'Sent' AND NEW.current_revision = 1 AND v_terms_unchanged THEN
                        RETURN NEW;
                    ELSIF OLD.status = 'Draft' AND NEW.status = 'Expired' AND NEW.current_revision = OLD.current_revision AND v_terms_unchanged THEN
                        RETURN NEW;
                    ELSIF OLD.status = 'Draft' AND NEW.status = 'Cancelled' AND NEW.current_revision = OLD.current_revision AND v_terms_unchanged THEN
                        RETURN NEW;
                    ELSIF v_amendment THEN
                        IF EXISTS (SELECT 1 FROM sales_orders o WHERE o.business_id = OLD.business_id AND o.source_quote_id = OLD.id) THEN
                            RAISE EXCEPTION 'A converted quote cannot be amended.' USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    ELSIF OLD.status IN ('Sent', 'Accepted')
                       AND NEW.status IN ('Accepted', 'Rejected', 'Expired', 'Cancelled')
                       AND NEW.current_revision = OLD.current_revision AND v_terms_unchanged THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Invalid sales quote transition or historical term edit (% -> %).', OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_quotes_guard
                    BEFORE UPDATE OR DELETE ON sales_quotes
                    FOR EACH ROW EXECUTE FUNCTION qts_sales_quote_header_guard();

                CREATE OR REPLACE FUNCTION qts_sales_quote_lines_draft_guard() RETURNS trigger AS $$
                DECLARE v_status text;
                        v_quote_id uuid;
                        v_business_id uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_quote_id := OLD.quote_id;
                        v_business_id := OLD.business_id;
                    ELSE
                        v_quote_id := NEW.quote_id;
                        v_business_id := NEW.business_id;
                    END IF;
                    IF TG_OP = 'UPDATE' AND (NEW.quote_id IS DISTINCT FROM OLD.quote_id OR NEW.business_id IS DISTINCT FROM OLD.business_id) THEN
                        RAISE EXCEPTION 'A quote line cannot be moved to another quote.' USING ERRCODE = 'check_violation';
                    END IF;
                    SELECT status INTO v_status FROM sales_quotes WHERE id = v_quote_id AND business_id = v_business_id;
                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Quote lines are mutable only while the quote is a draft.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_quote_lines_draft_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON sales_quote_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_sales_quote_lines_draft_guard();

                CREATE OR REPLACE FUNCTION qts_append_only_revision_guard() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Commercial revisions are immutable history.' USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_quote_revisions_immutable
                    BEFORE UPDATE OR DELETE ON sales_quote_revisions
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_sales_quote_revision_lines_immutable
                    BEFORE UPDATE OR DELETE ON sales_quote_revision_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_sales_order_revisions_immutable
                    BEFORE UPDATE OR DELETE ON sales_order_revisions
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();
                CREATE TRIGGER trg_sales_order_revision_lines_immutable
                    BEFORE UPDATE OR DELETE ON sales_order_revision_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_append_only_revision_guard();

                CREATE OR REPLACE FUNCTION qts_sales_order_header_guard() RETURNS trigger AS $$
                DECLARE v_terms_unchanged boolean;
                        v_amendment boolean;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Sales orders are retained as commercial history.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.location_id IS DISTINCT FROM OLD.location_id
                       OR NEW.order_number IS DISTINCT FROM OLD.order_number
                       OR NEW.source_quote_id IS DISTINCT FROM OLD.source_quote_id
                       OR NEW.source_quote_revision_id IS DISTINCT FROM OLD.source_quote_revision_id
                       OR NEW.created_by_user_id IS DISTINCT FROM OLD.created_by_user_id
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash IS DISTINCT FROM OLD.payload_hash THEN
                        RAISE EXCEPTION 'Sales order identity is immutable.' USING ERRCODE = 'check_violation';
                    END IF;
                    v_terms_unchanged := NEW.customer_id IS NOT DISTINCT FROM OLD.customer_id
                        AND NEW.order_date IS NOT DISTINCT FROM OLD.order_date
                        AND NEW.promised_delivery_date IS NOT DISTINCT FROM OLD.promised_delivery_date
                        AND NEW.currency IS NOT DISTINCT FROM OLD.currency
                        AND NEW.subtotal_amount IS NOT DISTINCT FROM OLD.subtotal_amount
                        AND NEW.discount_amount IS NOT DISTINCT FROM OLD.discount_amount
                        AND NEW.tax_amount IS NOT DISTINCT FROM OLD.tax_amount
                        AND NEW.total_amount IS NOT DISTINCT FROM OLD.total_amount
                        AND NEW.rounding_amount IS NOT DISTINCT FROM OLD.rounding_amount
                        AND NEW.note IS NOT DISTINCT FROM OLD.note;
                    v_amendment := OLD.status IN ('Confirmed', 'PartiallyFulfilled')
                        AND NEW.status IN ('Confirmed', 'PartiallyFulfilled', 'Closed')
                        AND NEW.current_revision = OLD.current_revision + 1;

                    IF OLD.status = 'Draft' AND NEW.status = 'Draft' AND NEW.current_revision = OLD.current_revision THEN
                        RETURN NEW;
                    ELSIF OLD.status = 'Draft' AND NEW.status = 'Confirmed' AND NEW.current_revision = 1 AND v_terms_unchanged THEN
                        RETURN NEW;
                    ELSIF v_amendment THEN
                        RETURN NEW;
                    ELSIF OLD.status IN ('Confirmed', 'PartiallyFulfilled')
                       AND NEW.status IN ('Confirmed', 'PartiallyFulfilled', 'Fulfilled')
                       AND NEW.current_revision = OLD.current_revision AND v_terms_unchanged THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Invalid sales order transition or historical term edit (% -> %).', OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_orders_guard
                    BEFORE UPDATE OR DELETE ON sales_orders
                    FOR EACH ROW EXECUTE FUNCTION qts_sales_order_header_guard();

                CREATE OR REPLACE FUNCTION qts_sales_order_lines_guard() RETURNS trigger AS $$
                DECLARE v_status text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        SELECT status INTO v_status FROM sales_orders WHERE id = OLD.order_id AND business_id = OLD.business_id;
                    ELSE
                        SELECT status INTO v_status FROM sales_orders WHERE id = NEW.order_id AND business_id = NEW.business_id;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF v_status = 'Draft' THEN RETURN OLD; END IF;
                        RAISE EXCEPTION 'Confirmed order lines cannot be deleted.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF v_status = 'Draft' THEN RETURN NEW; END IF;
                        IF v_status IN ('Confirmed', 'PartiallyFulfilled')
                           AND EXISTS (SELECT 1 FROM sales_orders o WHERE o.id = NEW.order_id AND o.business_id = NEW.business_id AND o.current_revision > 1) THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'Order lines can be added after confirmation only by a versioned amendment.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.order_id IS DISTINCT FROM OLD.order_id OR NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.line_key IS DISTINCT FROM OLD.line_key OR NEW.line_no IS DISTINCT FROM OLD.line_no
                       OR NEW.variant_id IS DISTINCT FROM OLD.variant_id OR NEW.description IS DISTINCT FROM OLD.description
                       OR NEW.sku_snapshot IS DISTINCT FROM OLD.sku_snapshot OR NEW.quantity_ordered IS DISTINCT FROM OLD.quantity_ordered
                       OR NEW.unit_price IS DISTINCT FROM OLD.unit_price OR NEW.discount_amount IS DISTINCT FROM OLD.discount_amount
                       OR NEW.revenue_base_amount IS DISTINCT FROM OLD.revenue_base_amount OR NEW.tax_rate IS DISTINCT FROM OLD.tax_rate
                       OR NEW.tax_amount IS DISTINCT FROM OLD.tax_amount OR NEW.line_payable_amount IS DISTINCT FROM OLD.line_payable_amount
                       OR NEW.tax_mode IS DISTINCT FROM OLD.tax_mode OR NEW.pricing_policy_version IS DISTINCT FROM OLD.pricing_policy_version
                       OR NEW.source_quote_revision_line_id IS DISTINCT FROM OLD.source_quote_revision_line_id THEN
                        RAISE EXCEPTION 'Confirmed order-line commercial terms are immutable.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF v_status = 'Draft' THEN RETURN NEW; END IF;
                    IF v_status IN ('Confirmed', 'PartiallyFulfilled')
                       AND NEW.quantity_fulfilled >= OLD.quantity_fulfilled
                       AND NEW.quantity_fulfilled <= NEW.quantity_ordered - NEW.quantity_cancelled
                       AND NEW.quantity_cancelled = OLD.quantity_cancelled THEN
                        RETURN NEW;
                    END IF;
                    IF v_status IN ('Confirmed', 'PartiallyFulfilled')
                       AND NEW.quantity_cancelled >= OLD.quantity_cancelled
                       AND NEW.quantity_cancelled = NEW.quantity_ordered - NEW.quantity_fulfilled
                       AND NEW.quantity_fulfilled = OLD.quantity_fulfilled THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Only fulfillment or approved remainder cancellation may change a confirmed order line.' USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_order_lines_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON sales_order_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_sales_order_lines_guard();

                CREATE OR REPLACE FUNCTION qts_validate_sales_quote_revision() RETURNS trigger AS $$
                DECLARE v_count integer;
                        v_discount numeric;
                        v_tax numeric;
                        v_gross numeric;
                        v_payable numeric;
                        v_balanced numeric;
                BEGIN
                    SELECT count(*)::integer, COALESCE(sum(discount_amount), 0), COALESCE(sum(tax_amount), 0),
                           COALESCE(sum(round(quantity * unit_price, 4)), 0), COALESCE(sum(line_payable_amount), 0),
                           COALESCE(sum(revenue_base_amount + tax_amount - line_payable_amount), 0)
                    INTO v_count, v_discount, v_tax, v_gross, v_payable, v_balanced
                    FROM sales_quote_revision_lines WHERE business_id = NEW.business_id AND quote_revision_id = NEW.id;
                    IF v_count = 0 OR v_discount <> NEW.discount_amount OR v_tax <> NEW.tax_amount
                       OR v_gross <> NEW.subtotal_amount OR v_payable + NEW.rounding_amount <> NEW.total_amount OR v_balanced <> 0 THEN
                        RAISE EXCEPTION 'A quote revision must commit with its complete balanced commercial line snapshot.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_sales_quote_revision_consistency
                    AFTER INSERT ON sales_quote_revisions DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_sales_quote_revision();

                CREATE OR REPLACE FUNCTION qts_validate_sales_order_revision() RETURNS trigger AS $$
                DECLARE v_count integer;
                        v_discount numeric;
                        v_tax numeric;
                        v_gross numeric;
                        v_payable numeric;
                        v_balanced numeric;
                BEGIN
                    SELECT count(*)::integer, COALESCE(sum(discount_amount), 0), COALESCE(sum(tax_amount), 0),
                           COALESCE(sum(round(quantity_ordered * unit_price, 4)), 0), COALESCE(sum(line_payable_amount), 0),
                           COALESCE(sum(revenue_base_amount + tax_amount - line_payable_amount), 0)
                    INTO v_count, v_discount, v_tax, v_gross, v_payable, v_balanced
                    FROM sales_order_revision_lines WHERE business_id = NEW.business_id AND order_revision_id = NEW.id;
                    IF NEW.total_amount <> 0 AND v_count = 0 THEN
                        RAISE EXCEPTION 'A non-zero order revision must contain commercial lines.' USING ERRCODE = 'check_violation';
                    END IF;
                    IF v_discount <> NEW.discount_amount OR v_tax <> NEW.tax_amount
                       OR v_gross <> NEW.subtotal_amount OR v_payable + NEW.rounding_amount <> NEW.total_amount OR v_balanced <> 0 THEN
                        RAISE EXCEPTION 'An order revision must commit with its complete balanced commercial line snapshot.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_sales_order_revision_consistency
                    AFTER INSERT ON sales_order_revisions DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_sales_order_revision();

                CREATE OR REPLACE FUNCTION qts_validate_order_current_revision() RETURNS trigger AS $$
                DECLARE v_revision sales_order_revisions%ROWTYPE;
                BEGIN
                    IF NEW.current_revision = 0 THEN RETURN NULL; END IF;
                    SELECT * INTO v_revision FROM sales_order_revisions WHERE business_id = NEW.business_id
                        AND order_id = NEW.id AND revision_number = NEW.current_revision;
                    IF NOT FOUND OR v_revision.subtotal_amount <> NEW.subtotal_amount OR v_revision.discount_amount <> NEW.discount_amount
                       OR v_revision.tax_amount <> NEW.tax_amount OR v_revision.total_amount <> NEW.total_amount
                       OR v_revision.rounding_amount <> NEW.rounding_amount OR v_revision.promised_delivery_date IS DISTINCT FROM NEW.promised_delivery_date THEN
                        RAISE EXCEPTION 'The current order header must match its latest immutable revision.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_sales_order_current_revision
                    AFTER INSERT OR UPDATE ON sales_orders DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_order_current_revision();

                CREATE OR REPLACE FUNCTION qts_validate_order_line_revision_binding() RETURNS trigger AS $$
                DECLARE v_order sales_orders%ROWTYPE;
                        v_quantity numeric;
                        v_gross numeric;
                        v_discount numeric;
                        v_revenue_base numeric;
                        v_tax numeric;
                        v_payable numeric;
                BEGIN
                    SELECT * INTO v_order FROM sales_orders WHERE id = NEW.order_id AND business_id = NEW.business_id;
                    IF NOT FOUND OR v_order.status = 'Draft' THEN RETURN NULL; END IF;
                    IF TG_OP = 'UPDATE' AND NEW.quantity_fulfilled IS DISTINCT FROM OLD.quantity_fulfilled
                       AND NEW.quantity_cancelled IS NOT DISTINCT FROM OLD.quantity_cancelled THEN RETURN NULL; END IF;
                    IF NEW.quantity_fulfilled = 0 AND NEW.quantity_cancelled = NEW.quantity_ordered THEN RETURN NULL; END IF;
                    v_quantity := CASE WHEN NEW.quantity_fulfilled > 0 THEN NEW.quantity_fulfilled ELSE NEW.quantity_ordered END;
                    v_gross := round(v_quantity * NEW.unit_price, 4);
                    v_discount := CASE WHEN NEW.quantity_fulfilled > 0
                        THEN round(NEW.discount_amount * NEW.quantity_fulfilled / NEW.quantity_ordered, 4)
                        ELSE NEW.discount_amount END;
                    CASE NEW.tax_mode
                        WHEN 'Exempt' THEN
                            v_revenue_base := v_gross - v_discount;
                            v_tax := 0;
                            v_payable := v_revenue_base;
                        WHEN 'Exclusive' THEN
                            v_revenue_base := v_gross - v_discount;
                            v_tax := round(v_revenue_base * NEW.tax_rate, 4);
                            v_payable := v_revenue_base + v_tax;
                        WHEN 'Inclusive' THEN
                            v_payable := v_gross - v_discount;
                            v_tax := round(v_payable * NEW.tax_rate / (1 + NEW.tax_rate), 4);
                            v_revenue_base := v_payable - v_tax;
                        ELSE
                            RAISE EXCEPTION 'A confirmed order line has an unsupported tax mode.' USING ERRCODE = 'check_violation';
                    END CASE;
                    IF NOT EXISTS (
                        SELECT 1 FROM sales_order_revisions r
                        JOIN sales_order_revision_lines l ON l.business_id = r.business_id AND l.order_revision_id = r.id
                        WHERE r.business_id = NEW.business_id AND r.order_id = NEW.order_id
                          AND r.revision_number = v_order.current_revision AND l.line_key = NEW.line_key
                          AND l.variant_id IS NOT DISTINCT FROM NEW.variant_id
                          AND l.quantity_ordered = v_quantity AND l.unit_price = NEW.unit_price
                          AND l.discount_amount = v_discount AND l.revenue_base_amount = v_revenue_base
                          AND l.tax_rate = NEW.tax_rate AND l.tax_amount = v_tax AND l.line_payable_amount = v_payable
                          AND l.tax_mode = NEW.tax_mode AND l.pricing_policy_version = NEW.pricing_policy_version
                    ) THEN
                        RAISE EXCEPTION 'A confirmed order line change must match the current immutable order revision.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_sales_order_line_revision_binding
                    AFTER INSERT OR UPDATE ON sales_order_lines DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_order_line_revision_binding();

                CREATE OR REPLACE FUNCTION qts_validate_quote_current_revision() RETURNS trigger AS $$
                DECLARE v_revision sales_quote_revisions%ROWTYPE;
                BEGIN
                    IF NEW.current_revision = 0 THEN RETURN NULL; END IF;
                    SELECT * INTO v_revision FROM sales_quote_revisions WHERE business_id = NEW.business_id
                        AND quote_id = NEW.id AND revision_number = NEW.current_revision;
                    IF NOT FOUND OR v_revision.subtotal_amount <> NEW.subtotal_amount OR v_revision.discount_amount <> NEW.discount_amount
                       OR v_revision.tax_amount <> NEW.tax_amount OR v_revision.total_amount <> NEW.total_amount
                       OR v_revision.rounding_amount <> NEW.rounding_amount OR v_revision.expiry_date <> NEW.expiry_date
                       OR v_revision.promised_delivery_date IS DISTINCT FROM NEW.promised_delivery_date THEN
                        RAISE EXCEPTION 'The current quote header must match its latest immutable revision.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_sales_quote_current_revision
                    AFTER UPDATE ON sales_quotes DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_quote_current_revision();

                CREATE TRIGGER trg_sales_document_sequences_no_truncate BEFORE TRUNCATE ON sales_document_sequences FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_quotes_no_truncate BEFORE TRUNCATE ON sales_quotes FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_quote_lines_no_truncate BEFORE TRUNCATE ON sales_quote_lines FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_quote_revisions_no_truncate BEFORE TRUNCATE ON sales_quote_revisions FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_quote_revision_lines_no_truncate BEFORE TRUNCATE ON sales_quote_revision_lines FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_orders_no_truncate BEFORE TRUNCATE ON sales_orders FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_order_lines_no_truncate BEFORE TRUNCATE ON sales_order_lines FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_order_revisions_no_truncate BEFORE TRUNCATE ON sales_order_revisions FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_sales_order_revision_lines_no_truncate BEFORE TRUNCATE ON sales_order_revision_lines FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM sales_document_sequences)
                       OR EXISTS (SELECT 1 FROM sales_quotes)
                       OR EXISTS (SELECT 1 FROM sales_quote_lines)
                       OR EXISTS (SELECT 1 FROM sales_quote_revisions)
                       OR EXISTS (SELECT 1 FROM sales_quote_revision_lines)
                       OR EXISTS (SELECT 1 FROM sales_orders)
                       OR EXISTS (SELECT 1 FROM sales_order_lines)
                       OR EXISTS (SELECT 1 FROM sales_order_revisions)
                       OR EXISTS (SELECT 1 FROM sales_order_revision_lines) THEN
                        RAISE EXCEPTION 'Sales quote/order history exists; this additive migration cannot be rolled back.';
                    END IF;
                END;
                $$;
                """);
            migrationBuilder.DropTable(
                name: "sales_document_sequences");

            migrationBuilder.DropTable(
                name: "sales_order_lines");

            migrationBuilder.DropTable(
                name: "sales_order_revision_lines");

            migrationBuilder.DropTable(
                name: "sales_quote_lines");

            migrationBuilder.DropTable(
                name: "sales_quote_revision_lines");

            migrationBuilder.DropTable(
                name: "sales_order_revisions");

            migrationBuilder.DropTable(
                name: "sales_orders");

            migrationBuilder.DropTable(
                name: "sales_quote_revisions");

            migrationBuilder.DropTable(
                name: "sales_quotes");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS qts_validate_quote_current_revision();
                DROP FUNCTION IF EXISTS qts_validate_order_line_revision_binding();
                DROP FUNCTION IF EXISTS qts_validate_order_current_revision();
                DROP FUNCTION IF EXISTS qts_validate_sales_order_revision();
                DROP FUNCTION IF EXISTS qts_validate_sales_quote_revision();
                DROP FUNCTION IF EXISTS qts_sales_order_lines_guard();
                DROP FUNCTION IF EXISTS qts_sales_order_header_guard();
                DROP FUNCTION IF EXISTS qts_append_only_revision_guard();
                DROP FUNCTION IF EXISTS qts_sales_quote_lines_draft_guard();
                DROP FUNCTION IF EXISTS qts_sales_quote_header_guard();
                DROP FUNCTION IF EXISTS qts_sales_document_sequence_guard();
                """);
        }
    }
}
