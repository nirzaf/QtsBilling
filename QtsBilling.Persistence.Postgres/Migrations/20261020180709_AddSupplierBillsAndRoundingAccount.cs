using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBillsAndRoundingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "rounding_account_id",
                table: "business_accounting_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_purchase_receipt_lines_id_business_id",
                table: "purchase_receipt_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateTable(
                name: "supplier_bills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bill_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    supplier_invoice_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    bill_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    rounding_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount_paid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    balance_due = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_bills", x => x.id);
                    table.UniqueConstraint("AK_supplier_bills_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_supplier_bills_amounts", "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND amount_paid >= 0 AND balance_due >= 0 AND balance_due = total_amount - amount_paid");
                    table.ForeignKey(
                        name: "FK_supplier_bills_business_locations_location_id_business_id",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bills_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bills_journal_entries_journal_entry_id_business_id",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bills_purchase_orders_purchase_order_id_business_id",
                        columns: x => new { x.purchase_order_id, x.business_id },
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bills_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payments", x => x.id);
                    table.UniqueConstraint("AK_supplier_payments_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_supplier_payments_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "FK_supplier_payments_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_payments_journal_entries_journal_entry_id_business~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_payments_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_bill_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    purchase_receipt_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity_billed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_bill_lines", x => x.id);
                    table.CheckConstraint("ck_supplier_bill_lines_amounts", "quantity_billed > 0 AND unit_cost >= 0 AND discount_amount >= 0 AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND line_total >= 0 AND net_amount >= 0");
                    table.ForeignKey(
                        name: "FK_supplier_bill_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bill_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bill_lines_purchase_receipt_lines_purchase_receipt~",
                        columns: x => new { x.purchase_receipt_line_id, x.business_id },
                        principalTable: "purchase_receipt_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_bill_lines_supplier_bills_supplier_bill_id_busines~",
                        columns: x => new { x.supplier_bill_id, x.business_id },
                        principalTable: "supplier_bills",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_payment_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocated_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payment_allocations", x => x.id);
                    table.CheckConstraint("ck_supplier_payment_allocations_amount_positive", "allocated_amount > 0");
                    table.ForeignKey(
                        name: "FK_supplier_payment_allocations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_payment_allocations_supplier_bills_supplier_bill_i~",
                        columns: x => new { x.supplier_bill_id, x.business_id },
                        principalTable: "supplier_bills",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_payment_allocations_supplier_payments_supplier_pay~",
                        columns: x => new { x.supplier_payment_id, x.business_id },
                        principalTable: "supplier_payments",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_rounding_account_id_business_id",
                table: "business_accounting_settings",
                columns: new[] { "rounding_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_business_id_purchase_receipt_line_id",
                table: "supplier_bill_lines",
                columns: new[] { "business_id", "purchase_receipt_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_purchase_receipt_line_id_business_id",
                table: "supplier_bill_lines",
                columns: new[] { "purchase_receipt_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_supplier_bill_id_business_id",
                table: "supplier_bill_lines",
                columns: new[] { "supplier_bill_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_supplier_bill_id_line_no",
                table: "supplier_bill_lines",
                columns: new[] { "supplier_bill_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_variant_id_business_id",
                table: "supplier_bill_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_business_id_bill_number",
                table: "supplier_bills",
                columns: new[] { "business_id", "bill_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_business_id_supplier_id_status_due_date",
                table: "supplier_bills",
                columns: new[] { "business_id", "supplier_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_journal_entry_id_business_id",
                table: "supplier_bills",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_location_id_business_id",
                table: "supplier_bills",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_purchase_order_id_business_id",
                table: "supplier_bills",
                columns: new[] { "purchase_order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_supplier_id_business_id",
                table: "supplier_bills",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_bills_business_idempotency_key",
                table: "supplier_bills",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_supplier_bills_business_supplier_reference",
                table: "supplier_bills",
                columns: new[] { "business_id", "supplier_id", "supplier_invoice_reference" },
                unique: true,
                filter: "supplier_invoice_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_business_id_supplier_bill_id",
                table: "supplier_payment_allocations",
                columns: new[] { "business_id", "supplier_bill_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_bill_id_business_id",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_bill_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_business_id",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_payment_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payment_allocations_supplier_payment_id_supplier_b~",
                table: "supplier_payment_allocations",
                columns: new[] { "supplier_payment_id", "supplier_bill_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_business_id_supplier_id_paid_on",
                table: "supplier_payments",
                columns: new[] { "business_id", "supplier_id", "paid_on" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_journal_entry_id_business_id",
                table: "supplier_payments",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_supplier_id_business_id",
                table: "supplier_payments",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_business_accounting_settings_accounts_rounding_account_id_b~",
                table: "business_accounting_settings",
                columns: new[] { "rounding_account_id", "business_id" },
                principalTable: "accounts",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_supplier_bill_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft' THEN
                            RAISE EXCEPTION 'Supplier bills must be created as drafts.' USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status = 'Draft' THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION 'Issued supplier bills are immutable; post a correcting document.' USING ERRCODE = 'check_violation';
                    END IF;

                    IF OLD.status = 'Draft' THEN
                        RETURN NEW;
                    END IF;

                    IF OLD.status IN ('Issued', 'PartiallyPaid')
                       AND NEW.status IN ('Issued', 'PartiallyPaid', 'Paid')
                       AND NEW.business_id = OLD.business_id
                       AND NEW.supplier_id = OLD.supplier_id
                       AND NEW.location_id = OLD.location_id
                       AND NEW.purchase_order_id IS NOT DISTINCT FROM OLD.purchase_order_id
                       AND NEW.bill_number = OLD.bill_number
                       AND NEW.supplier_invoice_reference IS NOT DISTINCT FROM OLD.supplier_invoice_reference
                       AND NEW.bill_date = OLD.bill_date
                       AND NEW.due_date IS NOT DISTINCT FROM OLD.due_date
                       AND NEW.currency = OLD.currency
                       AND NEW.subtotal_amount = OLD.subtotal_amount
                       AND NEW.discount_amount = OLD.discount_amount
                       AND NEW.tax_amount = OLD.tax_amount
                       AND NEW.rounding_amount = OLD.rounding_amount
                       AND NEW.total_amount = OLD.total_amount
                       AND NEW.journal_entry_id IS NOT DISTINCT FROM OLD.journal_entry_id
                       AND NEW.issued_at_utc IS NOT DISTINCT FROM OLD.issued_at_utc
                       AND NEW.amount_paid >= OLD.amount_paid
                       AND NEW.amount_paid <= NEW.total_amount
                       AND NEW.balance_due = NEW.total_amount - NEW.amount_paid
                       AND ((NEW.status = 'Issued' AND NEW.amount_paid = 0 AND NEW.balance_due = NEW.total_amount)
                            OR (NEW.status = 'PartiallyPaid' AND NEW.amount_paid > 0 AND NEW.balance_due > 0)
                            OR (NEW.status = 'Paid' AND NEW.balance_due = 0)) THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'Supplier bill commercial terms and terminal states are immutable.' USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_supplier_bills_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON supplier_bills
                    FOR EACH ROW EXECUTE FUNCTION qts_supplier_bill_guard();

                CREATE OR REPLACE FUNCTION qts_supplier_bill_lines_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT status INTO v_status FROM supplier_bills WHERE id = NEW.supplier_bill_id AND business_id = NEW.business_id;
                        IF v_status IS DISTINCT FROM 'Draft' THEN
                            RAISE EXCEPTION 'Supplier bill lines may only be added to a draft bill.' USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    SELECT status INTO v_status FROM supplier_bills WHERE id = OLD.supplier_bill_id AND business_id = OLD.business_id;
                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Issued supplier bill lines are immutable.' USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    SELECT status INTO v_status FROM supplier_bills WHERE id = NEW.supplier_bill_id AND business_id = NEW.business_id;
                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Supplier bill lines may only be moved to a draft bill.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_supplier_bill_lines_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON supplier_bill_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_supplier_bill_lines_guard();

                CREATE OR REPLACE FUNCTION qts_supplier_payment_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'Supplier payments are immutable financial history.' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_supplier_payments_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON supplier_payments
                    FOR EACH ROW EXECUTE FUNCTION qts_supplier_payment_guard();

                CREATE TRIGGER trg_supplier_payment_allocations_append_only
                    BEFORE UPDATE OR DELETE ON supplier_payment_allocations
                    FOR EACH ROW EXECUTE FUNCTION qts_forbid_mutation();

                CREATE TRIGGER trg_supplier_bills_no_truncate
                    BEFORE TRUNCATE ON supplier_bills
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_supplier_bill_lines_no_truncate
                    BEFORE TRUNCATE ON supplier_bill_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_supplier_payments_no_truncate
                    BEFORE TRUNCATE ON supplier_payments
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_supplier_payment_allocations_no_truncate
                    BEFORE TRUNCATE ON supplier_payment_allocations
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_supplier_payment_allocations_no_truncate ON supplier_payment_allocations;
                DROP TRIGGER IF EXISTS trg_supplier_payments_no_truncate ON supplier_payments;
                DROP TRIGGER IF EXISTS trg_supplier_bill_lines_no_truncate ON supplier_bill_lines;
                DROP TRIGGER IF EXISTS trg_supplier_bills_no_truncate ON supplier_bills;
                DROP TRIGGER IF EXISTS trg_supplier_payment_allocations_append_only ON supplier_payment_allocations;
                DROP TRIGGER IF EXISTS trg_supplier_payments_immutable ON supplier_payments;
                DROP TRIGGER IF EXISTS trg_supplier_bill_lines_immutable ON supplier_bill_lines;
                DROP TRIGGER IF EXISTS trg_supplier_bills_immutable ON supplier_bills;
                DROP FUNCTION IF EXISTS qts_supplier_payment_guard();
                DROP FUNCTION IF EXISTS qts_supplier_bill_lines_guard();
                DROP FUNCTION IF EXISTS qts_supplier_bill_guard();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_business_accounting_settings_accounts_rounding_account_id_b~",
                table: "business_accounting_settings");

            migrationBuilder.DropTable(
                name: "supplier_bill_lines");

            migrationBuilder.DropTable(
                name: "supplier_payment_allocations");

            migrationBuilder.DropTable(
                name: "supplier_bills");

            migrationBuilder.DropTable(
                name: "supplier_payments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_purchase_receipt_lines_id_business_id",
                table: "purchase_receipt_lines");

            migrationBuilder.DropIndex(
                name: "IX_business_accounting_settings_rounding_account_id_business_id",
                table: "business_accounting_settings");

            migrationBuilder.DropColumn(
                name: "rounding_account_id",
                table: "business_accounting_settings");
        }
    }
}
