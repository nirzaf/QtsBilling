using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <summary>
    /// Database-level integrity guards the EF model cannot express (specification §3.4/§3.5):
    /// deferred journal-balance validation at commit, movement-derived inventory balances, and
    /// append-only enforcement for ledgers, movements and audit/idempotency tables.
    /// </summary>
    public partial class AddIntegrityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Append-only tables reject UPDATE/DELETE even if application code (or an operator) tries.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_forbid_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Table % is append-only; % is not permitted (specification section 3.1/3.4).',
                        TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            foreach (var table in new[]
                     {
                         "inventory_movements",
                         "journal_lines",
                         "sale_lines",
                         "cash_register_movements",
                         "audit_events",
                         "processed_operations",
                         "sync_changes",
                         "legacy_identifier_map",
                     })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_{table}_append_only
                        BEFORE UPDATE OR DELETE ON {table}
                        FOR EACH ROW EXECUTE FUNCTION qts_forbid_mutation();
                    """);
            }

            // 2. Posted journal entries must balance — validated at COMMIT (deferred constraint trigger),
            //    so multi-statement posting inside one transaction stays atomic and all-or-nothing.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_check_journal_entry_balanced(p_entry_id uuid) RETURNS void AS $$
                DECLARE
                    v_status     text;
                    v_debits     numeric(18,4);
                    v_credits    numeric(18,4);
                    v_hdr_debit  numeric(18,4);
                    v_hdr_credit numeric(18,4);
                    v_line_count integer;
                BEGIN
                    SELECT status, total_debit, total_credit
                      INTO v_status, v_hdr_debit, v_hdr_credit
                      FROM journal_entries
                     WHERE id = p_entry_id;

                    IF NOT FOUND THEN
                        RETURN;
                    END IF;

                    SELECT COUNT(*), COALESCE(SUM(debit_amount), 0), COALESCE(SUM(credit_amount), 0)
                      INTO v_line_count, v_debits, v_credits
                      FROM journal_lines
                     WHERE journal_entry_id = p_entry_id;

                    IF v_status = 'Posted' THEN
                        IF v_line_count < 2 THEN
                            RAISE EXCEPTION 'Journal entry % must have at least two lines when posted.', p_entry_id
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF v_debits <> v_credits THEN
                            RAISE EXCEPTION 'Journal entry % is unbalanced at commit: debits % <> credits %.',
                                p_entry_id, v_debits, v_credits
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF v_hdr_debit <> v_debits OR v_hdr_credit <> v_credits THEN
                            RAISE EXCEPTION 'Journal entry % header totals (%) do not match its lines (%).',
                                p_entry_id, v_hdr_debit || '/' || v_hdr_credit, v_debits || '/' || v_credits
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_journal_lines_balance_guard() RETURNS trigger AS $$
                DECLARE
                    v_entry_id uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_entry_id := OLD.journal_entry_id;
                    ELSE
                        v_entry_id := NEW.journal_entry_id;
                    END IF;

                    PERFORM qts_check_journal_entry_balanced(v_entry_id);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_journal_entries_balance_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RETURN NULL;
                    END IF;

                    PERFORM qts_check_journal_entry_balanced(NEW.id);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_journal_lines_balanced
                    AFTER INSERT OR UPDATE OR DELETE ON journal_lines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_journal_lines_balance_guard();

                CREATE CONSTRAINT TRIGGER trg_journal_entries_balanced
                    AFTER INSERT OR UPDATE ON journal_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_journal_entries_balance_guard();
                """);

            // 3. Inventory balances are always the movement sum — checked at COMMIT from both sides.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_check_inventory_movement_sum(p_business_id uuid, p_location_id uuid, p_variant_id uuid) RETURNS numeric AS $$
                DECLARE
                    v_sum numeric(18,6);
                BEGIN
                    SELECT COALESCE(SUM(quantity_delta), 0)
                      INTO v_sum
                      FROM inventory_movements
                     WHERE business_id = p_business_id
                       AND location_id = p_location_id
                       AND variant_id = p_variant_id;

                    RETURN v_sum;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_inventory_balance_guard() RETURNS trigger AS $$
                DECLARE
                    v_sum   numeric(18,6);
                    v_row   inventory_balances%ROWTYPE;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_sum := qts_check_inventory_movement_sum(OLD.business_id, OLD.location_id, OLD.variant_id);
                        IF v_sum <> 0 THEN
                            RAISE EXCEPTION 'Inventory balance for business %, location %, variant % cannot be deleted: movements still total %.',
                                OLD.business_id, OLD.location_id, OLD.variant_id, v_sum
                                USING ERRCODE = 'check_violation';
                        END IF;

                        RETURN NULL;
                    END IF;

                    v_row := NEW;
                    v_sum := qts_check_inventory_movement_sum(v_row.business_id, v_row.location_id, v_row.variant_id);

                    IF v_row.on_hand <> v_sum THEN
                        RAISE EXCEPTION 'Inventory balance for business %, location %, variant % is %, but movements total % (balances are movement-derived).',
                            v_row.business_id, v_row.location_id, v_row.variant_id, v_row.on_hand, v_sum
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_inventory_movement_guard() RETURNS trigger AS $$
                DECLARE
                    v_sum    numeric(18,6);
                    v_onhand numeric(18,6);
                BEGIN
                    v_sum := qts_check_inventory_movement_sum(NEW.business_id, NEW.location_id, NEW.variant_id);

                    SELECT b.on_hand
                      INTO v_onhand
                      FROM inventory_balances b
                     WHERE b.business_id = NEW.business_id
                       AND b.location_id = NEW.location_id
                       AND b.variant_id = NEW.variant_id;

                    IF NOT FOUND THEN
                        v_onhand := 0;
                    END IF;

                    IF v_onhand <> v_sum THEN
                        RAISE EXCEPTION 'Inventory movement for business %, location %, variant % leaves balance % against movement total % (every movement must update its balance row in the same transaction).',
                            NEW.business_id, NEW.location_id, NEW.variant_id, v_onhand, v_sum
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_inventory_balances_derived
                    AFTER INSERT OR UPDATE OR DELETE ON inventory_balances
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_inventory_balance_guard();

                CREATE CONSTRAINT TRIGGER trg_inventory_movements_applied
                    AFTER INSERT ON inventory_movements
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_inventory_movement_guard();
                """);

            // 4. Draft documents may be edited; once issued/ordered their lines become immutable (§3.7).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_invoice_lines_draft_guard() RETURNS trigger AS $$
                DECLARE
                    v_status     text;
                    v_invoice_id uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_invoice_id := OLD.invoice_id;
                    ELSE
                        v_invoice_id := NEW.invoice_id;
                    END IF;

                    SELECT status INTO v_status FROM invoices WHERE id = v_invoice_id;

                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Invoice lines are immutable once the invoice leaves draft (current status: %).',
                            COALESCE(v_status, 'missing')
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

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

                CREATE TRIGGER trg_invoice_lines_draft_only
                    BEFORE UPDATE OR DELETE ON invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_invoice_lines_draft_guard();

                CREATE TRIGGER trg_purchase_order_lines_draft_only
                    BEFORE UPDATE OR DELETE ON purchase_order_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_purchase_order_lines_draft_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_purchase_order_lines_draft_only ON purchase_order_lines;
                DROP TRIGGER IF EXISTS trg_invoice_lines_draft_only ON invoice_lines;
                DROP TRIGGER IF EXISTS trg_inventory_movements_applied ON inventory_movements;
                DROP TRIGGER IF EXISTS trg_inventory_balances_derived ON inventory_balances;
                DROP TRIGGER IF EXISTS trg_journal_entries_balanced ON journal_entries;
                DROP TRIGGER IF EXISTS trg_journal_lines_balanced ON journal_lines;
                DROP FUNCTION IF EXISTS qts_inventory_movement_guard();
                DROP FUNCTION IF EXISTS qts_inventory_balance_guard();
                DROP FUNCTION IF EXISTS qts_check_inventory_movement_sum(uuid, uuid, uuid);
                DROP FUNCTION IF EXISTS qts_purchase_order_lines_draft_guard();
                DROP FUNCTION IF EXISTS qts_invoice_lines_draft_guard();
                DROP FUNCTION IF EXISTS qts_journal_entries_balance_guard();
                DROP FUNCTION IF EXISTS qts_journal_lines_balance_guard();
                DROP FUNCTION IF EXISTS qts_check_journal_entry_balanced(uuid);
                DROP FUNCTION IF EXISTS qts_forbid_mutation();
                """);

            foreach (var table in new[]
                     {
                         "inventory_movements",
                         "journal_lines",
                         "sale_lines",
                         "cash_register_movements",
                         "audit_events",
                         "processed_operations",
                         "sync_changes",
                         "legacy_identifier_map",
                     })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_append_only ON {table};");
            }
        }
    }
}
