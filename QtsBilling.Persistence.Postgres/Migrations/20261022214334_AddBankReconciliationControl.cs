using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBankReconciliationControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finalised_at_utc",
                table: "bank_statement_imports",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "finalised_by_user_id",
                table: "bank_statement_imports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "bank_statement_imports",
                type: "text",
                maxLength: 32,
                nullable: false,
                defaultValue: "Open");

            migrationBuilder.CreateTable(
                name: "bank_statement_reconciliation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    prior_journal_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_journal_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "text", maxLength: 400, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_reconciliation_events", x => x.id);
                    table.UniqueConstraint("AK_bank_statement_reconciliation_events_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_bank_statement_reconciliation_events_evidence", "(action = 'Matched' AND new_journal_line_id IS NOT NULL) OR (action = 'Unmatched' AND prior_journal_line_id IS NOT NULL) OR (action IN ('FeePosted', 'InterestPosted') AND journal_entry_id IS NOT NULL AND new_journal_line_id IS NOT NULL) OR action IN ('Finalised', 'Reopened')");
                    table.CheckConstraint("ck_bank_statement_reconciliation_events_level", "(action IN ('Finalised', 'Reopened') AND statement_line_id IS NULL) OR (action NOT IN ('Finalised', 'Reopened') AND statement_line_id IS NOT NULL)");
                    table.CheckConstraint("ck_bank_statement_reconciliation_events_reason", "(action IN ('Unmatched', 'FeePosted', 'InterestPosted', 'Reopened') AND reason IS NOT NULL AND btrim(reason) <> '') OR action IN ('Matched', 'Finalised')");
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_bank_accounts_bank_acc~",
                        columns: x => new { x.bank_account_id, x.business_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_bank_statement_imports~",
                        columns: x => new { x.statement_import_id, x.business_id },
                        principalTable: "bank_statement_imports",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_bank_statement_lines_s~",
                        columns: x => new { x.statement_line_id, x.business_id },
                        principalTable: "bank_statement_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_journal_entries_journa~",
                        columns: x => new { x.journal_entry_id, x.business_id },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_journal_lines_new_jour~",
                        columns: x => new { x.new_journal_line_id, x.business_id },
                        principalTable: "journal_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_journal_lines_prior_jo~",
                        columns: x => new { x.prior_journal_line_id, x.business_id },
                        principalTable: "journal_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_reconciliation_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_imports_business_account_status",
                table: "bank_statement_imports",
                columns: new[] { "business_id", "bank_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_imports_finalised_by_user_id",
                table: "bank_statement_imports",
                column: "finalised_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_bank_statement_imports_finalisation",
                table: "bank_statement_imports",
                sql: "(status = 'Open' AND finalised_at_utc IS NULL AND finalised_by_user_id IS NULL) OR (status = 'Finalised' AND finalised_at_utc IS NOT NULL AND finalised_by_user_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_actor_user_id",
                table: "bank_statement_reconciliation_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_bank_account_id_busine~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "bank_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_reconciliation_events_business_line_time",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "business_id", "statement_line_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_reconciliation_events_business_statement_time",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "business_id", "statement_import_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_journal_entry_id_busin~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "journal_entry_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_new_journal_line_id_bu~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "new_journal_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_prior_journal_line_id_~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "prior_journal_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_statement_import_id_bu~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "statement_import_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_reconciliation_events_statement_line_id_busi~",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "statement_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_reconciliation_events_business_journal_entry",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "business_id", "journal_entry_id" },
                unique: true,
                filter: "journal_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_reconciliation_events_business_key",
                table: "bank_statement_reconciliation_events",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key <> ''");

            migrationBuilder.AddForeignKey(
                name: "FK_bank_statement_imports_users_finalised_by_user_id",
                table: "bank_statement_imports",
                column: "finalised_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // The statement's own facts stay immutable while its review state becomes a live column. The guard
            // shipped with the import feature refused every UPDATE, which would have refused finalisation too, so
            // it is replaced with a rule that names exactly what may change (specification task 31, recipe step 3).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_bank_statement_import_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Bank statement imports are immutable audit evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.bank_account_id IS DISTINCT FROM OLD.bank_account_id
                       OR NEW.file_name IS DISTINCT FROM OLD.file_name
                       OR NEW.statement_date IS DISTINCT FROM OLD.statement_date
                       OR NEW.opening_balance IS DISTINCT FROM OLD.opening_balance
                       OR NEW.closing_balance IS DISTINCT FROM OLD.closing_balance
                       OR NEW.file_hash IS DISTINCT FROM OLD.file_hash
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.payload_hash IS DISTINCT FROM OLD.payload_hash
                       OR NEW.line_count IS DISTINCT FROM OLD.line_count
                       OR NEW.imported_at_utc IS DISTINCT FROM OLD.imported_at_utc
                       OR NEW.imported_by_user_id IS DISTINCT FROM OLD.imported_by_user_id
                       OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc
                       OR NEW.business_id IS DISTINCT FROM OLD.business_id THEN
                        RAISE EXCEPTION 'The facts of an imported bank statement are immutable; only its review state may change.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF NEW.status NOT IN ('Open', 'Finalised')
                       OR (NEW.status = 'Open' AND (NEW.finalised_at_utc IS NOT NULL OR NEW.finalised_by_user_id IS NOT NULL))
                       OR (NEW.status = 'Finalised' AND (NEW.finalised_at_utc IS NULL OR NEW.finalised_by_user_id IS NULL)) THEN
                        RAISE EXCEPTION 'A finalised statement must name who finalised it and when.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            // A reconciliation may be corrected, but never silently: every evidence column stays pinned, only the
            // match state moves, and the deferred rule below insists the change is explained by an event row
            // written in the same transaction (specification D10, case T31.03).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_bank_statement_lines_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.matched_journal_line_id IS NOT NULL
                           OR NEW.reconciled_at_utc IS NOT NULL
                           OR NEW.reconciled_by_user_id IS NOT NULL THEN
                            RAISE EXCEPTION 'A statement line must be imported as unmatched evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF TG_OP = 'UPDATE'
                       AND NEW.id IS NOT DISTINCT FROM OLD.id
                       AND NEW.statement_import_id IS NOT DISTINCT FROM OLD.statement_import_id
                       AND NEW.line_no IS NOT DISTINCT FROM OLD.line_no
                       AND NEW.value_date IS NOT DISTINCT FROM OLD.value_date
                       AND NEW.description IS NOT DISTINCT FROM OLD.description
                       AND NEW.reference IS NOT DISTINCT FROM OLD.reference
                       AND NEW.amount IS NOT DISTINCT FROM OLD.amount
                       AND NEW.business_id IS NOT DISTINCT FROM OLD.business_id
                       AND NEW.created_at_utc IS NOT DISTINCT FROM OLD.created_at_utc
                       AND NEW.matched_journal_line_id IS DISTINCT FROM OLD.matched_journal_line_id THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'A statement line may change only its match state, and only with recorded evidence.'
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_bank_statement_line_evidence() RETURNS trigger AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM bank_statement_reconciliation_events AS event
                        WHERE event.statement_line_id = NEW.id
                          AND event.business_id = NEW.business_id
                          AND event.occurred_at_utc >= OLD.updated_at_utc) THEN
                        RAISE EXCEPTION 'Statement line % changed reconciliation state without an evidence row written in the same transaction.', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_bank_statement_lines_evidence
                    AFTER UPDATE ON bank_statement_lines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_bank_statement_line_evidence();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_bank_statement_reconciliation_events_immutable
                    BEFORE UPDATE OR DELETE ON bank_statement_reconciliation_events
                    FOR EACH ROW EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_bank_statement_reconciliation_events_no_truncate
                    BEFORE TRUNCATE ON bank_statement_reconciliation_events
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Both guards go back to the shapes the import feature shipped: no UPDATE at all on the statement
            // header, and reconciliation of a line allowed once, in the forward direction only.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_bank_statement_lines_evidence ON bank_statement_lines;
                DROP FUNCTION IF EXISTS qts_bank_statement_line_evidence();
                DROP TRIGGER IF EXISTS trg_bank_statement_reconciliation_events_immutable ON bank_statement_reconciliation_events;
                DROP TRIGGER IF EXISTS trg_bank_statement_reconciliation_events_no_truncate ON bank_statement_reconciliation_events;

                CREATE OR REPLACE FUNCTION qts_bank_statement_import_guard() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Bank statement imports are immutable audit evidence.'
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION qts_bank_statement_lines_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.matched_journal_line_id IS NOT NULL
                           OR NEW.reconciled_at_utc IS NOT NULL
                           OR NEW.reconciled_by_user_id IS NOT NULL THEN
                            RAISE EXCEPTION 'A statement line must be imported as unmatched evidence.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    IF TG_OP = 'UPDATE'
                       AND OLD.matched_journal_line_id IS NULL
                       AND NEW.matched_journal_line_id IS NOT NULL
                       AND OLD.reconciled_at_utc IS NULL
                       AND NEW.reconciled_at_utc IS NOT NULL
                       AND OLD.reconciled_by_user_id IS NULL
                       AND NEW.reconciled_by_user_id IS NOT NULL
                       AND NEW.id IS NOT DISTINCT FROM OLD.id
                       AND NEW.statement_import_id IS NOT DISTINCT FROM OLD.statement_import_id
                       AND NEW.line_no IS NOT DISTINCT FROM OLD.line_no
                       AND NEW.value_date IS NOT DISTINCT FROM OLD.value_date
                       AND NEW.description IS NOT DISTINCT FROM OLD.description
                       AND NEW.reference IS NOT DISTINCT FROM OLD.reference
                       AND NEW.amount IS NOT DISTINCT FROM OLD.amount
                       AND NEW.business_id IS NOT DISTINCT FROM OLD.business_id THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'Statement lines are immutable except for one-time reconciliation.'
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_bank_statement_imports_users_finalised_by_user_id",
                table: "bank_statement_imports");

            migrationBuilder.DropTable(
                name: "bank_statement_reconciliation_events");

            migrationBuilder.DropIndex(
                name: "ix_bank_statement_imports_business_account_status",
                table: "bank_statement_imports");

            migrationBuilder.DropIndex(
                name: "IX_bank_statement_imports_finalised_by_user_id",
                table: "bank_statement_imports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_bank_statement_imports_finalisation",
                table: "bank_statement_imports");

            migrationBuilder.DropColumn(
                name: "finalised_at_utc",
                table: "bank_statement_imports");

            migrationBuilder.DropColumn(
                name: "finalised_by_user_id",
                table: "bank_statement_imports");

            migrationBuilder.DropColumn(
                name: "status",
                table: "bank_statement_imports");
        }
    }
}
