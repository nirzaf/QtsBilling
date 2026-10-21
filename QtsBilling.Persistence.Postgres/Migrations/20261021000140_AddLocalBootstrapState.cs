using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalBootstrapState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instance_bootstrap",
                columns: table => new
                {
                    singleton_key = table.Column<short>(type: "smallint", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    first_owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initialised_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    opening_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approved_opening_manifest_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_bootstrap", x => x.singleton_key);
                    table.CheckConstraint("ck_instance_bootstrap_opening_mode", "opening_mode IN ('NewBusiness', 'PendingNativeOpening')");
                    table.CheckConstraint("ck_instance_bootstrap_singleton", "singleton_key = 1");
                    table.CheckConstraint("ck_instance_bootstrap_state", "state = 'Completed'");
                    table.ForeignKey(
                        name: "FK_instance_bootstrap_businesses_first_business_id",
                        column: x => x.first_business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_bootstrap_users_first_owner_user_id",
                        column: x => x.first_owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_bootstrap_first_business_id",
                table: "instance_bootstrap",
                column: "first_business_id");

            migrationBuilder.CreateIndex(
                name: "IX_instance_bootstrap_first_owner_user_id",
                table: "instance_bootstrap",
                column: "first_owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_bootstrap");
        }
    }
}
