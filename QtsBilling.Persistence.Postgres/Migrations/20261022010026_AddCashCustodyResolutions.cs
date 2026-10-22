using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCashCustodyResolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_custody_resolutions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_register_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carry_forward_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_kind = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    receiving_custodian_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorised_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_custody_resolutions", x => x.id);
                    table.CheckConstraint("ck_cash_custody_resolutions_amount", "amount >= 0");
                    table.CheckConstraint("ck_cash_custody_resolutions_device_kind", "source_kind <> 'DeviceOperation' OR device_id IS NOT NULL");
                    table.CheckConstraint("ck_cash_custody_resolutions_distinct_people", "receiving_custodian_user_id <> authorised_by_user_id");
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_cash_register_sessions_cash_regist~",
                        columns: x => new { x.cash_register_session_id, x.business_id },
                        principalTable: "cash_register_sessions",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_cash_registers_cash_register_id_bu~",
                        columns: x => new { x.cash_register_id, x.business_id },
                        principalTable: "cash_registers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_device_registrations_device_id_bus~",
                        columns: x => new { x.device_id, x.business_id },
                        principalTable: "device_registrations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_users_authorised_by_user_id",
                        column: x => x.authorised_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_custody_resolutions_users_receiving_custodian_user_id",
                        column: x => x.receiving_custodian_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_authorised_by_user_id",
                table: "cash_custody_resolutions",
                column: "authorised_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_business_id_carry_forward_number",
                table: "cash_custody_resolutions",
                columns: new[] { "business_id", "carry_forward_number" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_business_id_cash_register_session_~",
                table: "cash_custody_resolutions",
                columns: new[] { "business_id", "cash_register_session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_cash_register_id_business_id",
                table: "cash_custody_resolutions",
                columns: new[] { "cash_register_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_cash_register_session_id_business_~",
                table: "cash_custody_resolutions",
                columns: new[] { "cash_register_session_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_device_id_business_id",
                table: "cash_custody_resolutions",
                columns: new[] { "device_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_custody_resolutions_receiving_custodian_user_id",
                table: "cash_custody_resolutions",
                column: "receiving_custodian_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_cash_custody_resolutions_session_source",
                table: "cash_custody_resolutions",
                columns: new[] { "business_id", "cash_register_session_id", "source_kind", "source_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_custody_resolutions");
        }
    }
}
