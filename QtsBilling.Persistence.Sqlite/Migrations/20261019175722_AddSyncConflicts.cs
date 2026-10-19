using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    server_value = table.Column<string>(type: "TEXT", nullable: true),
                    local_value = table.Column<string>(type: "TEXT", nullable: true),
                    detected_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    resolved_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    resolution = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_conflicts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_business_id_resolved_at_utc",
                table: "sync_conflicts",
                columns: new[] { "business_id", "resolved_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_conflicts");
        }
    }
}
