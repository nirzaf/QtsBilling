using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// Model configuration — sales, accounting, purchasing and platform (audit/idempotency/sync) tables.
/// </summary>
public partial class BillingDbContext
{
    private void ConfigureSales(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sale>(b =>
        {
            b.ToTable("sales");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.SaleNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.ReversalOfSaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SaleNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.PostedAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.CustomerId });
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_sales_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint(
                "ck_sales_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
        });

        modelBuilder.Entity<SaleLine>(b =>
        {
            b.ToTable("sale_lines");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.Property(e => e.TaxMode).HasMaxLength(16).IsRequired();
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.SaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.SaleId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_sale_lines_amounts",
                "quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND tax_rate >= 0 AND tax_rate <= 1"));
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reference).HasMaxLength(128);
            b.Property(e => e.PspReference).HasMaxLength(128);
            b.Property(e => e.CardBrand).HasMaxLength(32);
            b.Property(e => e.CardLast4).HasMaxLength(4);
            b.Property(e => e.BounceReason).HasMaxLength(500);
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.SaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceId, e.BusinessId })
                .HasPrincipalKey(i => new { i.Id, i.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => new { e.BankAccountId, e.BusinessId })
                .HasPrincipalKey(a => new { a.Id, a.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SaleId });
            b.HasIndex(e => new { e.BusinessId, e.PspReference })
                .IsUnique()
                .HasFilter("\"psp_reference\" IS NOT NULL");
            b.HasOne<CashRegisterSession>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterSessionId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterSessionId });
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_payments_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_payments_amount_positive", "amount > 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_payments_unallocated_amount", "unallocated_amount >= 0 AND unallocated_amount <= amount"));
        });

        modelBuilder.Entity<PaymentAllocation>(b =>
        {
            b.ToTable("payment_allocations");
            HasBusinessFk(b);
            b.Property(e => e.AllocatedAmount).HasPrecision(18, 4);
            b.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(e => new { e.PaymentId, e.BusinessId })
                .HasPrincipalKey(payment => new { payment.Id, payment.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceId, e.BusinessId })
                .HasPrincipalKey(invoice => new { invoice.Id, invoice.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenItem>()
                .WithMany()
                .HasForeignKey(e => new { e.OpenItemId, e.BusinessId })
                .HasPrincipalKey(openItem => new { openItem.Id, openItem.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.PaymentId });
            b.HasIndex(e => new { e.BusinessId, e.InvoiceId });
            b.HasIndex(e => new { e.BusinessId, e.OpenItemId });
            b.ToTable(t => t.HasCheckConstraint("ck_payment_allocations_amount_positive", "allocated_amount > 0"));

            // Exactly one target (specification task 25, recipe step 2): an application must say which document it
            // settles, and it cannot settle two at once. Rows written before this option existed keep their invoice
            // and satisfy the same rule, so no posted allocation is rewritten or reinterpreted.
            b.ToTable(t => t.HasCheckConstraint(
                "ck_payment_allocations_exactly_one_target",
                "((invoice_id IS NOT NULL AND open_item_id IS NULL) OR (invoice_id IS NULL AND open_item_id IS NOT NULL))"));
        });

        modelBuilder.Entity<DepositRefund>(b =>
        {
            b.ToTable("deposit_refunds");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Amount).HasPrecision(18, 4);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reason).HasMaxLength(512).IsRequired();
            b.Property(e => e.Method).HasMaxLength(32).IsRequired();
            b.Property(e => e.BankReference).HasMaxLength(128);
            b.Property(e => e.IdempotencyKey).HasMaxLength(64).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(e => new { e.PaymentId, e.BusinessId })
                .HasPrincipalKey(payment => new { payment.Id, payment.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(entry => new { entry.Id, entry.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.PaymentId });
            b.HasIndex(e => new { e.BusinessId, e.JournalEntryId });
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("ux_deposit_refunds_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_deposit_refunds_amount_positive", "amount > 0"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_deposit_refunds_settlement_named",
                "((method = 'cash' AND cash_register_session_id IS NOT NULL) OR (method <> 'cash' AND bank_reference IS NOT NULL AND length(bank_reference) > 0))"));
        });

        modelBuilder.Entity<CashRegister>(b =>
        {
            b.ToTable("cash_registers");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.LocationId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<CashRegisterMovement>(b =>
        {
            b.ToTable("cash_register_movements");
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reason).HasColumnType("text");
            b.HasOne<CashRegister>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.SaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashRegisterSession>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterSessionId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterId, e.OccurredAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterSessionId });
            b.ToTable(t => t.HasCheckConstraint("ck_cash_register_movements_amount_non_negative", "amount >= 0"));
        });

        modelBuilder.Entity<CashRegisterSession>(b =>
        {
            b.ToTable("cash_register_sessions");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.ClosingNote).HasColumnType("text");
            b.HasOne<CashRegister>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterId, e.OpenedAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterId })
                .IsUnique()
                .HasFilter("status = 'Open'")
                .HasDatabaseName("ux_cash_register_sessions_open");
            b.ToTable(t => t.HasCheckConstraint(
                "ck_cash_register_sessions_amounts",
                "opening_float >= 0 AND (counted_cash IS NULL OR counted_cash >= 0)"));
        });

        modelBuilder.Entity<CashCustodyResolution>(b =>
        {
            b.ToTable("cash_custody_resolutions");
            HasBusinessFk(b);
            b.Property(e => e.CarryForwardNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.SourceReference).HasMaxLength(160).IsRequired();
            b.Property(e => e.Reason).HasMaxLength(400).IsRequired();
            b.HasOne<CashRegister>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterId, e.BusinessId })
                .HasPrincipalKey(r => new { r.Id, r.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CashRegisterSession>()
                .WithMany()
                .HasForeignKey(e => new { e.CashRegisterSessionId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DeviceRegistration>()
                .WithMany()
                .HasForeignKey(e => new { e.DeviceId, e.BusinessId })
                .HasPrincipalKey(device => new { device.Id, device.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ReceivingCustodianUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.AuthorisedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterSessionId });
            b.HasIndex(e => new { e.BusinessId, e.CarryForwardNumber });

            // One carry-forward row per unresolved thing per session: a repeated act cannot quietly double-name the
            // same payment or operation, and a close reads this to decide whether anything is still uncovered.
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterSessionId, e.SourceKind, e.SourceId })
                .IsUnique()
                .HasDatabaseName("ux_cash_custody_resolutions_session_source");
            b.ToTable(t => t.HasCheckConstraint(
                "ck_cash_custody_resolutions_amount",
                "amount >= 0"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_cash_custody_resolutions_distinct_people",
                "receiving_custodian_user_id <> authorised_by_user_id"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_cash_custody_resolutions_device_kind",
                "source_kind <> 'DeviceOperation' OR device_id IS NOT NULL"));
        });

        modelBuilder.Entity<Refund>(b =>
        {
            b.ToTable("refunds");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.RefundNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reason).HasColumnType("text").IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.SaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.RefundNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.SaleId })
                .HasDatabaseName("ix_refunds_business_sale");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_refunds_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_refunds_amount_positive", "amount > 0"));
        });

        modelBuilder.Entity<RefundLine>(b =>
        {
            b.ToTable("refund_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.Property(e => e.Disposition).HasConversion<string>().HasMaxLength(32).HasDefaultValue(RefundDisposition.Restock).IsRequired();
            b.HasOne<Refund>()
                .WithMany()
                .HasForeignKey(e => new { e.RefundId, e.BusinessId })
                .HasPrincipalKey(r => new { r.Id, r.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<SaleLine>()
                .WithMany()
                .HasForeignKey(e => new { e.SaleLineId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.RefundId, e.LineNo }).IsUnique();
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_refund_lines_amounts", "quantity > 0 AND unit_price >= 0");
                t.HasCheckConstraint("ck_refund_lines_disposition", "disposition IN ('Restock', 'DamagedWriteOff')");
            });
        });
    }

    private void ConfigureAccounting(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("accounts");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Code).HasMaxLength(32).IsRequired();
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => new { e.ParentId, e.BusinessId })
                .HasPrincipalKey(a => new { a.Id, a.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.Code }).IsUnique();
        });

        modelBuilder.Entity<AccountingPeriod>(b =>
        {
            b.ToTable("accounting_periods");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("ck_accounting_periods_range", "start_date <= end_date"));
        });

        modelBuilder.Entity<BusinessAccountingSettings>(b =>
        {
            b.ToTable("business_accounting_settings", table =>
            {
                table.HasCheckConstraint("ck_business_accounting_settings_scale", "settlement_scale IN (0, 2, 3, 4)");
                table.HasCheckConstraint("ck_business_accounting_settings_prefix", "char_length(invoice_prefix) BETWEEN 1 AND 12");
                table.HasCheckConstraint("ck_business_accounting_settings_padding", "invoice_padding BETWEEN 3 AND 12");
            });
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.SettlementScale).HasDefaultValue(2);
            b.Property(e => e.InvoicePrefix).HasMaxLength(12).HasDefaultValue("INV-").IsRequired();
            b.Property(e => e.InvoicePadding).HasDefaultValue(5);
            b.HasIndex(e => e.BusinessId).IsUnique();
            ConfigureSettingsAccount(b, e => new { e.CashAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.BankClearingAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.AccountsReceivableAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.InventoryAssetAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.DeliveredNotInvoicedAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.InputTaxReceivableAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.AccountsPayableAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.OutputTaxPayableAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.GrniClearingAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.CustomerDepositsAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.RetainedEarningsAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.SalesRevenueAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.SalesReturnsAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.CogsAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.PurchasePriceVarianceAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.InventoryShrinkageAccountId, e.BusinessId });
            ConfigureSettingsAccount(b, e => new { e.CashOverShortAccountId, e.BusinessId });
            b.HasOne<Account>().WithMany()
                .HasForeignKey(e => new { e.RoundingAccountId, e.BusinessId })
                .HasPrincipalKey(account => new { account.Id, account.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankAccount>(b =>
        {
            b.ToTable("bank_accounts");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.AccountNumberMasked).HasMaxLength(32).IsRequired();
            b.Property(e => e.Iban).HasMaxLength(34);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => new { e.LedgerAccountId, e.BusinessId })
                .HasPrincipalKey(account => new { account.Id, account.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.LedgerAccountId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<BankStatementImport>(b =>
        {
            b.ToTable("bank_statement_imports");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.FileName).HasMaxLength(200).IsRequired();
            b.Property(e => e.OpeningBalance).HasPrecision(18, 4);
            b.Property(e => e.ClosingBalance).HasPrecision(18, 4);
            b.Property(e => e.FileHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(64).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.HasOne<BankAccount>()
                .WithMany()
                .HasForeignKey(e => new { e.BankAccountId, e.BusinessId })
                .HasPrincipalKey(account => new { account.Id, account.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_bank_statement_imports_business_idempotency_key");
            b.HasIndex(e => new { e.BusinessId, e.BankAccountId, e.FileHash }).IsUnique()
                .HasDatabaseName("ux_bank_statement_imports_account_file_hash");
            b.ToTable(t => t.HasCheckConstraint("ck_bank_statement_imports_line_count", "line_count > 0"));
        });

        modelBuilder.Entity<BankStatementLine>(b =>
        {
            b.ToTable("bank_statement_lines");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Description).HasColumnType("text").IsRequired();
            b.Property(e => e.Reference).HasMaxLength(128);
            b.Property(e => e.Amount).HasPrecision(18, 4);
            b.HasOne<BankStatementImport>()
                .WithMany()
                .HasForeignKey(e => new { e.StatementImportId, e.BusinessId })
                .HasPrincipalKey(import => new { import.Id, import.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalLine>()
                .WithMany()
                .HasForeignKey(e => new { e.MatchedJournalLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ReconciledByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.StatementImportId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.MatchedJournalLineId }).IsUnique()
                .HasFilter("matched_journal_line_id IS NOT NULL")
                .HasDatabaseName("ux_bank_statement_lines_business_matched_journal_line");
            b.HasIndex(e => new { e.BusinessId, e.ValueDate });
            b.ToTable(t => t.HasCheckConstraint("ck_bank_statement_lines_nonzero_amount", "amount <> 0"));
        });

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("journal_entries");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.EntryNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Memo).HasColumnType("text");
            b.HasOne<AccountingPeriod>()
                .WithMany()
                .HasForeignKey(e => new { e.AccountingPeriodId, e.BusinessId })
                .HasPrincipalKey(p => new { p.Id, p.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.ReversalOfEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.EntryNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.EntryDate });
            b.HasIndex(e => new { e.BusinessId, e.SourceType, e.SourceId })
                .IsUnique()
                .HasFilter("source_id IS NOT NULL")
                .HasDatabaseName("ux_journal_entries_business_source");
            b.ToTable(t => t.HasCheckConstraint(
                "ck_journal_entries_totals_non_negative",
                "total_debit >= 0 AND total_credit >= 0"));
        });

        modelBuilder.Entity<JournalLine>(b =>
        {
            b.ToTable("journal_lines");
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Description).HasColumnType("text");
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => new { e.AccountId, e.BusinessId })
                .HasPrincipalKey(a => new { a.Id, a.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.JournalEntryId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.AccountId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_journal_lines_single_sided",
                "(debit_amount = 0 AND credit_amount > 0) OR (credit_amount = 0 AND debit_amount > 0)"));
        });

        modelBuilder.Entity<InvoiceSequence>(b =>
        {
            b.ToTable("invoice_sequences");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(64).IsRequired();
            b.Property(e => e.Prefix).HasMaxLength(16).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.Name, e.FiscalYear }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoice_sequences_counters",
                "next_number >= 1 AND padding >= 1 AND padding <= 12"));
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.InvoiceNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.RecognitionMode).HasMaxLength(32).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.BusinessNameSnapshot).HasMaxLength(200);
            b.Property(e => e.BusinessLegalNameSnapshot).HasMaxLength(300);
            b.Property(e => e.LocationAddressSnapshot).HasMaxLength(1000);
            b.Property(e => e.CustomerNameSnapshot).HasMaxLength(200);
            b.Property(e => e.CustomerAddressSnapshot).HasMaxLength(1000);
            b.Property(e => e.CustomerPhoneSnapshot).HasMaxLength(64);
            b.Property(e => e.CustomerEmailSnapshot).HasMaxLength(320);
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoices_balance_math",
                "balance_due = GREATEST(0, total_amount - amount_paid - credited_amount) AND amount_paid >= 0 AND credited_amount >= 0"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoices_recognition_mode",
                "recognition_mode IN ('Service', 'DirectGoods', 'SaleRepresentation', 'Delivery', 'HistoricalUnknown')"));
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(e => new { e.SourceSaleId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InvoiceSequence>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceSequenceId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.InvoiceNumber })
                .IsUnique()
                .HasFilter("invoice_number <> ''")
                .HasDatabaseName("ux_invoices_business_number");
            b.HasIndex(e => new { e.BusinessId, e.IssueDate });
            b.HasIndex(e => new { e.BusinessId, e.Status });
            b.HasIndex(e => new { e.BusinessId, e.CustomerId, e.IssuedAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.SourceSaleId }).IsUnique()
                .HasFilter("source_sale_id IS NOT NULL")
                .HasDatabaseName("ux_invoices_business_source_sale");
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoices_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("invoice_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.Property(e => e.UnitCostSnapshot).HasPrecision(18, 4);
            b.Property(e => e.RevenueBaseAmount).HasPrecision(18, 4);
            b.Property(e => e.TaxMode).HasMaxLength(16).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.SourceSaleLineId }).IsUnique()
                .HasFilter("source_sale_line_id IS NOT NULL")
                .HasDatabaseName("ux_invoice_lines_business_source_sale_line");
            b.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceId, e.BusinessId })
                .HasPrincipalKey(i => new { i.Id, i.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SaleLine>()
                .WithMany()
                .HasForeignKey(e => new { e.SourceSaleLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.InvoiceId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoice_lines_amounts",
                "quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND tax_rate >= 0 AND tax_rate <= 1"));
        });

        modelBuilder.Entity<CreditNote>(b =>
        {
            b.ToTable("credit_notes");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.CreditNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reason).HasColumnType("text").IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceId, e.BusinessId })
                .HasPrincipalKey(i => new { i.Id, i.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.IssuedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CreditNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.InvoiceId })
                .HasDatabaseName("ix_credit_notes_business_invoice");
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_credit_notes_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_credit_notes_amount_positive", "amount > 0"));
        });

        modelBuilder.Entity<CreditNoteLine>(b =>
        {
            b.ToTable("credit_note_lines");
            HasBusinessFk(b);
            b.HasOne<CreditNote>()
                .WithMany()
                .HasForeignKey(e => new { e.CreditNoteId, e.BusinessId })
                .HasPrincipalKey(note => new { note.Id, note.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<InvoiceLine>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(variant => new { variant.Id, variant.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.QuantityCredited).HasPrecision(18, 6);
            b.Property(e => e.UnitPrice).HasPrecision(18, 4);
            b.Property(e => e.TaxAmount).HasPrecision(18, 4);
            b.Property(e => e.LineTotal).HasPrecision(18, 4);
            b.Property(e => e.UnitCostRestocked).HasPrecision(18, 4);
            b.HasIndex(e => new { e.CreditNoteId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.InvoiceLineId, e.BusinessId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_credit_note_lines_amounts",
                "quantity_credited > 0 AND unit_price >= 0 AND tax_amount >= 0 AND line_total >= 0 AND unit_cost_restocked >= 0"));
        });
    }

    private void ConfigurePurchasing(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.ToTable("purchase_orders");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.OrderNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.HasOne<Supplier>()
                .WithMany()
                .HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OrderNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.Status });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_purchase_orders_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
        });

        modelBuilder.Entity<PurchaseOrderLine>(b =>
        {
            b.ToTable("purchase_order_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.HasOne<PurchaseOrder>()
                .WithMany()
                .HasForeignKey(e => new { e.PurchaseOrderId, e.BusinessId })
                .HasPrincipalKey(p => new { p.Id, p.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.PurchaseOrderId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_purchase_order_lines_quantities",
                "quantity_ordered > 0 AND quantity_received >= 0 AND quantity_received <= quantity_ordered AND unit_cost >= 0"));
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.ToTable("expenses");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Description).HasColumnType("text").IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.TaxRate).HasPrecision(18, 6);
            b.Property(e => e.TaxAmount).HasPrecision(18, 4);
            b.Property(e => e.TaxMode).HasMaxLength(16).IsRequired();
            b.Property(e => e.Reference).HasMaxLength(128);
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<ExpenseCategory>()
                .WithMany()
                .HasForeignKey(e => new { e.ExpenseCategoryId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>()
                .WithMany()
                .HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(s => new { s.Id, s.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OccurredOn });
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_expenses_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_expenses_amount_positive", "amount > 0"));
        });

        modelBuilder.Entity<ExpenseReversal>(b =>
        {
            b.ToTable("expense_reversals");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Reason).HasColumnType("text").IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<Expense>()
                .WithMany()
                .HasForeignKey(e => new { e.ExpenseId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ReversedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.ExpenseId })
                .IsUnique()
                .HasDatabaseName("ux_expense_reversals_business_expense");
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_expense_reversals_business_idempotency_key");
        });

        modelBuilder.Entity<PurchaseReceipt>(b =>
        {
            b.ToTable("purchase_receipts");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.ReceiptNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<PurchaseOrder>()
                .WithMany()
                .HasForeignKey(e => new { e.PurchaseOrderId, e.BusinessId })
                .HasPrincipalKey(p => new { p.Id, p.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.ReceiptNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.PurchaseOrderId });
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_purchase_receipts_business_idempotency_key");
        });

        modelBuilder.Entity<PurchaseReceiptLine>(b =>
        {
            b.ToTable("purchase_receipt_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.Property(e => e.UnitCost).HasPrecision(18, 6);
            b.HasOne<PurchaseReceipt>()
                .WithMany()
                .HasForeignKey(e => new { e.PurchaseReceiptId, e.BusinessId })
                .HasPrincipalKey(r => new { r.Id, r.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrderLine>()
                .WithMany()
                .HasForeignKey(e => new { e.PurchaseOrderLineId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.PurchaseReceiptId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_purchase_receipt_lines_quantities",
                "quantity > 0 AND unit_cost >= 0"));
        });

        modelBuilder.Entity<SupplierBill>(b =>
        {
            b.ToTable("supplier_bills");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.BillNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.SupplierInvoiceReference).HasMaxLength(128);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<Supplier>().WithMany().HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(supplier => new { supplier.Id, supplier.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(location => new { location.Id, location.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(e => new { e.PurchaseOrderId, e.BusinessId })
                .HasPrincipalKey(order => new { order.Id, order.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(journal => new { journal.Id, journal.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.BillNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.SupplierId, e.Status, e.DueDate });
            b.HasIndex(e => new { e.BusinessId, e.SupplierId, e.SupplierInvoiceReference })
                .IsUnique().HasFilter("supplier_invoice_reference IS NOT NULL")
                .HasDatabaseName("ux_supplier_bills_business_supplier_reference");
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique().HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_supplier_bills_business_idempotency_key");
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_bills_amounts",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND amount_paid >= 0 AND balance_due >= 0 AND balance_due = total_amount - amount_paid"));
        });

        modelBuilder.Entity<SupplierBillLine>(b =>
        {
            b.ToTable("supplier_bill_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.TaxMode).HasMaxLength(16).IsRequired();
            b.Property(e => e.QuantityBilled).HasPrecision(18, 6);
            b.Property(e => e.UnitCost).HasPrecision(18, 6);
            b.Property(e => e.TaxRate).HasPrecision(12, 6);
            b.HasOne<SupplierBill>().WithMany().HasForeignKey(e => new { e.SupplierBillId, e.BusinessId })
                .HasPrincipalKey(bill => new { bill.Id, bill.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseReceiptLine>().WithMany().HasForeignKey(e => new { e.PurchaseReceiptLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>().WithMany().HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(variant => new { variant.Id, variant.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.SupplierBillId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.PurchaseReceiptLineId });
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_bill_lines_amounts",
                "quantity_billed > 0 AND unit_cost >= 0 AND gross_amount >= 0 AND discount_amount >= 0 AND discount_amount <= gross_amount AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND line_total >= 0 AND net_amount >= 0 AND line_total = net_amount + tax_amount AND ((tax_mode = 'Inclusive' AND line_total = gross_amount - discount_amount) OR (tax_mode IN ('Exclusive', 'Exempt') AND net_amount = gross_amount - discount_amount AND (tax_mode <> 'Exempt' OR tax_amount = 0)) )"));
        });

        modelBuilder.Entity<SupplierPayment>(b =>
        {
            b.ToTable("supplier_payments");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reference).HasMaxLength(128);
            b.HasOne<Supplier>().WithMany().HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(supplier => new { supplier.Id, supplier.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(journal => new { journal.Id, journal.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SupplierId, e.PaidOn });
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_payments_amount_positive", "amount > 0"));
        });

        modelBuilder.Entity<SupplierPaymentAllocation>(b =>
        {
            b.ToTable("supplier_payment_allocations");
            HasBusinessFk(b);
            b.HasOne<SupplierPayment>().WithMany().HasForeignKey(e => new { e.SupplierPaymentId, e.BusinessId })
                .HasPrincipalKey(payment => new { payment.Id, payment.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SupplierBill>().WithMany().HasForeignKey(e => new { e.SupplierBillId, e.BusinessId })
                .HasPrincipalKey(bill => new { bill.Id, bill.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenItem>()
                .WithMany()
                .HasForeignKey(e => new { e.OpenItemId, e.BusinessId })
                .HasPrincipalKey(openItem => new { openItem.Id, openItem.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.AllocatedAmount).HasPrecision(18, 4);
            b.HasIndex(e => new { e.BusinessId, e.SupplierBillId });
            b.HasIndex(e => new { e.BusinessId, e.OpenItemId });
            b.HasIndex(e => new { e.SupplierPaymentId, e.SupplierBillId }).IsUnique().HasFilter("supplier_bill_id IS NOT NULL");
            b.HasIndex(e => new { e.SupplierPaymentId, e.OpenItemId }).IsUnique().HasFilter("open_item_id IS NOT NULL");
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_payment_allocations_amount_positive", "allocated_amount > 0"));

            // Exactly one target (specification task 27, recipe step 2): a settlement says which document it pays -
            // an issued bill or an imported opening payable - and never both or neither. Every row written before this
            // option existed named a bill and satisfies the same rule, so no posted allocation is reinterpreted.
            b.ToTable(t => t.HasCheckConstraint(
                "ck_supplier_payment_allocations_exactly_one_target",
                "((supplier_bill_id IS NOT NULL AND open_item_id IS NULL) OR (supplier_bill_id IS NULL AND open_item_id IS NOT NULL))"));
        });

        modelBuilder.Entity<SupplierCreditNote>(b =>
        {
            b.ToTable("supplier_credit_notes");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.CreditNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Reason).HasMaxLength(512).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.BaseAmount).HasPrecision(18, 4);
            b.Property(e => e.TaxAmount).HasPrecision(18, 4);
            b.Property(e => e.ValueAmount).HasPrecision(18, 4);
            b.Property(e => e.AppliedAmount).HasPrecision(18, 4);
            b.Property(e => e.BalanceDue).HasPrecision(18, 4);
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<SupplierBill>().WithMany().HasForeignKey(e => new { e.SupplierBillId, e.BusinessId })
                .HasPrincipalKey(bill => new { bill.Id, bill.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(supplier => new { supplier.Id, supplier.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(location => new { location.Id, location.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(e => new { e.SourcePurchaseReceiptId, e.BusinessId })
                .HasPrincipalKey(receipt => new { receipt.Id, receipt.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(journal => new { journal.Id, journal.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CreditNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.SupplierBillId });
            b.HasIndex(e => new { e.BusinessId, e.SupplierId, e.IssuedAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique().HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_supplier_credit_notes_business_idempotency_key");

            // The posted figures are the credit's own; the usable balance is derived from what has used it, so a
            // remaining credit is a stated figure with an account behind it rather than a negative payable (task 27).
            b.ToTable(t => t.HasCheckConstraint(
                "ck_supplier_credit_notes_amounts",
                "base_amount >= 0 AND tax_amount >= 0 AND value_amount > 0 AND value_amount = base_amount + tax_amount AND applied_amount >= 0 AND balance_due >= 0 AND balance_due = value_amount - applied_amount"));
        });

        modelBuilder.Entity<SupplierCreditNoteLine>(b =>
        {
            b.ToTable("supplier_credit_note_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.Property(e => e.UnitCost).HasPrecision(18, 6);
            b.Property(e => e.BaseAmount).HasPrecision(18, 4);
            b.Property(e => e.TaxRate).HasPrecision(12, 6);
            b.Property(e => e.TaxAmount).HasPrecision(18, 4);
            b.Property(e => e.ValueAmount).HasPrecision(18, 4);
            b.HasOne<SupplierCreditNote>().WithMany().HasForeignKey(e => new { e.SupplierCreditNoteId, e.BusinessId })
                .HasPrincipalKey(credit => new { credit.Id, credit.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SupplierBillLine>().WithMany().HasForeignKey(e => new { e.SupplierBillLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseReceiptLine>().WithMany().HasForeignKey(e => new { e.PurchaseReceiptLineId, e.BusinessId })
                .HasPrincipalKey(line => new { line.Id, line.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>().WithMany().HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(variant => new { variant.Id, variant.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.SupplierCreditNoteId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.SupplierBillLineId });
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_credit_note_lines_amounts",
                "quantity > 0 AND unit_cost >= 0 AND base_amount >= 0 AND tax_rate >= 0 AND tax_rate < 1 AND tax_amount >= 0 AND value_amount = base_amount + tax_amount"));
        });

        modelBuilder.Entity<SupplierCreditApplication>(b =>
        {
            b.ToTable("supplier_credit_applications");
            HasBusinessFk(b);
            b.Property(e => e.Amount).HasPrecision(18, 4);
            b.HasOne<SupplierCreditNote>().WithMany().HasForeignKey(e => new { e.SupplierCreditNoteId, e.BusinessId })
                .HasPrincipalKey(credit => new { credit.Id, credit.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SupplierBill>().WithMany().HasForeignKey(e => new { e.SupplierBillId, e.BusinessId })
                .HasPrincipalKey(bill => new { bill.Id, bill.BusinessId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenItem>()
                .WithMany()
                .HasForeignKey(e => new { e.OpenItemId, e.BusinessId })
                .HasPrincipalKey(openItem => new { openItem.Id, openItem.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SupplierCreditNoteId });
            b.HasIndex(e => new { e.BusinessId, e.SupplierBillId });
            b.HasIndex(e => new { e.BusinessId, e.OpenItemId });
            b.ToTable(t => t.HasCheckConstraint("ck_supplier_credit_applications_amount_positive", "amount > 0"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_supplier_credit_applications_exactly_one_target",
                "((supplier_bill_id IS NOT NULL AND open_item_id IS NULL) OR (supplier_bill_id IS NULL AND open_item_id IS NOT NULL))"));
        });
    }

    private void ConfigurePlatform(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyReceipt>(b =>
        {
            b.ToTable("command_idempotency_receipts");
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.Scope).HasMaxLength(100).IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.ResponseJson).HasColumnType("jsonb").IsRequired().Metadata.SetMaxLength(null);
            b.HasIndex(e => new { e.BusinessId, e.Scope, e.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events");
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
            b.Property(e => e.Action).HasMaxLength(64).IsRequired();
            b.Property(e => e.CorrelationId).HasMaxLength(64);
            b.Property(e => e.Summary).HasColumnType("jsonb");
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DeviceRegistration>()
                .WithMany()
                .HasForeignKey(e => new { e.DeviceId, e.BusinessId })
                .HasPrincipalKey(d => new { d.Id, d.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OccurredAtUtc });
            b.HasIndex(e => new { e.EntityType, e.EntityId });
        });

        modelBuilder.Entity<ProcessedOperation>(b =>
        {
            b.ToTable("processed_operations");
            HasBusinessFk(b);
            b.HasOne<DeviceRegistration>()
                .WithMany()
                .HasForeignKey(e => new { e.DeviceId, e.BusinessId })
                .HasPrincipalKey(d => new { d.Id, d.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.OperationType).HasMaxLength(64).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.ErrorCode).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.DeviceId, e.OperationId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.ProcessedAtUtc });
        });

        modelBuilder.Entity<SyncRevision>(b =>
        {
            b.ToTable("sync_revisions");
            b.HasKey(e => e.BusinessId);
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncChange>(b =>
        {
            b.ToTable("sync_changes");
            b.HasKey(e => new { e.BusinessId, e.Revision, e.Ordinal });
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            b.Property(e => e.PayloadJson).HasColumnType("jsonb");
            b.HasIndex(e => new { e.BusinessId, e.Revision });
        });

        modelBuilder.Entity<LegacyIdentifierMap>(b =>
        {
            b.ToTable("legacy_identifier_map");
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.SourceCollection).HasMaxLength(64).IsRequired();
            b.Property(e => e.LegacyId).HasMaxLength(128).IsRequired();
            b.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
            b.HasIndex(e => new { e.SourceCollection, e.LegacyId }).IsUnique();
            b.HasIndex(e => new { e.EntityType, e.TargetId });
        });

        modelBuilder.Entity<ExternalRecordLink>(b =>
        {
            b.ToTable("external_record_links");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.DatasetNamespace).HasMaxLength(64).IsRequired();
            b.Property(e => e.ResourceType).HasMaxLength(64).IsRequired();
            b.Property(e => e.ExternalId).HasMaxLength(128).IsRequired();
            b.Property(e => e.PayloadSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.TargetType).HasMaxLength(64).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.DatasetNamespace, e.ResourceType, e.ExternalId })
                .IsUnique()
                .HasDatabaseName("ux_external_record_links_identity");
            b.HasIndex(e => new { e.BusinessId, e.BatchId, e.ResourceType, e.ExternalId })
                .IsUnique()
                .HasDatabaseName("ux_external_record_links_batch");
            b.HasIndex(e => new { e.BusinessId, e.TargetType, e.TargetId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_external_record_links_sha256",
                "length(payload_sha256) = 64"));
        });

        modelBuilder.Entity<OpenItem>(b =>
        {
            b.ToTable("open_items");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.SourceType).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reference).HasMaxLength(200);
            b.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(customer => new { customer.Id, customer.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>()
                .WithMany()
                .HasForeignKey(e => new { e.SupplierId, e.BusinessId })
                .HasPrincipalKey(supplier => new { supplier.Id, supplier.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(entry => new { entry.Id, entry.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SourceType, e.SourceId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.CustomerId });
            b.HasIndex(e => new { e.BusinessId, e.SupplierId });
            b.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_open_items_party",
                    "((kind = 'Receivable' AND customer_id IS NOT NULL AND supplier_id IS NULL) OR " +
                    "(kind = 'Payable' AND supplier_id IS NOT NULL AND customer_id IS NULL))");
                t.HasCheckConstraint("ck_open_items_amount", "original_amount > 0");
            });
        });
    }

    private static void ConfigureSettingsAccount(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<BusinessAccountingSettings> builder,
        System.Linq.Expressions.Expression<Func<BusinessAccountingSettings, object?>> accountId)
    {
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(accountId)
            .HasPrincipalKey(account => new { account.Id, account.BusinessId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
