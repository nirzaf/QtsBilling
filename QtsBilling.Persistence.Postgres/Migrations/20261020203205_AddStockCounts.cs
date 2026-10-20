using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddStockCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_count_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    snapshot_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_count_sessions", x => x.id);
                    table.UniqueConstraint("AK_stock_count_sessions_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "FK_stock_count_sessions_business_locations_location_id_busines~",
                        columns: x => new { x.location_id, x.business_id },
                        principalTable: "business_locations",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_sessions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_sessions_journal_entries_journal_entry_id_busin~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_sessions_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_count_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_count_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    expected_unit_cost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    variance_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    variance_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_count_lines", x => x.id);
                    table.UniqueConstraint("AK_stock_count_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_stock_count_lines_non_negative", "expected_quantity >= 0 AND expected_unit_cost >= 0 AND (counted_quantity IS NULL OR counted_quantity >= 0)");
                    table.ForeignKey(
                        name: "FK_stock_count_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_lines_stock_count_sessions_stock_count_session_~",
                        columns: x => new { x.stock_count_session_id, x.business_id },
                        principalTable: "stock_count_sessions",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_lines_business_id_variant_id",
                table: "stock_count_lines",
                columns: new[] { "business_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_lines_stock_count_session_id_business_id",
                table: "stock_count_lines",
                columns: new[] { "stock_count_session_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_lines_stock_count_session_id_variant_id",
                table: "stock_count_lines",
                columns: new[] { "stock_count_session_id", "variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_lines_variant_id_business_id",
                table: "stock_count_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_sessions_approved_by_user_id",
                table: "stock_count_sessions",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_sessions_business_id_count_number",
                table: "stock_count_sessions",
                columns: new[] { "business_id", "count_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_sessions_business_id_location_id_status_created~",
                table: "stock_count_sessions",
                columns: new[] { "business_id", "location_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_sessions_journal_entry_id_business_id",
                table: "stock_count_sessions",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_sessions_location_id_business_id",
                table: "stock_count_sessions",
                columns: new[] { "location_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_count_sessions_business_idempotency_key",
                table: "stock_count_sessions",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_stock_count_session_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Stock-count sessions are retained as audit evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Open'
                           OR NEW.snapshot_at_utc IS NULL
                           OR NEW.submitted_at_utc IS NOT NULL
                           OR NEW.approved_at_utc IS NOT NULL
                           OR NEW.approved_by_user_id IS NOT NULL
                           OR NEW.journal_entry_id IS NOT NULL THEN
                            RAISE EXCEPTION 'Stock-count sessions must be created as open snapshots.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.location_id IS DISTINCT FROM OLD.location_id
                       OR NEW.count_number IS DISTINCT FROM OLD.count_number
                       OR NEW.snapshot_at_utc IS DISTINCT FROM OLD.snapshot_at_utc
                       OR NEW.note IS DISTINCT FROM OLD.note
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash IS DISTINCT FROM OLD.payload_hash THEN
                        RAISE EXCEPTION 'Stock-count snapshot terms are immutable.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF OLD.status = 'Open' AND NEW.status = 'Submitted' THEN
                        IF NEW.submitted_at_utc IS NULL
                           OR NEW.approved_at_utc IS NOT NULL
                           OR NEW.approved_by_user_id IS NOT NULL
                           OR NEW.journal_entry_id IS NOT NULL THEN
                            RAISE EXCEPTION 'Submitting a count requires a timestamp and no approval evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD.status IN ('Open', 'Submitted') AND NEW.status = 'Cancelled' THEN
                        IF NEW.submitted_at_utc IS DISTINCT FROM OLD.submitted_at_utc
                           OR NEW.approved_at_utc IS NOT NULL
                           OR NEW.approved_by_user_id IS NOT NULL
                           OR NEW.journal_entry_id IS NOT NULL THEN
                            RAISE EXCEPTION 'A cancelled count cannot carry approval evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF OLD.status = 'Submitted' AND NEW.status = 'Approved' THEN
                        IF NEW.submitted_at_utc IS DISTINCT FROM OLD.submitted_at_utc
                           OR NEW.approved_at_utc IS NULL
                           OR NEW.approved_by_user_id IS NULL THEN
                            RAISE EXCEPTION 'Approval requires an operator and timestamp while preserving submission evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'Illegal stock-count state transition from % to %.', OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_stock_count_sessions_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_count_sessions
                    FOR EACH ROW EXECUTE FUNCTION qts_stock_count_session_guard();

                CREATE OR REPLACE FUNCTION qts_stock_count_lines_guard() RETURNS trigger AS $$
                DECLARE
                    v_status text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Stock-count lines are retained as audit evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    SELECT status INTO v_status FROM stock_count_sessions
                    WHERE id = COALESCE(NEW.stock_count_session_id, OLD.stock_count_session_id)
                      AND business_id = COALESCE(NEW.business_id, OLD.business_id);

                    IF TG_OP = 'INSERT' THEN
                        IF v_status IS DISTINCT FROM 'Open' THEN
                            RAISE EXCEPTION 'Lines may only be added to an open stock-count session.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF NEW.stock_count_session_id IS DISTINCT FROM OLD.stock_count_session_id
                       OR NEW.business_id IS DISTINCT FROM OLD.business_id
                       OR NEW.variant_id IS DISTINCT FROM OLD.variant_id
                       OR NEW.expected_quantity IS DISTINCT FROM OLD.expected_quantity
                       OR NEW.expected_unit_cost IS DISTINCT FROM OLD.expected_unit_cost THEN
                        RAISE EXCEPTION 'Stock-count snapshot quantities and costs are immutable.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF v_status = 'Open' THEN
                        IF NEW.variance_quantity IS DISTINCT FROM OLD.variance_quantity
                           OR NEW.variance_value IS DISTINCT FROM OLD.variance_value THEN
                            RAISE EXCEPTION 'Variances are computed only during approval.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF v_status = 'Submitted'
                       AND NEW.counted_quantity IS NOT DISTINCT FROM OLD.counted_quantity
                       AND OLD.variance_quantity IS NULL
                       AND OLD.variance_value IS NULL
                       AND NEW.variance_quantity IS NOT NULL
                       AND NEW.variance_value IS NOT NULL THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'Stock-count lines are immutable after submission.'
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_stock_count_lines_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON stock_count_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_stock_count_lines_guard();

                CREATE TRIGGER trg_stock_count_sessions_no_truncate
                    BEFORE TRUNCATE ON stock_count_sessions
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_stock_count_lines_no_truncate
                    BEFORE TRUNCATE ON stock_count_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM stock_count_sessions) THEN
                        RAISE EXCEPTION 'Stock counts contain audit evidence and prevent this migration from being rolled back.';
                    END IF;
                END;
                $$;
                """);
            migrationBuilder.DropTable(
                name: "stock_count_lines");

            migrationBuilder.DropTable(
                name: "stock_count_sessions");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS qts_stock_count_lines_guard();
                DROP FUNCTION IF EXISTS qts_stock_count_session_guard();
                """);
        }
    }
}
