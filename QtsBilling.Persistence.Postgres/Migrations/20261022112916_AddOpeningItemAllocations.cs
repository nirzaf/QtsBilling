using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningItemAllocations : Migration
    {
        /// <summary>
        /// Additive schema for specification task 25. A deposit application names exactly one settleable document -
        /// an issued invoice or an opening receivable the native import brought in - so <c>invoice_id</c> becomes
        /// nullable, <c>open_item_id</c> is added with a composite tenant foreign key, and a check constraint makes the
        /// exactly-one rule a fact the database refuses rather than a convention the code remembers. Every row
        /// written before this change already named an invoice and satisfies the constraint untouched: no posted
        /// allocation is rewritten, reinterpreted or repriced. <c>deposit_refunds</c> is the append-only record of
        /// money handed back from a customer's unapplied deposit, each row linked to its own reversal journal.
        /// Nothing is dropped that holds financial history, and the down path only removes what was added.
        /// </summary>
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "invoice_id",
                table: "payment_allocations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "open_item_id",
                table: "payment_allocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deposit_refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cash_register_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    refunded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deposit_refunds", x => x.id);
                    table.UniqueConstraint("AK_deposit_refunds_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_deposit_refunds_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_deposit_refunds_settlement_named", "((method = 'cash' AND cash_register_session_id IS NOT NULL) OR (method <> 'cash' AND bank_reference IS NOT NULL AND length(bank_reference) > 0))");
                    table.ForeignKey(
                        name: "FK_deposit_refunds_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deposit_refunds_journal_entries_journal_entry_id_business_id",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deposit_refunds_payments_payment_id_business_id",
                        columns: x => new { x.payment_id, x.business_id },
                        principalTable: "payments",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_business_id_open_item_id",
                table: "payment_allocations",
                columns: new[] { "business_id", "open_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_open_item_id_business_id",
                table: "payment_allocations",
                columns: new[] { "open_item_id", "business_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_allocations_exactly_one_target",
                table: "payment_allocations",
                sql: "((invoice_id IS NOT NULL AND open_item_id IS NULL) OR (invoice_id IS NULL AND open_item_id IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_deposit_refunds_business_id_journal_entry_id",
                table: "deposit_refunds",
                columns: new[] { "business_id", "journal_entry_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deposit_refunds_business_id_payment_id",
                table: "deposit_refunds",
                columns: new[] { "business_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deposit_refunds_journal_entry_id_business_id",
                table: "deposit_refunds",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deposit_refunds_payment_id_business_id",
                table: "deposit_refunds",
                columns: new[] { "payment_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_deposit_refunds_business_idempotency_key",
                table: "deposit_refunds",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_payment_allocations_open_items_open_item_id_business_id",
                table: "payment_allocations",
                columns: new[] { "open_item_id", "business_id" },
                principalTable: "open_items",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_payment_allocations_open_items_open_item_id_business_id",
                table: "payment_allocations");

            migrationBuilder.DropTable(
                name: "deposit_refunds");

            migrationBuilder.DropIndex(
                name: "IX_payment_allocations_business_id_open_item_id",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_payment_allocations_open_item_id_business_id",
                table: "payment_allocations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_allocations_exactly_one_target",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "open_item_id",
                table: "payment_allocations");

            migrationBuilder.AlterColumn<Guid>(
                name: "invoice_id",
                table: "payment_allocations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
