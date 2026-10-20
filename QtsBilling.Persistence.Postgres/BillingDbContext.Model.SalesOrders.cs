using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Persistence.Postgres;

public partial class BillingDbContext
{
    private void ConfigureSalesOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SalesDocumentSequence>(b =>
        {
            b.ToTable("sales_document_sequences", t => t.HasCheckConstraint(
                "ck_sales_document_sequences_values",
                "business_year BETWEEN 2000 AND 2200 AND next_number > 0 AND padding BETWEEN 1 AND 9"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.DocumentKind).HasMaxLength(24).IsRequired();
            b.Property(e => e.Prefix).HasMaxLength(16).IsRequired();
            b.HasOne<BusinessLocation>().WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.LocationId, e.DocumentKind, e.BusinessYear }).IsUnique();
        });

        modelBuilder.Entity<SalesQuote>(b =>
        {
            b.ToTable("sales_quotes", t => t.HasCheckConstraint(
                "ck_sales_quotes_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND current_revision >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.QuoteNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<BusinessLocation>().WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.SentByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.AcceptedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.QuoteNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.Status, e.QuoteDate });
            b.HasIndex(e => new { e.BusinessId, e.CustomerId, e.QuoteDate });
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        modelBuilder.Entity<SalesQuoteLine>(b =>
        {
            b.ToTable("sales_quote_lines", t => t.HasCheckConstraint(
                "ck_sales_quote_lines_values",
                "line_no > 0 AND quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Description).HasMaxLength(300).IsRequired();
            b.Property(e => e.SkuSnapshot).HasMaxLength(64);
            b.Property(e => e.TaxRate).HasPrecision(12, 6);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.Property(e => e.TaxMode).HasMaxLength(16).IsRequired();
            b.Property(e => e.PricingPolicyVersion).HasMaxLength(64).IsRequired();
            b.HasOne<SalesQuote>().WithMany()
                .HasForeignKey(e => new { e.QuoteId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>().WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.QuoteId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.LineKey }).IsUnique();
        });

        modelBuilder.Entity<SalesQuoteRevision>(b =>
        {
            b.ToTable("sales_quote_revisions", t => t.HasCheckConstraint(
                "ck_sales_quote_revisions_values",
                "revision_number > 0 AND subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            b.HasOne<SalesQuote>().WithMany()
                .HasForeignKey(e => new { e.QuoteId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.QuoteId, e.RevisionNumber }).IsUnique();
        });

        modelBuilder.Entity<SalesQuoteRevisionLine>(b =>
        {
            b.ToTable("sales_quote_revision_lines", t => t.HasCheckConstraint(
                "ck_sales_quote_revision_lines_values",
                "line_no > 0 AND quantity > 0 AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            ConfigureQuoteLineSnapshot(b);
            b.HasOne<SalesQuoteRevision>().WithMany()
                .HasForeignKey(e => new { e.QuoteRevisionId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.QuoteRevisionId, e.LineNo }).IsUnique();
        });

        modelBuilder.Entity<SalesOrder>(b =>
        {
            b.ToTable("sales_orders", t => t.HasCheckConstraint(
                "ck_sales_orders_amounts_non_negative",
                "subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0 AND current_revision >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.OrderNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.RemainderCancellationReason).HasMaxLength(500);
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<BusinessLocation>().WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany()
                .HasForeignKey(e => new { e.CustomerId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesQuote>().WithMany()
                .HasForeignKey(e => new { e.SourceQuoteId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesQuoteRevision>().WithMany()
                .HasForeignKey(e => new { e.SourceQuoteRevisionId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.RemainderCancelledByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OrderNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.Status, e.OrderDate });
            b.HasIndex(e => new { e.BusinessId, e.CustomerId, e.Status });
            b.HasIndex(e => new { e.BusinessId, e.SourceQuoteId }).IsUnique().HasFilter("source_quote_id IS NOT NULL");
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        modelBuilder.Entity<SalesOrderLine>(b =>
        {
            b.ToTable("sales_order_lines", t => t.HasCheckConstraint(
                "ck_sales_order_lines_quantities",
                "line_no > 0 AND quantity_ordered > 0 AND quantity_fulfilled >= 0 AND quantity_cancelled >= 0 AND quantity_fulfilled + quantity_cancelled <= quantity_ordered AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            ConfigureOrderLineSnapshot(b);
            b.Property(e => e.QuantityOrdered).HasPrecision(18, 6);
            b.Property(e => e.QuantityFulfilled).HasPrecision(18, 6);
            b.Property(e => e.QuantityCancelled).HasPrecision(18, 6);
            b.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(e => new { e.OrderId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesQuoteRevisionLine>().WithMany()
                .HasForeignKey(e => new { e.SourceQuoteRevisionLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.OrderId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.LineKey }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
        });

        modelBuilder.Entity<SalesOrderRevision>(b =>
        {
            b.ToTable("sales_order_revisions", t => t.HasCheckConstraint(
                "ck_sales_order_revisions_values",
                "revision_number > 0 AND subtotal_amount >= 0 AND discount_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            b.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(e => new { e.OrderId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.OrderId, e.RevisionNumber }).IsUnique();
        });

        modelBuilder.Entity<SalesOrderRevisionLine>(b =>
        {
            b.ToTable("sales_order_revision_lines", t => t.HasCheckConstraint(
                "ck_sales_order_revision_lines_values",
                "line_no > 0 AND quantity_ordered > 0 AND quantity_fulfilled >= 0 AND quantity_cancelled >= 0 AND quantity_fulfilled + quantity_cancelled <= quantity_ordered AND unit_price >= 0 AND discount_amount >= 0 AND revenue_base_amount >= 0 AND tax_rate >= 0 AND tax_amount >= 0 AND line_payable_amount >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            ConfigureOrderLineSnapshot(b);
            b.Property(e => e.QuantityOrdered).HasPrecision(18, 6);
            b.Property(e => e.QuantityFulfilled).HasPrecision(18, 6);
            b.Property(e => e.QuantityCancelled).HasPrecision(18, 6);
            b.HasOne<SalesOrderRevision>().WithMany()
                .HasForeignKey(e => new { e.OrderRevisionId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.OrderRevisionId, e.LineNo }).IsUnique();
        });
    }

    private static void ConfigureQuoteLineSnapshot<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> b)
        where TEntity : class, ITenantScoped
    {
        b.Property("Description").HasMaxLength(300).IsRequired();
        b.Property("SkuSnapshot").HasMaxLength(64);
        b.Property("Quantity").HasPrecision(18, 6);
        b.Property("TaxRate").HasPrecision(12, 6);
        b.Property("TaxMode").HasMaxLength(16).IsRequired();
        b.Property("PricingPolicyVersion").HasMaxLength(64).IsRequired();
    }

    private static void ConfigureOrderLineSnapshot<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> b)
        where TEntity : class, ITenantScoped
    {
        b.Property("Description").HasMaxLength(300).IsRequired();
        b.Property("SkuSnapshot").HasMaxLength(64);
        b.Property("TaxRate").HasPrecision(12, 6);
        b.Property("TaxMode").HasMaxLength(16).IsRequired();
        b.Property("PricingPolicyVersion").HasMaxLength(64).IsRequired();
        b.HasOne<ProductVariant>().WithMany()
            .HasForeignKey("VariantId", "BusinessId")
            .HasPrincipalKey(nameof(ProductVariant.Id), nameof(ProductVariant.BusinessId))
            .OnDelete(DeleteBehavior.Restrict);
    }
}
