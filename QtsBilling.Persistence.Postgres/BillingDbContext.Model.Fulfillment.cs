using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Persistence.Postgres;

public partial class BillingDbContext
{
    private void ConfigureFulfillment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryReservation>(b =>
        {
            b.ToTable("inventory_reservations", t => t.HasCheckConstraint(
                "ck_inventory_reservations_key",
                "length(idempotency_key) BETWEEN 1 AND 128 AND length(payload_hash) = 64"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.CloseReason).HasMaxLength(500);
            b.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(e => new { e.OrderId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>().WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.OrderId, e.Status, e.ExpiresAtUtc });
        });

        modelBuilder.Entity<InventoryReservationLine>(b =>
        {
            b.ToTable("inventory_reservation_lines", t => t.HasCheckConstraint(
                "ck_inventory_reservation_lines_quantities",
                "quantity_reserved > 0 AND quantity_consumed >= 0 AND quantity_released >= 0 AND quantity_consumed + quantity_released <= quantity_reserved"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.QuantityReserved).HasPrecision(18, 6);
            b.Property(e => e.QuantityConsumed).HasPrecision(18, 6);
            b.Property(e => e.QuantityReleased).HasPrecision(18, 6);
            b.HasOne<InventoryReservation>().WithMany()
                .HasForeignKey(e => new { e.ReservationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesOrderLine>().WithMany()
                .HasForeignKey(e => new { e.OrderLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>().WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.ReservationId, e.OrderLineId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.OrderLineId });
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
        });

        modelBuilder.Entity<SalesOrderDelivery>(b =>
        {
            b.ToTable("sales_order_deliveries", t => t.HasCheckConstraint(
                "ck_sales_order_deliveries_key",
                "length(delivery_number) BETWEEN 1 AND 64 AND length(idempotency_key) BETWEEN 1 AND 128 AND length(payload_hash) = 64"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.DeliveryNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
            b.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.Note).HasMaxLength(2_000);
            b.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(e => new { e.OrderId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>().WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.DeliveryNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.OrderId, e.DeliveryDate });
        });

        modelBuilder.Entity<SalesOrderDeliveryLine>(b =>
        {
            b.ToTable("sales_order_delivery_lines", t => t.HasCheckConstraint(
                "ck_sales_order_delivery_lines_values",
                "quantity > 0 AND unit_cost >= 0 AND cost_value >= 0 AND (track_stock_snapshot OR cost_value = 0)"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.HasOne<SalesOrderDelivery>().WithMany()
                .HasForeignKey(e => new { e.DeliveryId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesOrderLine>().WithMany()
                .HasForeignKey(e => new { e.OrderLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InventoryReservationLine>().WithMany()
                .HasForeignKey(e => new { e.ReservationLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>().WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.DeliveryId, e.OrderLineId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.OrderLineId });
        });

        modelBuilder.Entity<DeliveryInvoiceAllocation>(b =>
        {
            b.ToTable("delivery_invoice_allocations", t => t.HasCheckConstraint(
                "ck_delivery_invoice_allocations_values",
                "quantity > 0 AND cost_value >= 0"));
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.HasOne<SalesOrderDeliveryLine>().WithMany()
                .HasForeignKey(e => new { e.DeliveryLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InvoiceLine>().WithMany()
                .HasForeignKey(e => new { e.InvoiceLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany()
                .HasForeignKey(e => new { e.JournalEntryId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.InvoiceLineId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.DeliveryLineId });
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasOne<SalesOrder>().WithMany()
                .HasForeignKey(e => new { e.SourceOrderId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.SourceRequestKey).HasMaxLength(128);
            b.Property(e => e.SourcePayloadHash).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.SourceRequestKey }).IsUnique().HasFilter("source_request_key IS NOT NULL");
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.HasOne<SalesOrderDeliveryLine>().WithMany()
                .HasForeignKey(e => new { e.SourceDeliveryLineId, e.BusinessId })
                .HasPrincipalKey(e => new { e.Id, e.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.SourceDeliveryLineId });
        });
    }
}
