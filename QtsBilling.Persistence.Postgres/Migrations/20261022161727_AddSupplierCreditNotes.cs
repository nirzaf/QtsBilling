using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierCreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_supplier_b~",
                table: "supplier_payment_allocations");

            migrationBuilder.AlterColumn<Guid>(
                name: "supplier_bill_id",
                table: "supplier_payment_allocations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "open_item_id",
                table: "supplier_payment_allocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_supplier_bill_lines_id_business_id",
                table: "supplier_bill_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateTable(
                name: "supplier_credit_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_purchase_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    value_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    applied_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    balance_due = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_credit_notes", x => x.id);
                    table.UniqueConstraint("AK_supplier_credit_notes_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_supplier_credit_notes_amounts", "base_amount >= 0 AND tax_amount >= 0 AND value_amount > 0 AND value_amount = base_amount + tax_amount AND applied_amount >= 0 AND balance_due >= 0 AND balance_due = value_amount - applied_amount");
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_business_locations_location_id_busine~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_journal_entries_journal_entry_id_busi~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_purchase_receipts_source_purchase_rec~",
                        columns: x => new { x.source_purchase_receipt_id, x.business_id },
                        principalTable: "purchase_receipts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_supplier_bills_supplier_bill_id_busin~",
                        columns: x => new { x.supplier_bill_id, x.business_id },
                        principalTable: "supplier_bills",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_notes_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_credit_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    open_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    applied_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    applied_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_credit_applications", x => x.id);
                    table.CheckConstraint("ck_supplier_credit_applications_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_supplier_credit_applications_exactly_one_target", "((supplier_bill_id IS NOT NULL AND open_item_id IS NULL) OR (supplier_bill_id IS NULL AND open_item_id IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_supplier_credit_applications_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_applications_open_items_open_item_id_busine~",
                        columns: x => new { x.open_item_id, x.business_id },
                        principalTable: "open_items",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_applications_supplier_bills_supplier_bill_i~",
                        columns: x => new { x.supplier_bill_id, x.business_id },
                        principalTable: "supplier_bills",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_applications_supplier_credit_notes_supplier~",
                        columns: x => new { x.supplier_credit_note_id, x.business_id },
                        principalTable: "supplier_credit_notes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_credit_note_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    supplier_bill_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_receipt_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    base_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    value_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_credit_note_lines", x => x.id);
                    table.CheckConstraint("ck_supplier_credit_note_lines_amounts", "quantity > 0 AND unit_cost >= 0 AND base_amount >= 0 AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND value_amount = base_amount + tax_amount");
                    table.ForeignKey(
                        name: "FK_supplier_credit_note_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_note_lines_product_variants_variant_id_busi~",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_note_lines_purchase_receipt_lines_purchase_~",
                        columns: x => new { x.purchase_receipt_line_id, x.business_id },
                        principalTable: "purchase_receipt_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_note_lines_supplier_bill_lines_supplier_bil~",
                        columns: x => new { x.supplier_bill_line_id, x.business_id },
                        principalTable: "supplier_bill_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_credit_note_lines_supplier_credit_notes_supplier_c~",
                        columns: x => new { x.supplier_credit_note_id, x.business_id },
                        principalTable: "supplier_credit_notes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_business_id_open_item_id",
                table: "supplier_payment_allocations",
                columns: new[] { "business_id", "open_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_open_item_id_business_id",
                table: "supplier_payment_allocations",
                columns: new[] { "open_item_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_open_item_~",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_payment_id", "open_item_id" },
                unique: true,
                filter: "open_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_supplier_b~",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_payment_id", "supplier_bill_id" },
                unique: true,
                filter: "supplier_bill_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_payment_allocations_exactly_one_target",
                table: "supplier_payment_allocations",
                sql: "((supplier_bill_id IS NOT NULL AND open_item_id IS NULL) OR (supplier_bill_id IS NULL AND open_item_id IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_business_id_open_item_id",
                table: "supplier_credit_applications",
                columns: new[] { "business_id", "open_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_business_id_supplier_bill_id",
                table: "supplier_credit_applications",
                columns: new[] { "business_id", "supplier_bill_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_business_id_supplier_credit_no~",
                table: "supplier_credit_applications",
                columns: new[] { "business_id", "supplier_credit_note_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_open_item_id_business_id",
                table: "supplier_credit_applications",
                columns: new[] { "open_item_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_supplier_bill_id_business_id",
                table: "supplier_credit_applications",
                columns: new[] { "supplier_bill_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_applications_supplier_credit_note_id_busine~",
                table: "supplier_credit_applications",
                columns: new[] { "supplier_credit_note_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_business_id_supplier_bill_line_id",
                table: "supplier_credit_note_lines",
                columns: new[] { "business_id", "supplier_bill_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_purchase_receipt_line_id_busines~",
                table: "supplier_credit_note_lines",
                columns: new[] { "purchase_receipt_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_supplier_bill_line_id_business_id",
                table: "supplier_credit_note_lines",
                columns: new[] { "supplier_bill_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_supplier_credit_note_id_business~",
                table: "supplier_credit_note_lines",
                columns: new[] { "supplier_credit_note_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_supplier_credit_note_id_line_no",
                table: "supplier_credit_note_lines",
                columns: new[] { "supplier_credit_note_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_note_lines_variant_id_business_id",
                table: "supplier_credit_note_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_business_id_credit_number",
                table: "supplier_credit_notes",
                columns: new[] { "business_id", "credit_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_business_id_supplier_bill_id",
                table: "supplier_credit_notes",
                columns: new[] { "business_id", "supplier_bill_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_business_id_supplier_id_issued_at_utc",
                table: "supplier_credit_notes",
                columns: new[] { "business_id", "supplier_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_journal_entry_id_business_id",
                table: "supplier_credit_notes",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_location_id_business_id",
                table: "supplier_credit_notes",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_source_purchase_receipt_id_business_id",
                table: "supplier_credit_notes",
                columns: new[] { "source_purchase_receipt_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_supplier_bill_id_business_id",
                table: "supplier_credit_notes",
                columns: new[] { "supplier_bill_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_credit_notes_supplier_id_business_id",
                table: "supplier_credit_notes",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_credit_notes_business_idempotency_key",
                table: "supplier_credit_notes",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_payment_allocations_open_items_open_item_id_busine~",
                table: "supplier_payment_allocations",
                columns: new[] { "open_item_id", "business_id" },
                principalTable: "open_items",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supplier_payment_allocations_open_items_open_item_id_busine~",
                table: "supplier_payment_allocations");

            migrationBuilder.DropTable(
                name: "supplier_credit_applications");

            migrationBuilder.DropTable(
                name: "supplier_credit_note_lines");

            migrationBuilder.DropTable(
                name: "supplier_credit_notes");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_allocations_business_id_open_item_id",
                table: "supplier_payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_allocations_open_item_id_business_id",
                table: "supplier_payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_open_item_~",
                table: "supplier_payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_supplier_b~",
                table: "supplier_payment_allocations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_payment_allocations_exactly_one_target",
                table: "supplier_payment_allocations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_supplier_bill_lines_id_business_id",
                table: "supplier_bill_lines");

            migrationBuilder.DropColumn(
                name: "open_item_id",
                table: "supplier_payment_allocations");

            migrationBuilder.AlterColumn<Guid>(
                name: "supplier_bill_id",
                table: "supplier_payment_allocations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_supplier_b~",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_payment_id", "supplier_bill_id" },
                unique: true);
        }
    }
}
