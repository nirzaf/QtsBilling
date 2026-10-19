using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class HardenPurchaseOrderLineReceiptGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_purchase_order_lines_quantities",
                table: "purchase_order_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_purchase_order_lines_quantities",
                table: "purchase_order_lines",
                sql: "quantity_ordered > 0 AND quantity_received >= 0 AND quantity_received <= quantity_ordered AND unit_cost >= 0");

            // Receipts must record received quantities on placed orders (QB-12), so the draft-only guard is
            // narrowed: once the order left draft the only permitted change is the receipt bookkeeping -
            // ordered terms, prices and snapshots stay frozen.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_purchase_order_lines_draft_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                    v_po_id  uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_po_id := OLD.purchase_order_id;
                    ELSE
                        v_po_id := NEW.purchase_order_id;
                    END IF;

                    SELECT status INTO v_status FROM purchase_orders WHERE id = v_po_id;

                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        IF TG_OP = 'UPDATE'
                           AND NEW.purchase_order_id IS NOT DISTINCT FROM OLD.purchase_order_id
                           AND NEW.line_no IS NOT DISTINCT FROM OLD.line_no
                           AND NEW.variant_id IS NOT DISTINCT FROM OLD.variant_id
                           AND NEW.product_name_snapshot IS NOT DISTINCT FROM OLD.product_name_snapshot
                           AND NEW.sku_snapshot IS NOT DISTINCT FROM OLD.sku_snapshot
                           AND NEW.quantity_ordered IS NOT DISTINCT FROM OLD.quantity_ordered
                           AND NEW.unit_cost IS NOT DISTINCT FROM OLD.unit_cost
                           AND NEW.discount_amount IS NOT DISTINCT FROM OLD.discount_amount
                           AND NEW.tax_rate IS NOT DISTINCT FROM OLD.tax_rate
                           AND NEW.tax_amount IS NOT DISTINCT FROM OLD.tax_amount
                           AND NEW.line_total IS NOT DISTINCT FROM OLD.line_total
                        THEN
                            RETURN NEW;
                        END IF;

                        RAISE EXCEPTION 'Purchase-order lines are immutable once the order leaves draft (current status: %).',
                            COALESCE(v_status, 'missing')
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_purchase_order_lines_quantities",
                table: "purchase_order_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_purchase_order_lines_quantities",
                table: "purchase_order_lines",
                sql: "quantity_ordered > 0 AND quantity_received >= 0 AND unit_cost >= 0");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_purchase_order_lines_draft_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                    v_po_id  uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_po_id := OLD.purchase_order_id;
                    ELSE
                        v_po_id := NEW.purchase_order_id;
                    END IF;

                    SELECT status INTO v_status FROM purchase_orders WHERE id = v_po_id;

                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Purchase-order lines are immutable once the order leaves draft (current status: %).',
                            COALESCE(v_status, 'missing')
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
