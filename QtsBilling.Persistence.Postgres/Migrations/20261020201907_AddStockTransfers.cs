using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddStockTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    dispatched_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispatch_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receive_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_transfers", x => x.id);
                    table.UniqueConstraint("AK_stock_transfers_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_stock_transfers_distinct_locations", "source_location_id <> destination_location_id");
                    table.ForeignKey(
                        name: "FK_stock_transfers_business_locations_destination_location_id_~",
                        columns: x => new { x.destination_location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_business_locations_source_location_id_busin~",
                        columns: x => new { x.source_location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_journal_entries_dispatch_journal_entry_id_b~",
                        columns: x => new { x.dispatch_journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_journal_entries_receive_journal_entry_id_bu~",
                        columns: x => new { x.receive_journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_users_dispatched_by_user_id",
                        column: x => x.dispatched_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfers_users_received_by_user_id",
                        column: x => x.received_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_transfer_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost_at_dispatch = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    value_at_dispatch = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_transfer_lines", x => x.id);
                    table.UniqueConstraint("AK_stock_transfer_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_stock_transfer_lines_quantity_positive", "quantity > 0 AND (unit_cost_at_dispatch IS NULL OR unit_cost_at_dispatch >= 0) AND (value_at_dispatch IS NULL OR value_at_dispatch >= 0)");
                    table.ForeignKey(
                        name: "FK_stock_transfer_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfer_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfer_lines_stock_transfers_stock_transfer_id_busi~",
                        columns: x => new { x.stock_transfer_id, x.business_id },
                        principalTable: "stock_transfers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_lines_business_id_variant_id",
                table: "stock_transfer_lines",
                columns: new[] { "business_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_lines_stock_transfer_id_business_id",
                table: "stock_transfer_lines",
                columns: new[] { "stock_transfer_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_lines_stock_transfer_id_line_no",
                table: "stock_transfer_lines",
                columns: new[] { "stock_transfer_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_lines_variant_id_business_id",
                table: "stock_transfer_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_business_id_status_created_at_utc",
                table: "stock_transfers",
                columns: new[] { "business_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_business_id_transfer_number",
                table: "stock_transfers",
                columns: new[] { "business_id", "transfer_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_destination_location_id_business_id",
                table: "stock_transfers",
                columns: new[] { "destination_location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_dispatch_journal_entry_id_business_id",
                table: "stock_transfers",
                columns: new[] { "dispatch_journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_dispatched_by_user_id",
                table: "stock_transfers",
                column: "dispatched_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_receive_journal_entry_id_business_id",
                table: "stock_transfers",
                columns: new[] { "receive_journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_received_by_user_id",
                table: "stock_transfers",
                column: "received_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfers_source_location_id_business_id",
                table: "stock_transfers",
                columns: new[] { "source_location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_transfers_business_idempotency_key",
                table: "stock_transfers",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.Sql("""
                INSERT INTO accounts (id, business_id, code, name, account_type, parent_id, is_active, version, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), business.id, '1210', 'Inventory In Transit', 'Asset', NULL, TRUE, 1, now(), now()
                FROM businesses AS business
                WHERE NOT EXISTS (
                    SELECT 1 FROM accounts AS account
                    WHERE account.business_id = business.id AND account.code = '1210');

                CREATE OR REPLACE FUNCTION qts_stock_transfer_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Stock transfers are retained as financial evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Draft'
                           OR NEW.dispatched_at_utc IS NOT NULL
                           OR NEW.dispatched_by_user_id IS NOT NULL
                           OR NEW.dispatch_journal_entry_id IS NOT NULL
                           OR NEW.received_at_utc IS NOT NULL
                           OR NEW.received_by_user_id IS NOT NULL
                           OR NEW.receive_journal_entry_id IS NOT NULL THEN
                            RAISE EXCEPTION 'Stock transfers must be created as clean drafts.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.transfer_number IS DISTINCT FROM OLD.transfer_number
                       OR NEW.source_location_id IS DISTINCT FROM OLD.source_location_id
                       OR NEW.destination_location_id IS DISTINCT FROM OLD.destination_location_id
                       OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.note IS DISTINCT FROM OLD.note
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash IS DISTINCT FROM OLD.payload_hash THEN
                        RAISE EXCEPTION 'Stock-transfer terms are immutable.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF OLD.status = 'Draft' AND NEW.status = 'Dispatched' THEN
                        IF NEW.dispatched_at_utc IS NULL
                           OR NEW.dispatched_by_user_id IS NULL
                           OR NEW.dispatch_journal_entry_id IS NULL
                           OR NEW.received_at_utc IS NOT NULL
                           OR NEW.received_by_user_id IS NOT NULL
                           OR NEW.receive_journal_entry_id IS NOT NULL THEN
                            RAISE EXCEPTION 'A dispatch requires its timestamp, operator, and journal entry.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD.status = 'Dispatched' AND NEW.status = 'Received' THEN
                        IF NEW.dispatched_at_utc IS DISTINCT FROM OLD.dispatched_at_utc
                           OR NEW.dispatched_by_user_id IS DISTINCT FROM OLD.dispatched_by_user_id
                           OR NEW.dispatch_journal_entry_id IS DISTINCT FROM OLD.dispatch_journal_entry_id
                           OR NEW.received_at_utc IS NULL
                           OR NEW.received_by_user_id IS NULL
                           OR NEW.receive_journal_entry_id IS NULL THEN
                            RAISE EXCEPTION 'A receipt must preserve dispatch evidence and add its own posting evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'Illegal stock-transfer state transition from % to %.', OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_stock_transfers_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_transfers
                    FOR EACH ROW EXECUTE FUNCTION qts_stock_transfer_guard();

                CREATE OR REPLACE FUNCTION qts_stock_transfer_lines_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Stock-transfer lines are retained as financial evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    SELECT status INTO v_status FROM stock_transfers
                    WHERE id = COALESCE(NEW.stock_transfer_id, OLD.stock_transfer_id)
                      AND business_id = COALESCE(NEW.business_id, OLD.business_id);

                    IF v_status IS DISTINCT FROM 'Draft' THEN
                        RAISE EXCEPTION 'Stock-transfer lines are immutable after dispatch.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        RETURN NEW;
                    END IF;

                    IF NEW.stock_transfer_id IS DISTINCT FROM OLD.stock_transfer_id
                       OR NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.line_no IS DISTINCT FROM OLD.line_no
                       OR NEW.variant_id IS DISTINCT FROM OLD.variant_id
                       OR NEW.quantity IS DISTINCT FROM OLD.quantity
                       OR OLD.unit_cost_at_dispatch IS NOT NULL
                       OR OLD.value_at_dispatch IS NOT NULL
                       OR NEW.unit_cost_at_dispatch IS NULL
                       OR NEW.value_at_dispatch IS NULL THEN
                        RAISE EXCEPTION 'Only first-time dispatch valuation may be recorded on a draft transfer line.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_stock_transfer_lines_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_transfer_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_stock_transfer_lines_guard();

                CREATE TRIGGER trg_stock_transfers_no_truncate
                    BEFORE TRUNCATE ON stock_transfers
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_stock_transfer_lines_no_truncate
                    BEFORE TRUNCATE ON stock_transfer_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM stock_transfers) THEN
                        RAISE EXCEPTION 'Stock transfers contain financial evidence and prevent this migration from being rolled back.';
                    END IF;
                END;
                $$;
                """);
            migrationBuilder.DropTable(
                name: "stock_transfer_lines");

            migrationBuilder.DropTable(
                name: "stock_transfers");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS qts_stock_transfer_lines_guard();
                DROP FUNCTION IF EXISTS qts_stock_transfer_guard();
                DELETE FROM accounts AS account
                WHERE account.code = '1210'
                  AND account.name = 'Inventory In Transit'
                  AND NOT EXISTS (SELECT 1 FROM journal_lines WHERE account_id = account.id)
                  AND NOT EXISTS (
                      SELECT 1 FROM business_accounting_settings AS settings
                      WHERE settings.business_id = account.business_id
                        AND settings.inventory_asset_account_id = account.id);
                """);
        }
    }
}
