using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBankingStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_journal_lines_id_business_id",
                table: "journal_lines",
                columns: new[] { "id", "business_id" });

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_number_masked = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ledger_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.id);
                    table.UniqueConstraint("AK_bank_accounts_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "FK_bank_accounts_accounts_ledger_account_id_business_id",
                        columns: x => new { x.ledger_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_accounts_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_statement_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    statement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    closing_balance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    imported_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_imports", x => x.id);
                    table.UniqueConstraint("AK_bank_statement_imports_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_bank_statement_imports_line_count", "line_count > 0");
                    table.ForeignKey(
                        name: "FK_bank_statement_imports_bank_accounts_bank_account_id_busine~",
                        columns: x => new { x.bank_account_id, x.business_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_imports_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_imports_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_statement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "text", maxLength: 400, nullable: false),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    matched_journal_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reconciled_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    reconciled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_lines", x => x.id);
                    table.UniqueConstraint("AK_bank_statement_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_bank_statement_lines_nonzero_amount", "amount <> 0");
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_bank_statement_imports_statement_impor~",
                        columns: x => new { x.statement_import_id, x.business_id },
                        principalTable: "bank_statement_imports",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_journal_lines_matched_journal_line_id_~",
                        columns: x => new { x.matched_journal_line_id, x.business_id },
                        principalTable: "journal_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_users_reconciled_by_user_id",
                        column: x => x.reconciled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_business_id_ledger_account_id",
                table: "bank_accounts",
                columns: new[] { "business_id", "ledger_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_business_id_name",
                table: "bank_accounts",
                columns: new[] { "business_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_ledger_account_id_business_id",
                table: "bank_accounts",
                columns: new[] { "ledger_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_imports_bank_account_id_business_id",
                table: "bank_statement_imports",
                columns: new[] { "bank_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_imports_imported_by_user_id",
                table: "bank_statement_imports",
                column: "imported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_imports_account_file_hash",
                table: "bank_statement_imports",
                columns: new[] { "business_id", "bank_account_id", "file_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_imports_business_idempotency_key",
                table: "bank_statement_imports",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_business_id_value_date",
                table: "bank_statement_lines",
                columns: new[] { "business_id", "value_date" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_matched_journal_line_id_business_id",
                table: "bank_statement_lines",
                columns: new[] { "matched_journal_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_reconciled_by_user_id",
                table: "bank_statement_lines",
                column: "reconciled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_statement_import_id_business_id",
                table: "bank_statement_lines",
                columns: new[] { "statement_import_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_statement_import_id_line_no",
                table: "bank_statement_lines",
                columns: new[] { "statement_import_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_lines_business_matched_journal_line",
                table: "bank_statement_lines",
                columns: new[] { "business_id", "matched_journal_line_id" },
                unique: true,
                filter: "matched_journal_line_id IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_bank_account_guard() RETURNS trigger AS $$
                DECLARE v_account_type text;
                        v_account_active boolean;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Bank accounts are retained as reconciliation evidence.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'UPDATE' THEN
                        IF NEW.id IS DISTINCT FROM OLD.id
                           OR NEW.business_id IS DISTINCT FROM OLD.business_id
                           OR NEW.account_number_masked IS DISTINCT FROM OLD.account_number_masked
                           OR NEW.iban IS DISTINCT FROM OLD.iban
                           OR NEW.currency IS DISTINCT FROM OLD.currency
                           OR NEW.ledger_account_id IS DISTINCT FROM OLD.ledger_account_id THEN
                            RAISE EXCEPTION 'Bank account identity and ledger mapping are immutable.'
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    SELECT account_type, is_active INTO v_account_type, v_account_active
                    FROM accounts
                    WHERE id = NEW.ledger_account_id AND business_id = NEW.business_id;
                    IF NOT FOUND OR v_account_type <> 'Asset' OR NOT v_account_active THEN
                        RAISE EXCEPTION 'A bank account must map to an active asset ledger account in its business.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_bank_accounts_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON bank_accounts
                    FOR EACH ROW EXECUTE FUNCTION qts_bank_account_guard();

                CREATE OR REPLACE FUNCTION qts_bank_statement_import_guard() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Bank statement imports are immutable audit evidence.'
                        USING ERRCODE = 'check_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_bank_statement_imports_immutable
                    BEFORE UPDATE OR DELETE ON bank_statement_imports
                    FOR EACH ROW EXECUTE FUNCTION qts_bank_statement_import_guard();

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

                CREATE TRIGGER trg_bank_statement_lines_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON bank_statement_lines
                    FOR EACH ROW EXECUTE FUNCTION qts_bank_statement_lines_guard();

                CREATE OR REPLACE FUNCTION qts_validate_bank_statement_import_lines() RETURNS trigger AS $$
                DECLARE v_count integer;
                        v_min integer;
                        v_max integer;
                BEGIN
                    SELECT count(*)::integer, min(line_no), max(line_no)
                    INTO v_count, v_min, v_max
                    FROM bank_statement_lines
                    WHERE statement_import_id = NEW.id AND business_id = NEW.business_id;
                    IF v_count <> NEW.line_count OR v_min <> 1 OR v_max <> NEW.line_count THEN
                        RAISE EXCEPTION 'Bank statement imports must commit with their complete, consecutively numbered line set.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER trg_bank_statement_import_line_count
                    AFTER INSERT ON bank_statement_imports
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION qts_validate_bank_statement_import_lines();

                CREATE TRIGGER trg_bank_accounts_no_truncate
                    BEFORE TRUNCATE ON bank_accounts
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_bank_statement_imports_no_truncate
                    BEFORE TRUNCATE ON bank_statement_imports
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                CREATE TRIGGER trg_bank_statement_lines_no_truncate
                    BEFORE TRUNCATE ON bank_statement_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION qts_forbid_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM bank_statement_lines)
                       OR EXISTS (SELECT 1 FROM bank_statement_imports)
                       OR EXISTS (SELECT 1 FROM bank_accounts) THEN
                        RAISE EXCEPTION 'Bank reconciliation contains audit evidence and prevents this migration from being rolled back.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "bank_statement_lines");

            migrationBuilder.DropTable(
                name: "bank_statement_imports");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS qts_validate_bank_statement_import_lines();
                DROP FUNCTION IF EXISTS qts_bank_statement_lines_guard();
                DROP FUNCTION IF EXISTS qts_bank_statement_import_guard();
                DROP FUNCTION IF EXISTS qts_bank_account_guard();
                """);

            migrationBuilder.DropUniqueConstraint(
                name: "AK_journal_lines_id_business_id",
                table: "journal_lines");
        }
    }
}
