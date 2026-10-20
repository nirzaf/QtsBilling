using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiBusinessDeviceSecurityAndCommandReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "preferred_business_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "all_locations",
                table: "business_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "authorization_revision",
                table: "business_memberships",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<decimal>(
                name: "max_discount_rate",
                table: "business_memberships",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                defaultValue: 1.000000m);

            migrationBuilder.CreateTable(
                name: "business_membership_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_membership_locations", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_membership_locations_business_locations_location_i~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_membership_locations_business_memberships_membersh~",
                        columns: x => new { x.membership_id, x.business_id },
                        principalTable: "business_memberships",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_membership_locations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "command_idempotency_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_idempotency_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_command_idempotency_receipts_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_enrolment_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    consumed_by_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_enrolment_challenges", x => x.id);
                    table.UniqueConstraint("AK_device_enrolment_challenges_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "FK_device_enrolment_challenges_business_locations_location_id_~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_enrolment_challenges_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_enrolment_challenges_device_registrations_consumed_b~",
                        columns: x => new { x.consumed_by_device_id, x.business_id },
                        principalTable: "device_registrations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_enrolment_challenges_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_preferred_business_id",
                table: "users",
                column: "preferred_business_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_memberships_max_discount_rate",
                table: "business_memberships",
                sql: "max_discount_rate >= 0 AND max_discount_rate <= 1");

            migrationBuilder.CreateIndex(
                name: "IX_business_membership_locations_business_id_membership_id_loc~",
                table: "business_membership_locations",
                columns: new[] { "business_id", "membership_id", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_business_membership_locations_location_id_business_id",
                table: "business_membership_locations",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_membership_locations_membership_id_business_id",
                table: "business_membership_locations",
                columns: new[] { "membership_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_command_idempotency_receipts_business_id_scope_idempotency_~",
                table: "command_idempotency_receipts",
                columns: new[] { "business_id", "scope", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_enrolment_challenges_business_id_location_id_expires~",
                table: "device_enrolment_challenges",
                columns: new[] { "business_id", "location_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_device_enrolment_challenges_challenge_hash",
                table: "device_enrolment_challenges",
                column: "challenge_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_enrolment_challenges_consumed_by_device_id_business_~",
                table: "device_enrolment_challenges",
                columns: new[] { "consumed_by_device_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_device_enrolment_challenges_created_by_user_id",
                table: "device_enrolment_challenges",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_enrolment_challenges_location_id_business_id",
                table: "device_enrolment_challenges",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_users_businesses_preferred_business_id",
                table: "users",
                column: "preferred_business_id",
                principalTable: "businesses",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_bump_membership_auth_revision()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.role IS DISTINCT FROM NEW.role
                       OR OLD.is_active IS DISTINCT FROM NEW.is_active
                       OR OLD.all_locations IS DISTINCT FROM NEW.all_locations
                       OR OLD.max_discount_rate IS DISTINCT FROM NEW.max_discount_rate THEN
                        NEW.authorization_revision := OLD.authorization_revision + 1;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_business_membership_auth_revision
                BEFORE UPDATE OF role, is_active, all_locations, max_discount_rate
                ON business_memberships
                FOR EACH ROW EXECUTE FUNCTION qts_bump_membership_auth_revision();

                CREATE OR REPLACE FUNCTION qts_bump_membership_location_revision()
                RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    old_membership_id uuid;
                    old_business_id uuid;
                    new_membership_id uuid;
                    new_business_id uuid;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        old_membership_id := OLD.membership_id;
                        old_business_id := OLD.business_id;
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        new_membership_id := NEW.membership_id;
                        new_business_id := NEW.business_id;
                    END IF;

                    IF old_membership_id IS NOT NULL
                       AND (new_membership_id IS NULL
                            OR old_membership_id IS DISTINCT FROM new_membership_id
                            OR old_business_id IS DISTINCT FROM new_business_id
                            OR OLD.location_id IS DISTINCT FROM NEW.location_id) THEN
                        UPDATE business_memberships
                        SET authorization_revision = authorization_revision + 1,
                            version = version + 1,
                            updated_at_utc = now()
                        WHERE id = old_membership_id AND business_id = old_business_id;
                    END IF;

                    IF new_membership_id IS NOT NULL
                       AND (old_membership_id IS NULL
                            OR old_membership_id IS DISTINCT FROM new_membership_id
                            OR old_business_id IS DISTINCT FROM new_business_id
                            OR OLD.location_id IS DISTINCT FROM NEW.location_id) THEN
                        UPDATE business_memberships
                        SET authorization_revision = authorization_revision + 1,
                            version = version + 1,
                            updated_at_utc = now()
                        WHERE id = new_membership_id AND business_id = new_business_id;
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER trg_business_membership_location_auth_revision
                AFTER INSERT OR UPDATE OR DELETE ON business_membership_locations
                FOR EACH ROW EXECUTE FUNCTION qts_bump_membership_location_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_businesses_preferred_business_id",
                table: "users");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_business_membership_location_auth_revision ON business_membership_locations;
                DROP FUNCTION IF EXISTS qts_bump_membership_location_revision();
                DROP TRIGGER IF EXISTS trg_business_membership_auth_revision ON business_memberships;
                DROP FUNCTION IF EXISTS qts_bump_membership_auth_revision();
                """);

            migrationBuilder.DropTable(
                name: "business_membership_locations");

            migrationBuilder.DropTable(
                name: "command_idempotency_receipts");

            migrationBuilder.DropTable(
                name: "device_enrolment_challenges");

            migrationBuilder.DropIndex(
                name: "IX_users_preferred_business_id",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_business_memberships_max_discount_rate",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "preferred_business_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "all_locations",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "authorization_revision",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "max_discount_rate",
                table: "business_memberships");
        }
    }
}
