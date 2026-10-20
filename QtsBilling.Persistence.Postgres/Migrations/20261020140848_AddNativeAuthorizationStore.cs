using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeAuthorizationStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "openiddict_applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    display_names = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    json_web_key_set = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    permissions = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    properties = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    redirect_uris = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    requirements = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    settings = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_scopes",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    descriptions = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    display_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    display_names = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    resources = table.Column<string>(type: "text", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_scopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    application_id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    scopes = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "FK_openiddict_authorizations_openiddict_applications_applicati~",
                        column: x => x.application_id,
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    application_id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    authorization_id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    properties = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openiddict_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_openiddict_tokens_openiddict_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "openiddict_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_openiddict_tokens_openiddict_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "openiddict_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_applications_client_id",
                table: "openiddict_applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_authorizations_application_id_status_subject_type",
                table: "openiddict_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_scopes_name",
                table: "openiddict_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_application_id_status_subject_type",
                table: "openiddict_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_authorization_id",
                table: "openiddict_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_openiddict_tokens_reference_id",
                table: "openiddict_tokens",
                column: "reference_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "openiddict_scopes");

            migrationBuilder.DropTable(
                name: "openiddict_tokens");

            migrationBuilder.DropTable(
                name: "openiddict_authorizations");

            migrationBuilder.DropTable(
                name: "openiddict_applications");
        }
    }
}
