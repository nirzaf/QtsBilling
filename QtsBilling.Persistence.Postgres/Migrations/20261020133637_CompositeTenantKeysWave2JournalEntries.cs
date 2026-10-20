using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave2JournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_credit_notes_journal_entries_journal_entry_id",
                table: "credit_notes");

            migrationBuilder.DropForeignKey(
                name: "FK_expense_reversals_journal_entries_journal_entry_id",
                table: "expense_reversals");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_entries_journal_entries_reversal_of_entry_id",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_refunds_journal_entries_journal_entry_id",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "IX_refunds_journal_entry_id",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "IX_journal_entries_reversal_of_entry_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "IX_expense_reversals_journal_entry_id",
                table: "expense_reversals");

            migrationBuilder.DropIndex(
                name: "IX_credit_notes_journal_entry_id",
                table: "credit_notes");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_journal_entry_id_business_id",
                table: "refunds",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_reversal_of_entry_id_business_id",
                table: "journal_entries",
                columns: new[] { "reversal_of_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_reversals_journal_entry_id_business_id",
                table: "expense_reversals",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_journal_entry_id_business_id",
                table: "credit_notes",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_credit_notes_journal_entries_journal_entry_id_business_id",
                table: "credit_notes",
                columns: new[] { "journal_entry_id", "business_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expense_reversals_journal_entries_journal_entry_id_business~",
                table: "expense_reversals",
                columns: new[] { "journal_entry_id", "business_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_entries_journal_entries_reversal_of_entry_id_busine~",
                table: "journal_entries",
                columns: new[] { "reversal_of_entry_id", "business_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refunds_journal_entries_journal_entry_id_business_id",
                table: "refunds",
                columns: new[] { "journal_entry_id", "business_id" },
                principalTable: "journal_entries",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_credit_notes_journal_entries_journal_entry_id_business_id",
                table: "credit_notes");

            migrationBuilder.DropForeignKey(
                name: "FK_expense_reversals_journal_entries_journal_entry_id_business~",
                table: "expense_reversals");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_entries_journal_entries_reversal_of_entry_id_busine~",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_refunds_journal_entries_journal_entry_id_business_id",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "IX_refunds_journal_entry_id_business_id",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "IX_journal_entries_reversal_of_entry_id_business_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "IX_expense_reversals_journal_entry_id_business_id",
                table: "expense_reversals");

            migrationBuilder.DropIndex(
                name: "IX_credit_notes_journal_entry_id_business_id",
                table: "credit_notes");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_journal_entry_id",
                table: "refunds",
                column: "journal_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_reversal_of_entry_id",
                table: "journal_entries",
                column: "reversal_of_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reversals_journal_entry_id",
                table: "expense_reversals",
                column: "journal_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_notes_journal_entry_id",
                table: "credit_notes",
                column: "journal_entry_id");

            migrationBuilder.AddForeignKey(
                name: "FK_credit_notes_journal_entries_journal_entry_id",
                table: "credit_notes",
                column: "journal_entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expense_reversals_journal_entries_journal_entry_id",
                table: "expense_reversals",
                column: "journal_entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_entries_journal_entries_reversal_of_entry_id",
                table: "journal_entries",
                column: "reversal_of_entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refunds_journal_entries_journal_entry_id",
                table: "refunds",
                column: "journal_entry_id",
                principalTable: "journal_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
