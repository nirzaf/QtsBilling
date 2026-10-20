using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialCreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_credit_notes_business_invoice",
                table: "credit_notes");

            migrationBuilder.AddColumn<bool>(
                name: "track_stock_snapshot",
                table: "invoice_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost_snapshot",
                table: "invoice_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_invoice_lines_id_business_id",
                table: "invoice_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateTable(
                name: "credit_note_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    quantity_credited = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    restock_inventory = table.Column<bool>(type: "boolean", nullable: false),
                    unit_cost_restocked = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_note_lines", x => x.id);
                    table.CheckConstraint("ck_credit_note_lines_amounts", "quantity_credited > 0 AND unit_price >= 0 AND tax_amount >= 0 AND line_total >= 0 AND unit_cost_restocked >= 0");
                    table.ForeignKey(
                        name: "FK_credit_note_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_lines_credit_notes_credit_note_id_business_id",
                        columns: x => new { x.credit_note_id, x.business_id },
                        principalTable: "credit_notes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_credit_note_lines_invoice_lines_invoice_line_id_business_id",
                        columns: x => new { x.invoice_line_id, x.business_id },
                        principalTable: "invoice_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve historical full-credit rows as line-level facts before allowing multiple partial notes.
            migrationBuilder.Sql("""
                INSERT INTO credit_note_lines
                    (id, business_id, credit_note_id, invoice_line_id, variant_id, line_no,
                     quantity_credited, unit_price, tax_amount, line_total, restock_inventory,
                     unit_cost_restocked, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), note.business_id, note.id, line.id, line.variant_id, line.line_no,
                       line.quantity, line.unit_price, line.tax_amount, line.line_total, FALSE, 0,
                       now(), now()
                FROM credit_notes AS note
                INNER JOIN invoice_lines AS line
                    ON line.invoice_id = note.invoice_id
                   AND line.business_id = note.business_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_credit_notes_business_invoice",
                table: "credit_notes",
                columns: new[] { "business_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_lines_business_id",
                table: "credit_note_lines",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_lines_credit_note_id_business_id",
                table: "credit_note_lines",
                columns: new[] { "credit_note_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_lines_credit_note_id_line_no",
                table: "credit_note_lines",
                columns: new[] { "credit_note_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_lines_invoice_line_id_business_id",
                table: "credit_note_lines",
                columns: new[] { "invoice_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_lines_variant_id_business_id",
                table: "credit_note_lines",
                columns: new[] { "variant_id", "business_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_note_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_invoice_lines_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "ix_credit_notes_business_invoice",
                table: "credit_notes");

            migrationBuilder.DropColumn(
                name: "track_stock_snapshot",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "unit_cost_snapshot",
                table: "invoice_lines");

            migrationBuilder.CreateIndex(
                name: "ux_credit_notes_business_invoice",
                table: "credit_notes",
                columns: new[] { "business_id", "invoice_id" },
                unique: true);
        }
    }
}
