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
            b.HasOne<Customer>().WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>().WithMany().HasForeignKey(e => e.ReversalOfSaleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SaleNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.PostedAtUtc });
            b.HasIndex(e => new { e.BusinessId, e.CustomerId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_sales_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
        });

        modelBuilder.Entity<SaleLine>(b =>
        {
            b.ToTable("sale_lines");
            HasBusinessFk(b);
            b.Property(e => e.ProductNameSnapshot).HasMaxLength(200).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
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
            b.HasOne<Sale>().WithMany().HasForeignKey(e => e.SaleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SaleId });
            b.HasIndex(e => new { e.BusinessId, e.PspReference })
                .IsUnique()
                .HasFilter("\"psp_reference\" IS NOT NULL");
            b.ToTable(t => t.HasCheckConstraint("ck_payments_amount_positive", "amount > 0"));
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
            b.HasOne<Sale>().WithMany().HasForeignKey(e => e.SaleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.CashRegisterId, e.OccurredAtUtc });
            b.ToTable(t => t.HasCheckConstraint("ck_cash_register_movements_amount_non_negative", "amount >= 0"));
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
            b.HasOne<Account>().WithMany().HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("journal_entries");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.EntryNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Memo).HasColumnType("text");
            b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(e => e.AccountingPeriodId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => e.ReversalOfEntryId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
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
            b.Property(e => e.Note).HasColumnType("text");
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sale>().WithMany().HasForeignKey(e => e.SourceSaleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InvoiceSequence>().WithMany().HasForeignKey(e => e.InvoiceSequenceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.InvoiceNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.IssueDate });
            b.HasIndex(e => new { e.BusinessId, e.Status });
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
            b.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(e => new { e.InvoiceId, e.BusinessId })
                .HasPrincipalKey(i => new { i.Id, i.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProductVariant>().WithMany().HasForeignKey(e => e.VariantId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.InvoiceId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_invoice_lines_amounts",
                "quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND tax_rate >= 0 AND tax_rate <= 1"));
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
            b.HasOne<Supplier>().WithMany().HasForeignKey(e => e.SupplierId).OnDelete(DeleteBehavior.Restrict);
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
                "quantity_ordered > 0 AND quantity_received >= 0 AND unit_cost >= 0"));
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.ToTable("expenses");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Description).HasColumnType("text").IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reference).HasMaxLength(128);
            b.HasOne<ExpenseCategory>().WithMany().HasForeignKey(e => e.ExpenseCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(e => e.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OccurredOn });
            b.ToTable(t => t.HasCheckConstraint("ck_expenses_amount_positive", "amount > 0"));
        });
    }

    private void ConfigurePlatform(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events");
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
            b.Property(e => e.Action).HasMaxLength(64).IsRequired();
            b.Property(e => e.CorrelationId).HasMaxLength(64);
            b.Property(e => e.Summary).HasColumnType("jsonb");
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DeviceRegistration>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OccurredAtUtc });
            b.HasIndex(e => new { e.EntityType, e.EntityId });
        });

        modelBuilder.Entity<ProcessedOperation>(b =>
        {
            b.ToTable("processed_operations");
            HasBusinessFk(b);
            b.HasOne<DeviceRegistration>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Restrict);
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
    }
}
