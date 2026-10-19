using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerSourceIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_journal_entries_business_id_source_type_source_id",
                table: "journal_entries");

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_business_source",
                table: "journal_entries",
                columns: new[] { "business_id", "source_type", "source_id" },
                unique: true,
                filter: "source_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_journal_entries_business_source",
                table: "journal_entries");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_business_id_source_type_source_id",
                table: "journal_entries",
                columns: new[] { "business_id", "source_type", "source_id" });
        }
    }
}
