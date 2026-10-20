using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxBackoffAndMasterDataCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_operations_attempts",
                table: "outbox_operations");

            migrationBuilder.RenameColumn(
                name: "next_attempt_utc",
                table: "outbox_operations",
                newName: "next_attempt_at_utc");

            migrationBuilder.RenameColumn(
                name: "attempts",
                table: "outbox_operations",
                newName: "attempt_count");

            migrationBuilder.RenameIndex(
                name: "IX_outbox_operations_state_next_attempt_utc_created_at_utc",
                table: "outbox_operations",
                newName: "IX_outbox_operations_state_next_attempt_at_utc_created_at_utc");

            migrationBuilder.AddColumn<string>(
                name: "last_error_code",
                table: "outbox_operations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "register_session_id",
                table: "local_sale_drafts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "register_session_id",
                table: "local_cash_movements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cached_categories",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    category_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_categories", x => new { x.business_id, x.category_id });
                });

            migrationBuilder.CreateTable(
                name: "cached_units",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    unit_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    short_name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    allow_decimals = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_units", x => new { x.business_id, x.unit_id });
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_operations_attempt_count",
                table: "outbox_operations",
                sql: "attempt_count >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_cached_categories_business_id_revision",
                table: "cached_categories",
                columns: new[] { "business_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "IX_cached_units_business_id_revision",
                table: "cached_units",
                columns: new[] { "business_id", "revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cached_categories");

            migrationBuilder.DropTable(
                name: "cached_units");

            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_operations_attempt_count",
                table: "outbox_operations");

            migrationBuilder.DropColumn(
                name: "last_error_code",
                table: "outbox_operations");

            migrationBuilder.DropColumn(
                name: "register_session_id",
                table: "local_sale_drafts");

            migrationBuilder.DropColumn(
                name: "register_session_id",
                table: "local_cash_movements");

            migrationBuilder.RenameColumn(
                name: "next_attempt_at_utc",
                table: "outbox_operations",
                newName: "next_attempt_utc");

            migrationBuilder.RenameColumn(
                name: "attempt_count",
                table: "outbox_operations",
                newName: "attempts");

            migrationBuilder.RenameIndex(
                name: "IX_outbox_operations_state_next_attempt_at_utc_created_at_utc",
                table: "outbox_operations",
                newName: "IX_outbox_operations_state_next_attempt_utc_created_at_utc");

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_operations_attempts",
                table: "outbox_operations",
                sql: "attempts >= 0");
        }
    }
}
