using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeysWave6Remaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_accounts_accounts_parent_id",
                table: "accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_audit_events_device_registrations_device_id",
                table: "audit_events");

            migrationBuilder.DropForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_expense_categories_accounts_account_id",
                table: "expense_categories");

            migrationBuilder.DropForeignKey(
                name: "FK_expenses_expense_categories_expense_category_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_invoice_sequences_invoice_sequence_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_entries_accounting_periods_accounting_period_id",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_processed_operations_device_registrations_device_id",
                table: "processed_operations");

            migrationBuilder.DropIndex(
                name: "IX_processed_operations_device_id",
                table: "processed_operations");

            migrationBuilder.DropIndex(
                name: "IX_payments_cash_register_session_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_journal_entries_accounting_period_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "IX_invoices_invoice_sequence_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_expense_category_id",
                table: "expenses");

            migrationBuilder.DropIndex(
                name: "IX_expense_categories_account_id",
                table: "expense_categories");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_cash_register_session_id",
                table: "cash_register_movements");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_device_id",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_accounts_parent_id",
                table: "accounts");

            migrationBuilder.CreateIndex(
                name: "IX_processed_operations_device_id_business_id",
                table: "processed_operations",
                columns: new[] { "device_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_cash_register_session_id_business_id",
                table: "payments",
                columns: new[] { "cash_register_session_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_accounting_period_id_business_id",
                table: "journal_entries",
                columns: new[] { "accounting_period_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_invoice_sequence_id_business_id",
                table: "invoices",
                columns: new[] { "invoice_sequence_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_expense_category_id_business_id",
                table: "expenses",
                columns: new[] { "expense_category_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_categories_account_id_business_id",
                table: "expense_categories",
                columns: new[] { "account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_cash_register_session_id_business_id",
                table: "cash_register_movements",
                columns: new[] { "cash_register_session_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_device_id_business_id",
                table: "audit_events",
                columns: new[] { "device_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_parent_id_business_id",
                table: "accounts",
                columns: new[] { "parent_id", "business_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_accounts_accounts_parent_id_business_id",
                table: "accounts",
                columns: new[] { "parent_id", "business_id" },
                principalTable: "accounts",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_audit_events_device_registrations_device_id_business_id",
                table: "audit_events",
                columns: new[] { "device_id", "business_id" },
                principalTable: "device_registrations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements",
                columns: new[] { "cash_register_session_id", "business_id" },
                principalTable: "cash_register_sessions",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expense_categories_accounts_account_id_business_id",
                table: "expense_categories",
                columns: new[] { "account_id", "business_id" },
                principalTable: "accounts",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_expense_categories_expense_category_id_business_id",
                table: "expenses",
                columns: new[] { "expense_category_id", "business_id" },
                principalTable: "expense_categories",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_invoice_sequences_invoice_sequence_id_business_id",
                table: "invoices",
                columns: new[] { "invoice_sequence_id", "business_id" },
                principalTable: "invoice_sequences",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_entries_accounting_periods_accounting_period_id_bus~",
                table: "journal_entries",
                columns: new[] { "accounting_period_id", "business_id" },
                principalTable: "accounting_periods",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id_bu~",
                table: "payments",
                columns: new[] { "cash_register_session_id", "business_id" },
                principalTable: "cash_register_sessions",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_processed_operations_device_registrations_device_id_busines~",
                table: "processed_operations",
                columns: new[] { "device_id", "business_id" },
                principalTable: "device_registrations",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_accounts_accounts_parent_id_business_id",
                table: "accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_audit_events_device_registrations_device_id_business_id",
                table: "audit_events");

            migrationBuilder.DropForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_expense_categories_accounts_account_id_business_id",
                table: "expense_categories");

            migrationBuilder.DropForeignKey(
                name: "FK_expenses_expense_categories_expense_category_id_business_id",
                table: "expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_invoice_sequences_invoice_sequence_id_business_id",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_journal_entries_accounting_periods_accounting_period_id_bus~",
                table: "journal_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id_bu~",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_processed_operations_device_registrations_device_id_busines~",
                table: "processed_operations");

            migrationBuilder.DropIndex(
                name: "IX_processed_operations_device_id_business_id",
                table: "processed_operations");

            migrationBuilder.DropIndex(
                name: "IX_payments_cash_register_session_id_business_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_journal_entries_accounting_period_id_business_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "IX_invoices_invoice_sequence_id_business_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expenses_expense_category_id_business_id",
                table: "expenses");

            migrationBuilder.DropIndex(
                name: "IX_expense_categories_account_id_business_id",
                table: "expense_categories");

            migrationBuilder.DropIndex(
                name: "IX_cash_register_movements_cash_register_session_id_business_id",
                table: "cash_register_movements");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_device_id_business_id",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_accounts_parent_id_business_id",
                table: "accounts");

            migrationBuilder.CreateIndex(
                name: "IX_processed_operations_device_id",
                table: "processed_operations",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_cash_register_session_id",
                table: "payments",
                column: "cash_register_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_accounting_period_id",
                table: "journal_entries",
                column: "accounting_period_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_invoice_sequence_id",
                table: "invoices",
                column: "invoice_sequence_id");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_expense_category_id",
                table: "expenses",
                column: "expense_category_id");

            migrationBuilder.CreateIndex(
                name: "IX_expense_categories_account_id",
                table: "expense_categories",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_register_movements_cash_register_session_id",
                table: "cash_register_movements",
                column: "cash_register_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_device_id",
                table: "audit_events",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_parent_id",
                table: "accounts",
                column: "parent_id");

            migrationBuilder.AddForeignKey(
                name: "FK_accounts_accounts_parent_id",
                table: "accounts",
                column: "parent_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_audit_events_device_registrations_device_id",
                table: "audit_events",
                column: "device_id",
                principalTable: "device_registrations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_cash_register_movements_cash_register_sessions_cash_registe~",
                table: "cash_register_movements",
                column: "cash_register_session_id",
                principalTable: "cash_register_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expense_categories_accounts_account_id",
                table: "expense_categories",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expenses_expense_categories_expense_category_id",
                table: "expenses",
                column: "expense_category_id",
                principalTable: "expense_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_invoice_sequences_invoice_sequence_id",
                table: "invoices",
                column: "invoice_sequence_id",
                principalTable: "invoice_sequences",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_journal_entries_accounting_periods_accounting_period_id",
                table: "journal_entries",
                column: "accounting_period_id",
                principalTable: "accounting_periods",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_cash_register_sessions_cash_register_session_id",
                table: "payments",
                column: "cash_register_session_id",
                principalTable: "cash_register_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_processed_operations_device_registrations_device_id",
                table: "processed_operations",
                column: "device_id",
                principalTable: "device_registrations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
