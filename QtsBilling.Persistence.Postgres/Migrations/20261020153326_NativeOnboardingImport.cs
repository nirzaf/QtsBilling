using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class NativeOnboardingImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_record_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_namespace = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_record_links", x => x.id);
                    table.UniqueConstraint("AK_external_record_links_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_external_record_links_sha256", "length(payload_sha256) = 64");
                    table.ForeignKey(
                        name: "FK_external_record_links_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "open_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    original_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_items", x => x.id);
                    table.UniqueConstraint("AK_open_items_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_open_items_amount", "original_amount > 0");
                    table.CheckConstraint("ck_open_items_party", "((kind = 'Receivable' AND customer_id IS NOT NULL AND supplier_id IS NULL) OR (kind = 'Payable' AND supplier_id IS NOT NULL AND customer_id IS NULL))");
                    table.ForeignKey(
                        name: "FK_open_items_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_items_customers_customer_id_business_id",
                        columns: x => new { x.customer_id, x.business_id },
                        principalTable: "customers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_items_journal_entries_journal_entry_id_business_id",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_items_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_record_links_business_id_target_type_target_id",
                table: "external_record_links",
                columns: new[] { "business_id", "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ux_external_record_links_batch",
                table: "external_record_links",
                columns: new[] { "business_id", "batch_id", "resource_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_external_record_links_identity",
                table: "external_record_links",
                columns: new[] { "business_id", "dataset_namespace", "resource_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_open_items_business_id_customer_id",
                table: "open_items",
                columns: new[] { "business_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_open_items_business_id_source_type_source_id",
                table: "open_items",
                columns: new[] { "business_id", "source_type", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_open_items_business_id_supplier_id",
                table: "open_items",
                columns: new[] { "business_id", "supplier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_open_items_customer_id_business_id",
                table: "open_items",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_open_items_journal_entry_id_business_id",
                table: "open_items",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_open_items_supplier_id_business_id",
                table: "open_items",
                columns: new[] { "supplier_id", "business_id" });

            // Source identity and opening items are financial provenance: they are insert-only at the
            // database, not only in the application context.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_import_append_only_guard() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% rows are append-only; corrections must be recorded as new rows.', TG_TABLE_NAME
                    USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_external_record_links_append_only
                BEFORE UPDATE OR DELETE ON external_record_links
                FOR EACH ROW EXECUTE FUNCTION qts_import_append_only_guard();

                CREATE TRIGGER trg_open_items_append_only
                BEFORE UPDATE OR DELETE ON open_items
                FOR EACH ROW EXECUTE FUNCTION qts_import_append_only_guard();

                CREATE TRIGGER trg_external_record_links_no_truncate
                BEFORE TRUNCATE ON external_record_links
                FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();

                CREATE TRIGGER trg_open_items_no_truncate
                BEFORE TRUNCATE ON open_items
                FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_external_record_links_no_truncate ON external_record_links;
                DROP TRIGGER IF EXISTS trg_open_items_no_truncate ON open_items;
                DROP TRIGGER IF EXISTS trg_external_record_links_append_only ON external_record_links;
                DROP TRIGGER IF EXISTS trg_open_items_append_only ON open_items;
                DROP FUNCTION IF EXISTS qts_import_append_only_guard();
                """);

            migrationBuilder.DropTable(
                name: "external_record_links");

            migrationBuilder.DropTable(
                name: "open_items");
        }
    }
}
