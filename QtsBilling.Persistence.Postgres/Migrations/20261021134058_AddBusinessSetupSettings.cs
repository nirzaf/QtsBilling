using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessSetupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "fiscal_year_start_month",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "invoice_padding",
                table: "business_accounting_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "invoice_prefix",
                table: "business_accounting_settings",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "INV-");

            migrationBuilder.AddCheckConstraint(
                name: "ck_businesses_fiscal_year_start_month",
                table: "businesses",
                sql: "fiscal_year_start_month BETWEEN 1 AND 12");

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_accounting_settings_padding",
                table: "business_accounting_settings",
                sql: "invoice_padding BETWEEN 3 AND 12");

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_accounting_settings_prefix",
                table: "business_accounting_settings",
                sql: "char_length(invoice_prefix) BETWEEN 1 AND 12");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_businesses_fiscal_year_start_month",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_business_accounting_settings_padding",
                table: "business_accounting_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_business_accounting_settings_prefix",
                table: "business_accounting_settings");

            migrationBuilder.DropColumn(
                name: "fiscal_year_start_month",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "invoice_padding",
                table: "business_accounting_settings");

            migrationBuilder.DropColumn(
                name: "invoice_prefix",
                table: "business_accounting_settings");
        }
    }
}
