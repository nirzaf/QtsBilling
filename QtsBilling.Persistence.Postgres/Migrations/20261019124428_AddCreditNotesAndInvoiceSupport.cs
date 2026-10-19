using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditNotesAndInvoiceSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invoices_business_id_invoice_number",
                table: "invoices");

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_hash",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credit_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "text", maxLength: 400, nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_notes", x => x.id);
                    table.UniqueConstraint("AK_credit_notes_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_credit_notes_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "FK_credit_notes_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_notes_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_notes_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_notes_users_issued_by_user_id",
                        column: x => x.issued_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_payments_business_idempotency_key",
                table: "payments",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_business_id_customer_id_issued_at_utc",
                table: "invoices",
                columns: new[] { "business_id", "customer_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_business_number",
                table: "invoices",
                columns: new[] { "business_id", "invoice_number" },
                unique: true,
                filter: "invoice_number <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_business_id_credit_number",
                table: "credit_notes",
                columns: new[] { "business_id", "credit_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_invoice_id_business_id",
                table: "credit_notes",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_issued_by_user_id",
                table: "credit_notes",
                column: "issued_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_journal_entry_id",
                table: "credit_notes",
                column: "journal_entry_id");

            migrationBuilder.CreateIndex(
                name: "ux_credit_notes_business_invoice",
                table: "credit_notes",
                columns: new[] { "business_id", "invoice_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_notes");

            migrationBuilder.DropIndex(
                name: "ux_payments_business_idempotency_key",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_invoices_business_id_customer_id_issued_at_utc",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ux_invoices_business_number",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "payload_hash",
                table: "payments");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_business_id_invoice_number",
                table: "invoices",
                columns: new[] { "business_id", "invoice_number" },
                unique: true);
        }
    }
}
