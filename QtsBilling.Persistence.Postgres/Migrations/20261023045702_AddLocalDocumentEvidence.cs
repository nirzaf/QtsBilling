using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalDocumentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    aggregate_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_extension = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    replaces_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    uploaded_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    retain_until_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_evidence", x => x.id);
                    table.UniqueConstraint("AK_document_evidence_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_document_evidence_size_bounded", "size_bytes > 0 AND size_bytes <= 5242880");
                    table.CheckConstraint("ck_document_evidence_version_positive", "version_number > 0");
                    table.ForeignKey(
                        name: "FK_document_evidence_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_evidence_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_evidence_users_uploaded_by_user_id",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_business_id_aggregate_type_aggregate_id",
                table: "document_evidence",
                columns: new[] { "business_id", "aggregate_type", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_business_id_aggregate_type_aggregate_id_v~",
                table: "document_evidence",
                columns: new[] { "business_id", "aggregate_type", "aggregate_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_location_id_business_id",
                table: "document_evidence",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_replaced_by_id",
                table: "document_evidence",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_replaces_id",
                table: "document_evidence",
                column: "replaces_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_sha256",
                table: "document_evidence",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_storage_key",
                table: "document_evidence",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_evidence_uploaded_by_user_id",
                table: "document_evidence",
                column: "uploaded_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_evidence");
        }
    }
}
