using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceLifecycleRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at_utc",
                table: "device_registrations",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "schema_version",
                table: "device_registrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "device_reassignment_clearances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledged_provisional_cash = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    acknowledged_operations = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    cleared_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    consumed_by_reassignment_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_reassignment_clearances", x => x.id);
                    table.UniqueConstraint("AK_device_reassignment_clearances_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_device_clearances_cash_non_negative", "acknowledged_provisional_cash >= 0");
                    table.CheckConstraint("ck_device_clearances_operations_non_negative", "acknowledged_operations >= 0");
                    table.CheckConstraint("ck_device_clearances_reason", "char_length(reason) BETWEEN 1 AND 500");
                    table.ForeignKey(
                        name: "FK_device_reassignment_clearances_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_reassignment_clearances_device_registrations_device_~",
                        columns: x => new { x.device_id, x.business_id },
                        principalTable: "device_registrations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_reassignment_clearances_users_cleared_by_user_id",
                        column: x => x.cleared_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_reassignment_clearances_business_id_device_id_consum~",
                table: "device_reassignment_clearances",
                columns: new[] { "business_id", "device_id", "consumed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_device_reassignment_clearances_cleared_by_user_id",
                table: "device_reassignment_clearances",
                column: "cleared_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_reassignment_clearances_device_id_business_id",
                table: "device_reassignment_clearances",
                columns: new[] { "device_id", "business_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_reassignment_clearances");

            migrationBuilder.DropColumn(
                name: "retired_at_utc",
                table: "device_registrations");

            migrationBuilder.DropColumn(
                name: "schema_version",
                table: "device_registrations");
        }
    }
}
