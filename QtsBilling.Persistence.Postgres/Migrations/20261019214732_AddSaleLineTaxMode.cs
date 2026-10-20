using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleLineTaxMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tax_mode",
                table: "sale_lines",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unspecified");

            // The historical lines are NOT rewritten: sale_lines is append-only, and a posted document
            // must never be repaired in place. The existing rows keep the default "Unspecified", which
            // states the truth - the composition was not recorded - and the effective composition is
            // derived from each line's own numbers when it is read
            // (QtsModels.ValueObjects.CommercialAmounts.ClassifyStoredLine).
            //
            // A previous version of this migration read the composition from the product table. That
            // was wrong twice over: the line points at a variant, not at a product, so the join matched
            // nothing; and the current catalog tax type is not the composition a historical sale was
            // priced with. The corrective migration 20261019223500_CorrectSaleLineTaxModeClassification
            // repairs the databases that ran that version.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tax_mode",
                table: "sale_lines");
        }
    }
}
