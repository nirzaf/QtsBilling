using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalSetupTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setup_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    verifier_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issuer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_tickets", x => x.id);
                    table.UniqueConstraint("AK_setup_tickets_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_setup_tickets_purpose", "purpose IN ('Setup', 'Recovery')");
                    table.CheckConstraint("ck_setup_tickets_reason", "char_length(reason) BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_setup_tickets_single_terminal_state", "NOT (consumed_at_utc IS NOT NULL AND revoked_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_setup_tickets_verifier_length", "char_length(verifier_hash) = 64");
                    table.ForeignKey(
                        name: "FK_setup_tickets_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_setup_tickets_users_issuer_user_id",
                        column: x => x.issuer_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_setup_tickets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_setup_tickets_business_id_expires_at_utc",
                table: "setup_tickets",
                columns: new[] { "business_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_setup_tickets_business_id_user_id_consumed_at_utc",
                table: "setup_tickets",
                columns: new[] { "business_id", "user_id", "consumed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_setup_tickets_issuer_user_id",
                table: "setup_tickets",
                column: "issuer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_setup_tickets_user_id",
                table: "setup_tickets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_setup_tickets_verifier_hash",
                table: "setup_tickets",
                column: "verifier_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_tickets");
        }
    }
}
