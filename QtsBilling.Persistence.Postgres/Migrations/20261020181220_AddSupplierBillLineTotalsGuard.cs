using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBillLineTotalsGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_bills_amounts",
                table: "supplier_bills");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_bill_lines_amounts",
                table: "supplier_bill_lines");

            migrationBuilder.AddColumn<decimal>(
                name: "gross_amount",
                table: "supplier_bill_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_bills_amounts",
                table: "supplier_bills",
                sql: "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND amount_paid >= 0 AND balance_due >= 0 AND balance_due = total_amount - amount_paid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_bill_lines_amounts",
                table: "supplier_bill_lines",
                sql: "quantity_billed > 0 AND unit_cost >= 0 AND gross_amount >= 0 AND discount_amount >= 0 AND discount_amount <= gross_amount AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND line_total >= 0 AND net_amount >= 0 AND line_total = net_amount + tax_amount AND ((tax_mode = 'Inclusive' AND line_total = gross_amount - discount_amount) OR (tax_mode IN ('Exclusive', 'Exempt') AND net_amount = gross_amount - discount_amount AND (tax_mode <> 'Exempt' OR tax_amount = 0)) )");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_assert_supplier_bill_totals(p_business_id uuid, p_bill_id uuid) RETURNS void AS $$
                DECLARE
                    v_status text;
                    v_subtotal numeric(18,4);
                    v_discount numeric(18,4);
                    v_tax numeric(18,4);
                    v_rounding numeric(18,4);
                    v_total numeric(18,4);
                    v_line_count bigint;
                    v_line_subtotal numeric(18,4);
                    v_line_discount numeric(18,4);
                    v_line_tax numeric(18,4);
                    v_line_total numeric(18,4);
                BEGIN
                    SELECT status, subtotal_amount, discount_amount, tax_amount, rounding_amount, total_amount
                    INTO v_status, v_subtotal, v_discount, v_tax, v_rounding, v_total
                    FROM supplier_bills
                    WHERE id = p_bill_id AND business_id = p_business_id;
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;

                    SELECT COUNT(*), COALESCE(SUM(gross_amount), 0), COALESCE(SUM(discount_amount), 0),
                           COALESCE(SUM(tax_amount), 0), COALESCE(SUM(line_total), 0)
                    INTO v_line_count, v_line_subtotal, v_line_discount, v_line_tax, v_line_total
                    FROM supplier_bill_lines
                    WHERE supplier_bill_id = p_bill_id AND business_id = p_business_id;

                    IF v_line_count = 0
                       OR v_subtotal <> v_line_subtotal
                       OR v_discount <> v_line_discount
                       OR v_tax <> v_line_tax
                       OR v_total <> v_line_total + v_rounding THEN
                        RAISE EXCEPTION 'Supplier bill % totals do not equal its stored line snapshots.', p_bill_id
                            USING ERRCODE = 'check_violation';
                    END IF;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_supplier_bill_totals_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_TABLE_NAME = 'supplier_bill_lines' THEN
                        IF TG_OP = 'DELETE' THEN
                            PERFORM qts_assert_supplier_bill_totals(OLD.business_id, OLD.supplier_bill_id);
                        ELSE
                            PERFORM qts_assert_supplier_bill_totals(NEW.business_id, NEW.supplier_bill_id);
                            IF TG_OP = 'UPDATE'
                               AND (OLD.business_id, OLD.supplier_bill_id) IS DISTINCT FROM (NEW.business_id, NEW.supplier_bill_id) THEN
                                PERFORM qts_assert_supplier_bill_totals(OLD.business_id, OLD.supplier_bill_id);
                            END IF;
                        END IF;
                    ELSIF TG_OP = 'DELETE' THEN
                        PERFORM qts_assert_supplier_bill_totals(OLD.business_id, OLD.id);
                    ELSE
                        PERFORM qts_assert_supplier_bill_totals(NEW.business_id, NEW.id);
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_supplier_bills_totals_consistent
                    AFTER INSERT OR UPDATE OR DELETE ON supplier_bills
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_supplier_bill_totals_guard();
                CREATE CONSTRAINT TRIGGER trg_supplier_bill_lines_totals_consistent
                    AFTER INSERT OR UPDATE OR DELETE ON supplier_bill_lines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_supplier_bill_totals_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_bills_amounts",
                table: "supplier_bills");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_bill_lines_amounts",
                table: "supplier_bill_lines");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_supplier_bill_lines_totals_consistent ON supplier_bill_lines;
                DROP TRIGGER IF EXISTS trg_supplier_bills_totals_consistent ON supplier_bills;
                DROP FUNCTION IF EXISTS qts_supplier_bill_totals_guard();
                DROP FUNCTION IF EXISTS qts_assert_supplier_bill_totals(uuid, uuid);
                """);

            migrationBuilder.DropColumn(
                name: "gross_amount",
                table: "supplier_bill_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_bills_amounts",
                table: "supplier_bills",
                sql: "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND amount_paid >= 0 AND balance_due >= 0 AND balance_due = total_amount - amount_paid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_bill_lines_amounts",
                table: "supplier_bill_lines",
                sql: "quantity_billed > 0 AND unit_cost >= 0 AND discount_amount >= 0 AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND line_total >= 0 AND net_amount >= 0");
        }
    }
}
