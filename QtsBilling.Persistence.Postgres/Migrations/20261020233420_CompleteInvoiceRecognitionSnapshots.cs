using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompleteInvoiceRecognitionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: the sale-link uniqueness below must not be created over duplicate links. Raising here aborts the
            // transaction, so no column or index is added and no existing row is changed or merged.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    v_groups integer;
                    v_sample text;
                BEGIN
                    SELECT COUNT(*), string_agg(format('business %s sale %s (%s invoices)', business_id, source_sale_id, invoice_count), '; ')
                      INTO v_groups, v_sample
                      FROM (
                          SELECT business_id, source_sale_id, COUNT(*) AS invoice_count
                            FROM invoices
                           WHERE source_sale_id IS NOT NULL
                           GROUP BY business_id, source_sale_id
                          HAVING COUNT(*) > 1
                      ) duplicates;

                    IF v_groups > 0 THEN
                        RAISE EXCEPTION 'Cannot add the one-invoice-per-sale rule: % sale link(s) are shared by several invoices. First: %. Resolve the duplicates by an approved correction; nothing was changed.', v_groups, left(v_sample, 500)
                            USING ERRCODE = 'unique_violation';
                    END IF;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.AddColumn<string>(
                name: "business_legal_name_snapshot",
                table: "invoices",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "business_name_snapshot",
                table: "invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_address_snapshot",
                table: "invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_email_snapshot",
                table: "invoices",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_name_snapshot",
                table: "invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_phone_snapshot",
                table: "invoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_address_snapshot",
                table: "invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "payment_terms_days_snapshot",
                table: "invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recognition_mode",
                table: "invoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "HistoricalUnknown");

            migrationBuilder.AddColumn<long>(
                name: "product_version_snapshot",
                table: "invoice_lines",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_sale_line_id",
                table: "invoice_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "variant_version_snapshot",
                table: "invoice_lines",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoices_business_source_sale",
                table: "invoices",
                columns: new[] { "business_id", "source_sale_id" },
                unique: true,
                filter: "source_sale_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_recognition_mode",
                table: "invoices",
                sql: "recognition_mode IN ('Service', 'DirectGoods', 'SaleRepresentation', 'Delivery', 'HistoricalUnknown')");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_source_sale_line_id_business_id",
                table: "invoice_lines",
                columns: new[] { "source_sale_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_invoice_lines_business_source_sale_line",
                table: "invoice_lines",
                columns: new[] { "business_id", "source_sale_line_id" },
                unique: true,
                filter: "source_sale_line_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_lines_sale_lines_source_sale_line_id_business_id",
                table: "invoice_lines",
                columns: new[] { "source_sale_line_id", "business_id" },
                principalTable: "sale_lines",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            // Existing invoices predate recognition modes. Only links that name their source are classified; everything
            // else stays HistoricalUnknown, which cannot be issued or voided through the Service route.
            migrationBuilder.Sql("""
                UPDATE invoices SET recognition_mode = 'SaleRepresentation' WHERE source_sale_id IS NOT NULL;
                UPDATE invoices SET recognition_mode = 'Delivery' WHERE source_sale_id IS NULL AND source_order_id IS NOT NULL;
                """);

            // Once an invoice leaves draft, its recognition source, snapshots and business identity are part of the posted
            // record. A separate trigger keeps the existing financial-terms guard unchanged.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_invoice_recognition_guard() RETURNS trigger AS $$
                BEGIN
                    IF OLD.status <> 'Draft' AND (
                           NEW.recognition_mode              IS DISTINCT FROM OLD.recognition_mode
                        OR NEW.source_order_id               IS DISTINCT FROM OLD.source_order_id
                        OR NEW.source_request_key            IS DISTINCT FROM OLD.source_request_key
                        OR NEW.source_payload_hash           IS DISTINCT FROM OLD.source_payload_hash
                        OR NEW.business_name_snapshot        IS DISTINCT FROM OLD.business_name_snapshot
                        OR NEW.business_legal_name_snapshot  IS DISTINCT FROM OLD.business_legal_name_snapshot
                        OR NEW.location_address_snapshot     IS DISTINCT FROM OLD.location_address_snapshot
                        OR NEW.customer_name_snapshot        IS DISTINCT FROM OLD.customer_name_snapshot
                        OR NEW.customer_address_snapshot     IS DISTINCT FROM OLD.customer_address_snapshot
                        OR NEW.customer_phone_snapshot       IS DISTINCT FROM OLD.customer_phone_snapshot
                        OR NEW.customer_email_snapshot       IS DISTINCT FROM OLD.customer_email_snapshot
                        OR NEW.payment_terms_days_snapshot   IS DISTINCT FROM OLD.payment_terms_days_snapshot)
                    THEN
                        RAISE EXCEPTION 'Invoice % is %; its recognition source and snapshots are immutable.', OLD.id, OLD.status
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_invoices_recognition_immutable
                    BEFORE UPDATE ON invoices
                    FOR EACH ROW EXECUTE FUNCTION qts_invoice_recognition_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_invoices_recognition_immutable ON invoices;
                DROP FUNCTION IF EXISTS qts_invoice_recognition_guard();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_lines_sale_lines_source_sale_line_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "ux_invoices_business_source_sale",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_recognition_mode",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_source_sale_line_id_business_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "ux_invoice_lines_business_source_sale_line",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "business_legal_name_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "business_name_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "customer_address_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "customer_email_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "customer_name_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "customer_phone_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "location_address_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "payment_terms_days_snapshot",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "recognition_mode",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "product_version_snapshot",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "source_sale_line_id",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "variant_version_snapshot",
                table: "invoice_lines");
        }
    }
}
