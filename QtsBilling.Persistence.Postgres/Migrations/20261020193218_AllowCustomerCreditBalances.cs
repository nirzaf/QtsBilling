using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AllowCustomerCreditBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices",
                sql: "balance_due = GREATEST(0, total_amount - amount_paid - credited_amount) AND amount_paid >= 0 AND credited_amount >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices",
                sql: "balance_due = total_amount - amount_paid - credited_amount AND balance_due >= 0 AND amount_paid >= 0 AND credited_amount >= 0");
        }
    }
}
