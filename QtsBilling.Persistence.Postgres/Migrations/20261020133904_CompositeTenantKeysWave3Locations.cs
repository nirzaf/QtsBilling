using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave3Locations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_device_registrations_business_locations_location_id",
                table: "device_registrations");

            migrationBuilder.DropForeignKey(
                name: "FK_expenses_business_locations_location_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_business_locations_location_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_lines_business_locations_location_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "IX_journal_lines_location_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "IX_invoices_location_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_location_id",
                table: "expenses");

            migrationBuilder.DropIndex(
                name: "IX_device_registrations_location_id",
                table: "device_registrations");

            migrationBuilder.CreateIndex(
                name: "IX_journal_lines_location_id_business_id",
                table: "journal_lines",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_location_id_business_id",
                table: "invoices",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_location_id_business_id",
                table: "expenses",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_device_registrations_location_id_business_id",
                table: "device_registrations",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_device_registrations_business_locations_location_id_busines~",
                table: "device_registrations",
                columns: new[] { "location_id", "business_id" },
                principalTable: "business_locations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_business_locations_location_id_business_id",
                table: "expenses",
                columns: new[] { "location_id", "business_id" },
                principalTable: "business_locations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_business_locations_location_id_business_id",
                table: "invoices",
                columns: new[] { "location_id", "business_id" },
                principalTable: "business_locations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_lines_business_locations_location_id_business_id",
                table: "journal_lines",
                columns: new[] { "location_id", "business_id" },
                principalTable: "business_locations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_device_registrations_business_locations_location_id_busines~",
                table: "device_registrations");

            migrationBuilder.DropForeignKey(
                name: "FK_expenses_business_locations_location_id_business_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_business_locations_location_id_business_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_lines_business_locations_location_id_business_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "IX_journal_lines_location_id_business_id",
                table: "journal_lines");

            migrationBuilder.DropIndex(
                name: "IX_invoices_location_id_business_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_location_id_business_id",
                table: "expenses");

            migrationBuilder.DropIndex(
                name: "IX_device_registrations_location_id_business_id",
                table: "device_registrations");

            migrationBuilder.CreateIndex(
                name: "IX_journal_lines_location_id",
                table: "journal_lines",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_location_id",
                table: "invoices",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_location_id",
                table: "expenses",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_registrations_location_id",
                table: "device_registrations",
                column: "location_id");

            migrationBuilder.AddForeignKey(
                name: "FK_device_registrations_business_locations_location_id",
                table: "device_registrations",
                column: "location_id",
                principalTable: "business_locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_business_locations_location_id",
                table: "expenses",
                column: "location_id",
                principalTable: "business_locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_business_locations_location_id",
                table: "invoices",
                column: "location_id",
                principalTable: "business_locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_lines_business_locations_location_id",
                table: "journal_lines",
                column: "location_id",
                principalTable: "business_locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
