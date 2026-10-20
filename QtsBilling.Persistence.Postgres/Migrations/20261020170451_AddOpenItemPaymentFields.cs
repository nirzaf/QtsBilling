using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenItemPaymentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "bank_account_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bounce_reason",
                table: "payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bounced_at_utc",
                table: "payments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cleared_at_utc",
                table: "payments",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "customer_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unallocated_amount",
                table: "payments",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "amount_paid",
                table: "invoices",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "balance_due",
                table: "invoices",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "credited_amount",
                table: "invoices",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "credit_limit",
                table: "customers",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "payment_terms_days",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                WITH payment_totals AS (
                    SELECT business_id, invoice_id, SUM(amount) AS amount_paid
                    FROM payments
                    WHERE status = 'Confirmed' AND invoice_id IS NOT NULL
                    GROUP BY business_id, invoice_id
                ), credit_totals AS (
                    SELECT business_id, invoice_id, SUM(amount) AS credited_amount
                    FROM credit_notes
                    GROUP BY business_id, invoice_id
                ), totals AS (
                    SELECT invoice.id, invoice.status, invoice.total_amount,
                           COALESCE(payment_totals.amount_paid, 0) AS amount_paid,
                           COALESCE(credit_totals.credited_amount, 0) AS credited_amount
                    FROM invoices invoice
                    LEFT JOIN payment_totals ON payment_totals.business_id = invoice.business_id AND payment_totals.invoice_id = invoice.id
                    LEFT JOIN credit_totals ON credit_totals.business_id = invoice.business_id AND credit_totals.invoice_id = invoice.id
                )
                UPDATE invoices invoice
                SET amount_paid = totals.amount_paid,
                    credited_amount = LEAST(
                        GREATEST(totals.total_amount - totals.amount_paid, 0),
                        CASE WHEN totals.status = 'Void'
                             THEN GREATEST(totals.total_amount, totals.credited_amount)
                             ELSE totals.credited_amount END),
                    balance_due = GREATEST(
                        totals.total_amount
                        - totals.amount_paid
                        - LEAST(
                            GREATEST(totals.total_amount - totals.amount_paid, 0),
                            CASE WHEN totals.status = 'Void'
                                 THEN GREATEST(totals.total_amount, totals.credited_amount)
                                 ELSE totals.credited_amount END), 0)
                FROM totals
                WHERE invoice.id = totals.id;
                """);

            migrationBuilder.CreateTable(
                name: "payment_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocated_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    allocated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_allocations", x => x.id);
                    table.CheckConstraint("ck_payment_allocations_amount_positive", "allocated_amount > 0");
                    table.ForeignKey(
                        name: "FK_payment_allocations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_allocations_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_allocations_payments_payment_id_business_id",
                        columns: x => new { x.payment_id, x.business_id },
                        principalTable: "payments",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payments_bank_account_id_business_id",
                table: "payments",
                columns: new[] { "bank_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_customer_id_business_id",
                table: "payments",
                columns: new[] { "customer_id", "business_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_unallocated_amount",
                table: "payments",
                sql: "unallocated_amount >= 0 AND unallocated_amount <= amount");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices",
                sql: "balance_due = total_amount - amount_paid - credited_amount AND balance_due >= 0 AND amount_paid >= 0 AND credited_amount >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_credit_limit",
                table: "customers",
                sql: "credit_limit IS NULL OR credit_limit >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_payment_terms",
                table: "customers",
                sql: "payment_terms_days >= 0 AND payment_terms_days <= 3650");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_business_id_invoice_id",
                table: "payment_allocations",
                columns: new[] { "business_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_business_id_payment_id",
                table: "payment_allocations",
                columns: new[] { "business_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_invoice_id_business_id",
                table: "payment_allocations",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_payment_id_business_id",
                table: "payment_allocations",
                columns: new[] { "payment_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_payments_accounts_bank_account_id_business_id",
                table: "payments",
                columns: new[] { "bank_account_id", "business_id" },
                principalTable: "accounts",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_customers_customer_id_business_id",
                table: "payments",
                columns: new[] { "customer_id", "business_id" },
                principalTable: "customers",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION qts_payment_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Pending', 'Confirmed', 'Bounced', 'Refunded', 'Voided') THEN
                        RAISE EXCEPTION 'Payment status % is not a known payment state.', NEW.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status <> 'Pending' THEN
                            RAISE EXCEPTION 'Payment % is % and cannot be deleted.', OLD.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN OLD;
                    END IF;

                    IF TG_OP = 'UPDATE' AND OLD.status = 'Pending'
                       AND NEW.status NOT IN ('Pending', 'Confirmed', 'Bounced', 'Voided') THEN
                        RAISE EXCEPTION 'Pending payment % must be confirmed, bounced or voided.', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'UPDATE' AND OLD.status <> 'Pending' THEN
                        IF NEW.status = 'Pending' THEN
                            RAISE EXCEPTION 'Payment % is % and cannot be rewound to pending.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF NEW.amount IS DISTINCT FROM OLD.amount
                           OR NEW.currency IS DISTINCT FROM OLD.currency
                           OR NEW.sale_id IS DISTINCT FROM OLD.sale_id
                           OR NEW.invoice_id IS DISTINCT FROM OLD.invoice_id
                           OR NEW.customer_id IS DISTINCT FROM OLD.customer_id
                           OR NEW.bank_account_id IS DISTINCT FROM OLD.bank_account_id
                           OR NEW.method IS DISTINCT FROM OLD.method
                           OR NEW.cash_received IS DISTINCT FROM OLD.cash_received
                           OR NEW.reference IS DISTINCT FROM OLD.reference
                           OR NEW.psp_reference IS DISTINCT FROM OLD.psp_reference
                           OR NEW.card_brand IS DISTINCT FROM OLD.card_brand
                           OR NEW.card_last4 IS DISTINCT FROM OLD.card_last4
                           OR NEW.received_at_utc IS DISTINCT FROM OLD.received_at_utc
                           OR NEW.cash_register_session_id IS DISTINCT FROM OLD.cash_register_session_id
                           OR (NEW.unallocated_amount IS DISTINCT FROM OLD.unallocated_amount
                               AND NOT (OLD.status = 'Confirmed'
                                        AND NEW.status = 'Confirmed'
                                        AND OLD.customer_id IS NOT NULL
                                        AND NEW.customer_id = OLD.customer_id
                                        AND NEW.unallocated_amount < OLD.unallocated_amount))
                           OR NEW.cleared_at_utc IS DISTINCT FROM OLD.cleared_at_utc
                           OR NEW.bounced_at_utc IS DISTINCT FROM OLD.bounced_at_utc
                           OR NEW.bounce_reason IS DISTINCT FROM OLD.bounce_reason THEN
                            RAISE EXCEPTION 'Payment % is %; its financial terms and clearing result are immutable.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_payment_allocations_immutable
                    BEFORE UPDATE OR DELETE ON payment_allocations
                    FOR EACH ROW EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_payment_allocations_no_truncate
                    BEFORE TRUNCATE ON payment_allocations
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_payment_allocations_no_truncate ON payment_allocations; DROP TRIGGER IF EXISTS trg_payment_allocations_immutable ON payment_allocations;");
            migrationBuilder.DropForeignKey(
                name: "FK_payments_accounts_bank_account_id_business_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_customers_customer_id_business_id",
                table: "payments");

            migrationBuilder.DropTable(
                name: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_payments_bank_account_id_business_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_customer_id_business_id",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_unallocated_amount",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_balance_math",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_credit_limit",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_payment_terms",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "bounce_reason",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "bounced_at_utc",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "cleared_at_utc",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "unallocated_amount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "amount_paid",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "balance_due",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "credited_amount",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "credit_limit",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "payment_terms_days",
                table: "customers");
        }
    }
}
