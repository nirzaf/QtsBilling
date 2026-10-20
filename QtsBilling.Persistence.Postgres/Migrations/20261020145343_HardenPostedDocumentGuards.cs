using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <summary>
    /// Posted-document and final-state guards (specification task 09, findings F17/F19/F32):
    /// header commercial terms freeze when a document leaves draft, line triggers check both the old and
    /// the new parent (so a line cannot be moved out of an issued document), lines cannot be inserted
    /// into an issued document, journal lines can only be added while their entry is draft, and bulk
    /// TRUNCATE cannot erase append-only history.
    /// </summary>
    public partial class HardenPostedDocumentGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------- invoices
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_invoice_guard() RETURNS trigger AS $$
                DECLARE
                    v_line_count integer;
                    v_line_total numeric(18,4);
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Draft', 'Issued', 'Paid', 'PartiallyPaid', 'Void') THEN
                        RAISE EXCEPTION 'Invoice status % is not a known invoice state.', NEW.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Invoice % is % and cannot be deleted; void it or issue a credit note.', OLD.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN OLD;
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Invoices are created as drafts; % is not a legal initial status.', NEW.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD.status <> 'Draft' AND NEW.status = 'Draft' THEN
                        RAISE EXCEPTION 'Invoice % is % and cannot be rewound to draft.', NEW.id, OLD.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF OLD.status = 'Draft' AND NEW.status = 'Issued' THEN
                        SELECT COUNT(*), COALESCE(SUM(line_total), 0)
                          INTO v_line_count, v_line_total
                          FROM invoice_lines
                         WHERE invoice_id = NEW.id;

                        IF v_line_count = 0 THEN
                            RAISE EXCEPTION 'Invoice % cannot be issued without lines.', NEW.id
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF v_line_total <> NEW.total_amount THEN
                            RAISE EXCEPTION 'Invoice % header total % does not match its lines %.', NEW.id, NEW.total_amount, v_line_total
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF NEW.invoice_number IS NULL OR btrim(NEW.invoice_number) = '' THEN
                            RAISE EXCEPTION 'Invoice % cannot be issued without a number.', NEW.id
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    IF OLD.status <> 'Draft' THEN
                        IF NEW.invoice_number   IS DISTINCT FROM OLD.invoice_number
                           OR NEW.customer_id       IS DISTINCT FROM OLD.customer_id
                           OR NEW.source_sale_id    IS DISTINCT FROM OLD.source_sale_id
                           OR NEW.invoice_sequence_id IS DISTINCT FROM OLD.invoice_sequence_id
                           OR NEW.location_id       IS DISTINCT FROM OLD.location_id
                           OR NEW.issue_date        IS DISTINCT FROM OLD.issue_date
                           OR NEW.due_date          IS DISTINCT FROM OLD.due_date
                           OR NEW.currency          IS DISTINCT FROM OLD.currency
                           OR NEW.subtotal_amount   IS DISTINCT FROM OLD.subtotal_amount
                           OR NEW.discount_amount   IS DISTINCT FROM OLD.discount_amount
                           OR NEW.tax_amount        IS DISTINCT FROM OLD.tax_amount
                           OR NEW.total_amount      IS DISTINCT FROM OLD.total_amount
                        THEN
                            RAISE EXCEPTION 'Invoice % is %; its financial terms are immutable.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_invoices_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON invoices
                    FOR EACH ROW EXECUTE FUNCTION qts_invoice_guard();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_invoice_lines_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                BEGIN
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        SELECT status INTO v_status FROM invoices WHERE id = OLD.invoice_id;
                        IF v_status IS DISTINCT FROM 'Draft' THEN
                            RAISE EXCEPTION 'Invoice line % belongs to an invoice that is %; its lines are immutable.', OLD.id, COALESCE(v_status, 'missing')
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    IF TG_OP IN ('INSERT', 'UPDATE') THEN
                        SELECT status INTO v_status FROM invoices WHERE id = NEW.invoice_id;
                        IF v_status IS DISTINCT FROM 'Draft' THEN
                            RAISE EXCEPTION 'A line cannot be added to an invoice that is %.', COALESCE(v_status, 'missing')
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_invoice_lines_draft_only ON invoice_lines;
                CREATE TRIGGER trg_invoice_lines_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_invoice_lines_guard();
                """);

            // ---------------------------------------------------------------- purchase orders
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_purchase_order_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Draft', 'Ordered', 'PartiallyReceived', 'Received', 'Cancelled') THEN
                        RAISE EXCEPTION 'Purchase-order status % is not a known state.', NEW.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Purchase order % is % and cannot be deleted.', OLD.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN OLD;
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Purchase orders are created as drafts; % is not a legal initial status.', NEW.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD.status <> 'Draft' AND NEW.status = 'Draft' THEN
                        RAISE EXCEPTION 'Purchase order % is % and cannot be rewound to draft.', NEW.id, OLD.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF OLD.status <> 'Draft' THEN
                        IF NEW.supplier_id     IS DISTINCT FROM OLD.supplier_id
                           OR NEW.location_id    IS DISTINCT FROM OLD.location_id
                           OR NEW.order_number   IS DISTINCT FROM OLD.order_number
                           OR NEW.order_date     IS DISTINCT FROM OLD.order_date
                           OR NEW.expected_date  IS DISTINCT FROM OLD.expected_date
                           OR NEW.currency       IS DISTINCT FROM OLD.currency
                           OR NEW.subtotal_amount IS DISTINCT FROM OLD.subtotal_amount
                           OR NEW.discount_amount IS DISTINCT FROM OLD.discount_amount
                           OR NEW.tax_amount     IS DISTINCT FROM OLD.tax_amount
                           OR NEW.total_amount   IS DISTINCT FROM OLD.total_amount
                        THEN
                            RAISE EXCEPTION 'Purchase order % is %; its financial terms are immutable.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_purchase_orders_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON purchase_orders
                    FOR EACH ROW EXECUTE FUNCTION qts_purchase_order_guard();

                CREATE OR REPLACE FUNCTION qts_purchase_order_lines_guard() RETURNS trigger AS $$
                DECLARE
                    v_old_status text;
                    v_new_status text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        SELECT status INTO v_old_status FROM purchase_orders WHERE id = OLD.purchase_order_id;
                        IF v_old_status IS DISTINCT FROM 'Draft' THEN
                            RAISE EXCEPTION 'Purchase-order line % belongs to an order that is %; its terms are immutable.', OLD.id, COALESCE(v_old_status, 'missing')
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN OLD;
                    END IF;

                    SELECT status INTO v_new_status FROM purchase_orders WHERE id = NEW.purchase_order_id;

                    IF TG_OP = 'INSERT' THEN
                        IF v_new_status IS DISTINCT FROM 'Draft' THEN
                            RAISE EXCEPTION 'A line cannot be added to a purchase order that is %.', COALESCE(v_new_status, 'missing')
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    SELECT status INTO v_old_status FROM purchase_orders WHERE id = OLD.purchase_order_id;

                    IF NEW.purchase_order_id IS DISTINCT FROM OLD.purchase_order_id
                       AND (v_old_status IS DISTINCT FROM 'Draft' OR v_new_status IS DISTINCT FROM 'Draft')
                    THEN
                        RAISE EXCEPTION 'Purchase-order line % cannot be moved between orders once either order has left draft.', OLD.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    -- Receiving updates quantity_received on an ordered line. Every commercial term stays
                    -- frozen: only the received quantity and the timestamp may move.
                    IF v_old_status IS DISTINCT FROM 'Draft' OR v_new_status IS DISTINCT FROM 'Draft' THEN
                        IF NEW.line_no            IS DISTINCT FROM OLD.line_no
                           OR NEW.variant_id      IS DISTINCT FROM OLD.variant_id
                           OR NEW.product_name_snapshot IS DISTINCT FROM OLD.product_name_snapshot
                           OR NEW.sku_snapshot    IS DISTINCT FROM OLD.sku_snapshot
                           OR NEW.quantity_ordered IS DISTINCT FROM OLD.quantity_ordered
                           OR NEW.unit_cost       IS DISTINCT FROM OLD.unit_cost
                           OR NEW.discount_amount IS DISTINCT FROM OLD.discount_amount
                           OR NEW.tax_rate        IS DISTINCT FROM OLD.tax_rate
                           OR NEW.tax_amount      IS DISTINCT FROM OLD.tax_amount
                           OR NEW.line_total      IS DISTINCT FROM OLD.line_total
                        THEN
                            RAISE EXCEPTION 'Purchase-order line % belongs to an order that is %; only the received quantity may change.', OLD.id, COALESCE(v_old_status, 'missing')
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_purchase_order_lines_draft_only ON purchase_order_lines;
                CREATE TRIGGER trg_purchase_order_lines_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON purchase_order_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_purchase_order_lines_guard();
                """);

            // ---------------------------------------------------------------- sales and payments
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_sale_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Posted', 'Reversed', 'NeedsReview') THEN
                        RAISE EXCEPTION 'Sale status % is not a known sale state.', NEW.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Sales are never deleted; reverse the sale with a refund.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        RETURN NEW;
                    END IF;

                    IF OLD.status = 'Reversed' THEN
                        RAISE EXCEPTION 'Sale % is reversed; nothing about it may change.', OLD.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF NEW.sale_number       IS DISTINCT FROM OLD.sale_number
                       OR NEW.location_id       IS DISTINCT FROM OLD.location_id
                       OR NEW.customer_id       IS DISTINCT FROM OLD.customer_id
                       OR NEW.currency          IS DISTINCT FROM OLD.currency
                       OR NEW.subtotal_amount   IS DISTINCT FROM OLD.subtotal_amount
                       OR NEW.discount_amount   IS DISTINCT FROM OLD.discount_amount
                       OR NEW.tax_amount        IS DISTINCT FROM OLD.tax_amount
                       OR NEW.total_amount      IS DISTINCT FROM OLD.total_amount
                       OR NEW.posted_at_utc     IS DISTINCT FROM OLD.posted_at_utc
                       OR NEW.posted_by_user_id IS DISTINCT FROM OLD.posted_by_user_id
                       OR NEW.reversal_of_sale_id IS DISTINCT FROM OLD.reversal_of_sale_id
                       OR NEW.idempotency_key   IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash      IS DISTINCT FROM OLD.payload_hash
                    THEN
                        RAISE EXCEPTION 'Sale % is posted; its financial terms are immutable.', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_sales_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON sales
                    FOR EACH ROW EXECUTE FUNCTION qts_sale_guard();

                CREATE OR REPLACE FUNCTION qts_payment_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Pending', 'Confirmed', 'Refunded', 'Voided') THEN
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

                    IF TG_OP = 'UPDATE' AND OLD.status <> 'Pending' THEN
                        IF NEW.status = 'Pending' THEN
                            RAISE EXCEPTION 'Payment % is % and cannot be rewound to pending.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF NEW.amount            IS DISTINCT FROM OLD.amount
                           OR NEW.currency           IS DISTINCT FROM OLD.currency
                           OR NEW.sale_id            IS DISTINCT FROM OLD.sale_id
                           OR NEW.invoice_id         IS DISTINCT FROM OLD.invoice_id
                           OR NEW.method             IS DISTINCT FROM OLD.method
                           OR NEW.cash_received      IS DISTINCT FROM OLD.cash_received
                           OR NEW.reference          IS DISTINCT FROM OLD.reference
                           OR NEW.psp_reference      IS DISTINCT FROM OLD.psp_reference
                           OR NEW.card_brand         IS DISTINCT FROM OLD.card_brand
                           OR NEW.card_last4         IS DISTINCT FROM OLD.card_last4
                           OR NEW.received_at_utc    IS DISTINCT FROM OLD.received_at_utc
                           OR NEW.cash_register_session_id IS DISTINCT FROM OLD.cash_register_session_id
                        THEN
                            RAISE EXCEPTION 'Payment % is %; its settled amount and subject are immutable.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_payments_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON payments
                    FOR EACH ROW EXECUTE FUNCTION qts_payment_guard();
                """);

            // ---------------------------------------------------------------- journal entries
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_journal_entry_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'DELETE' AND NEW.status NOT IN ('Draft', 'Posted', 'Reversed') THEN
                        RAISE EXCEPTION 'Journal-entry status % is not a known state.', NEW.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Journal entry % is % and cannot be deleted; reverse it with a new entry.', OLD.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN OLD;
                    END IF;

                    IF TG_OP = 'UPDATE' AND OLD.status <> 'Draft' THEN
                        IF NEW.entry_number          IS DISTINCT FROM OLD.entry_number
                           OR NEW.entry_date         IS DISTINCT FROM OLD.entry_date
                           OR NEW.accounting_period_id IS DISTINCT FROM OLD.accounting_period_id
                           OR NEW.memo              IS DISTINCT FROM OLD.memo
                           OR NEW.source_type       IS DISTINCT FROM OLD.source_type
                           OR NEW.source_id         IS DISTINCT FROM OLD.source_id
                           OR NEW.currency          IS DISTINCT FROM OLD.currency
                           OR NEW.total_debit       IS DISTINCT FROM OLD.total_debit
                           OR NEW.total_credit      IS DISTINCT FROM OLD.total_credit
                           OR NEW.posted_at_utc     IS DISTINCT FROM OLD.posted_at_utc
                           OR NEW.posted_by_user_id IS DISTINCT FROM OLD.posted_by_user_id
                           OR NEW.reversal_of_entry_id IS DISTINCT FROM OLD.reversal_of_entry_id
                        THEN
                            RAISE EXCEPTION 'Journal entry % is %; its posted facts are immutable.', NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF OLD.status = 'Reversed' AND NEW.status IS DISTINCT FROM OLD.status THEN
                            RAISE EXCEPTION 'Journal entry % is reversed and cannot change status again.', NEW.id
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_journal_entries_immutable
                    BEFORE UPDATE OR DELETE ON journal_entries
                    FOR EACH ROW EXECUTE FUNCTION qts_journal_entry_guard();

                CREATE OR REPLACE FUNCTION qts_journal_lines_insert_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                BEGIN
                    SELECT status INTO v_status FROM journal_entries WHERE id = NEW.journal_entry_id;
                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'A line cannot be added to journal entry % because it is %.', NEW.journal_entry_id, COALESCE(v_status, 'missing')
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_journal_lines_insert_draft_only
                    BEFORE INSERT ON journal_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_journal_lines_insert_guard();
                """);

            // ---------------------------------------------------------------- bulk erasure
            foreach (var table in new[]
                     {
                         "invoices",
                         "sales",
                         "journal_entries",
                         "payments",
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
                    CREATE TRIGGER trg_{table}_no_truncate
                        BEFORE TRUNCATE ON {table}
                        FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
                     {
                         "invoices",
                         "sales",
                         "journal_entries",
                         "payments",
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
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_no_truncate ON {table};");
            }

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_journal_lines_insert_draft_only ON journal_lines;
                DROP TRIGGER IF EXISTS trg_journal_entries_immutable ON journal_entries;
                DROP TRIGGER IF EXISTS trg_payments_immutable ON payments;
                DROP TRIGGER IF EXISTS trg_sales_immutable ON sales;
                DROP TRIGGER IF EXISTS trg_purchase_order_lines_immutable ON purchase_order_lines;
                DROP TRIGGER IF EXISTS trg_purchase_orders_immutable ON purchase_orders;
                DROP TRIGGER IF EXISTS trg_invoice_lines_immutable ON invoice_lines;
                DROP TRIGGER IF EXISTS trg_invoices_immutable ON invoices;
                DROP FUNCTION IF EXISTS qts_journal_lines_insert_guard();
                DROP FUNCTION IF EXISTS qts_journal_entry_guard();
                DROP FUNCTION IF EXISTS qts_payment_guard();
                DROP FUNCTION IF EXISTS qts_sale_guard();
                DROP FUNCTION IF EXISTS qts_purchase_order_lines_guard();
                DROP FUNCTION IF EXISTS qts_purchase_order_guard();
                DROP FUNCTION IF EXISTS qts_invoice_lines_guard();
                DROP FUNCTION IF EXISTS qts_invoice_guard();

                -- Restore the guards the previous migration owned.
                CREATE TRIGGER trg_invoice_lines_draft_only
                    BEFORE UPDATE OR DELETE ON invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_invoice_lines_draft_guard();

                CREATE TRIGGER trg_purchase_order_lines_draft_only
                    BEFORE UPDATE OR DELETE ON purchase_order_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_purchase_order_lines_draft_guard();
                """);
        }
    }
}
