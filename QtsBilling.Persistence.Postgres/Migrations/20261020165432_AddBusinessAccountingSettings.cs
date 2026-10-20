using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessAccountingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_accounting_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_scale = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    cash_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_clearing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accounts_receivable_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_asset_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_tax_receivable_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accounts_payable_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_tax_payable_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grni_clearing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_deposits_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retained_earnings_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_revenue_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_returns_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cogs_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_price_variance_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_shrinkage_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_over_short_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_accounting_settings", x => x.id);
                    table.UniqueConstraint("AK_business_accounting_settings_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_business_accounting_settings_scale", "settlement_scale IN (0, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_accounts_payable_acco~",
                        columns: x => new { x.accounts_payable_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_accounts_receivable_a~",
                        columns: x => new { x.accounts_receivable_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_bank_clearing_account~",
                        columns: x => new { x.bank_clearing_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_cash_account_id_busin~",
                        columns: x => new { x.cash_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_cash_over_short_accou~",
                        columns: x => new { x.cash_over_short_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_cogs_account_id_busin~",
                        columns: x => new { x.cogs_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_customer_deposits_acc~",
                        columns: x => new { x.customer_deposits_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_grni_clearing_account~",
                        columns: x => new { x.grni_clearing_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_input_tax_receivable_~",
                        columns: x => new { x.input_tax_receivable_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_inventory_asset_accou~",
                        columns: x => new { x.inventory_asset_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_inventory_shrinkage_a~",
                        columns: x => new { x.inventory_shrinkage_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_output_tax_payable_ac~",
                        columns: x => new { x.output_tax_payable_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_purchase_price_varian~",
                        columns: x => new { x.purchase_price_variance_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_retained_earnings_acc~",
                        columns: x => new { x.retained_earnings_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_sales_returns_account~",
                        columns: x => new { x.sales_returns_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_accounts_sales_revenue_account~",
                        columns: x => new { x.sales_revenue_account_id, x.business_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_accounting_settings_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_accounts_payable_account_id_bu~",
                table: "business_accounting_settings",
                columns: new[] { "accounts_payable_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_accounts_receivable_account_id~",
                table: "business_accounting_settings",
                columns: new[] { "accounts_receivable_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_bank_clearing_account_id_busin~",
                table: "business_accounting_settings",
                columns: new[] { "bank_clearing_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_business_id",
                table: "business_accounting_settings",
                column: "business_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_cash_account_id_business_id",
                table: "business_accounting_settings",
                columns: new[] { "cash_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_cash_over_short_account_id_bus~",
                table: "business_accounting_settings",
                columns: new[] { "cash_over_short_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_cogs_account_id_business_id",
                table: "business_accounting_settings",
                columns: new[] { "cogs_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_customer_deposits_account_id_b~",
                table: "business_accounting_settings",
                columns: new[] { "customer_deposits_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_grni_clearing_account_id_busin~",
                table: "business_accounting_settings",
                columns: new[] { "grni_clearing_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_input_tax_receivable_account_i~",
                table: "business_accounting_settings",
                columns: new[] { "input_tax_receivable_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_inventory_asset_account_id_bus~",
                table: "business_accounting_settings",
                columns: new[] { "inventory_asset_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_inventory_shrinkage_account_id~",
                table: "business_accounting_settings",
                columns: new[] { "inventory_shrinkage_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_output_tax_payable_account_id_~",
                table: "business_accounting_settings",
                columns: new[] { "output_tax_payable_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_purchase_price_variance_accoun~",
                table: "business_accounting_settings",
                columns: new[] { "purchase_price_variance_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_retained_earnings_account_id_b~",
                table: "business_accounting_settings",
                columns: new[] { "retained_earnings_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_sales_returns_account_id_busin~",
                table: "business_accounting_settings",
                columns: new[] { "sales_returns_account_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_accounting_settings_sales_revenue_account_id_busin~",
                table: "business_accounting_settings",
                columns: new[] { "sales_revenue_account_id", "business_id" });

            migrationBuilder.Sql(
                """
                INSERT INTO accounts (id, business_id, code, name, account_type, is_active, created_at_utc, updated_at_utc, version)
                SELECT gen_random_uuid(), b.id, defaults.code, defaults.name, defaults.account_type, TRUE, now(), now(), 1
                FROM businesses b
                CROSS JOIN (VALUES
                    ('1000', 'Cash on Hand', 'Asset'),
                    ('1010', 'Bank', 'Asset'),
                    ('1100', 'Accounts Receivable', 'Asset'),
                    ('1200', 'Inventory', 'Asset'),
                    ('1300', 'Input Tax Receivable', 'Asset'),
                    ('2000', 'Accounts Payable', 'Liability'),
                    ('2100', 'Tax Payable', 'Liability'),
                    ('2101', 'Goods Received Not Invoiced', 'Liability'),
                    ('2105', 'Customer Deposits', 'Liability'),
                    ('3000', 'Owner Equity', 'Equity'),
                    ('4000', 'Sales Revenue', 'Revenue'),
                    ('4010', 'Sales Returns', 'Revenue'),
                    ('5000', 'Cost of Goods Sold', 'Expense'),
                    ('5150', 'Purchase Price Variance', 'Expense'),
                    ('5200', 'Inventory Shrinkage', 'Expense'),
                    ('5300', 'Cash Over Short', 'Expense')
                ) AS defaults(code, name, account_type)
                ON CONFLICT (business_id, code) DO NOTHING;

                INSERT INTO business_accounting_settings (
                    id, business_id, settlement_scale,
                    cash_account_id, bank_clearing_account_id, accounts_receivable_account_id,
                    inventory_asset_account_id, input_tax_receivable_account_id, accounts_payable_account_id,
                    output_tax_payable_account_id, grni_clearing_account_id, customer_deposits_account_id,
                    retained_earnings_account_id, sales_revenue_account_id, sales_returns_account_id,
                    cogs_account_id, purchase_price_variance_account_id, inventory_shrinkage_account_id,
                    cash_over_short_account_id, created_at_utc, updated_at_utc, version)
                SELECT gen_random_uuid(), b.id, 2,
                    cash.id, bank.id, ar.id, inventory.id, input_tax.id, ap.id, output_tax.id, grni.id,
                    deposits.id, equity.id, revenue.id, returns.id, cogs.id, variance.id, shrinkage.id,
                    over_short.id, now(), now(), 1
                FROM businesses b
                JOIN accounts cash ON cash.business_id = b.id AND cash.code = '1000'
                JOIN accounts bank ON bank.business_id = b.id AND bank.code = '1010'
                JOIN accounts ar ON ar.business_id = b.id AND ar.code = '1100'
                JOIN accounts inventory ON inventory.business_id = b.id AND inventory.code = '1200'
                JOIN accounts input_tax ON input_tax.business_id = b.id AND input_tax.code = '1300'
                JOIN accounts ap ON ap.business_id = b.id AND ap.code = '2000'
                JOIN accounts output_tax ON output_tax.business_id = b.id AND output_tax.code = '2100'
                JOIN accounts grni ON grni.business_id = b.id AND grni.code = '2101'
                JOIN accounts deposits ON deposits.business_id = b.id AND deposits.code = '2105'
                JOIN accounts equity ON equity.business_id = b.id AND equity.code = '3000'
                JOIN accounts revenue ON revenue.business_id = b.id AND revenue.code = '4000'
                JOIN accounts returns ON returns.business_id = b.id AND returns.code = '4010'
                JOIN accounts cogs ON cogs.business_id = b.id AND cogs.code = '5000'
                JOIN accounts variance ON variance.business_id = b.id AND variance.code = '5150'
                JOIN accounts shrinkage ON shrinkage.business_id = b.id AND shrinkage.code = '5200'
                JOIN accounts over_short ON over_short.business_id = b.id AND over_short.code = '5300'
                ON CONFLICT (business_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_accounting_settings");
        }
    }
}
