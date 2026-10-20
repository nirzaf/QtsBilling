using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CorrectSaleLineTaxModeClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default of a line that was written before the composition was recorded states the
            // truth: the composition is unknown.
            migrationBuilder.Sql("""
                ALTER TABLE sale_lines ALTER COLUMN tax_mode SET DEFAULT 'Unspecified';
                """);

            // The previous version of 20261019214732_AddSaleLineTaxMode tried to read the composition
            // from the product table. That join was wrong (the line points at a variant, not at a
            // product) and the current catalog is not the composition a historical sale was priced with,
            // so the rows were left with the default instead.
            //
            // The composition is therefore derived from each line's own numbers - the quantity, the
            // price, the discount, the rate and the tax amount it was posted with. A line whose numbers
            // match neither composition is left untouched: it is not classified, it is not guessed.
            //
            // sale_lines is append-only, so the reviewed one-time repair runs with the trigger disabled
            // and re-enabled inside this transaction: a failure rolls the whole migration back and the
            // trigger state with it.
            migrationBuilder.Sql("""
                ALTER TABLE sale_lines DISABLE TRIGGER trg_sale_lines_append_only;

                WITH classified AS (
                    SELECT
                        id,
                        CASE
                            WHEN tax_rate = 0 AND tax_amount = 0 THEN 'Exclusive'
                            WHEN abs(round((quantity * unit_price - discount_amount) * tax_rate, 4) - tax_amount) <= 0.0001
                                THEN 'Exclusive'
                            WHEN abs(round((quantity * unit_price - discount_amount) * tax_rate / (1 + tax_rate), 4) - tax_amount) <= 0.0001
                                THEN 'Inclusive'
                            ELSE 'Unspecified'
                        END AS tax_mode
                    FROM sale_lines
                )
                UPDATE sale_lines AS line
                SET tax_mode = classified.tax_mode
                FROM classified
                WHERE line.id = classified.id
                  AND line.tax_mode IS DISTINCT FROM classified.tax_mode;

                ALTER TABLE sale_lines ENABLE TRIGGER trg_sale_lines_append_only;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The classification is derived from the line's own numbers, so there is nothing to undo:
            // the rows keep the composition their numbers prove.
            migrationBuilder.Sql("""
                ALTER TABLE sale_lines ALTER COLUMN tax_mode SET DEFAULT 'Exclusive';
                """);
        }
    }
}
