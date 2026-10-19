using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Sqlite.Entities;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// Device-local SQLite model (QB-17, specification 4.2). It intentionally does not mirror the central
/// PostgreSQL accounting schema: it contains only discardable projections plus durable provisional money,
/// outbox, receipts and sync cursors. Its EF migration history (<c>__EFMigrationsHistory</c> inside the
/// SQLite file) is completely independent of the PostgreSQL migration history.
/// </summary>
public sealed class LocalStoreDbContext : DbContext
{
    public LocalStoreDbContext(DbContextOptions<LocalStoreDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeviceProfile> DeviceProfiles => Set<DeviceProfile>();

    public DbSet<CachedProduct> CachedProducts => Set<CachedProduct>();

    public DbSet<CachedCustomer> CachedCustomers => Set<CachedCustomer>();

    public DbSet<CachedStock> CachedStock => Set<CachedStock>();

    public DbSet<CacheTombstone> CacheTombstones => Set<CacheTombstone>();

    public DbSet<LocalSaleDraft> LocalSaleDrafts => Set<LocalSaleDraft>();

    public DbSet<LocalSaleLine> LocalSaleLines => Set<LocalSaleLine>();

    public DbSet<LocalCashMovement> LocalCashMovements => Set<LocalCashMovement>();

    public DbSet<OutboxOperation> OutboxOperations => Set<OutboxOperation>();

    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();

    public DbSet<SyncState> SyncStates => Set<SyncState>();

    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceProfile>(b =>
        {
            b.ToTable("device_profile");
            b.HasKey(x => x.DeviceId);
            b.Property(x => x.DeviceId).HasColumnName("device_id");
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.LocationId).HasColumnName("location_id");
            b.Property(x => x.EnrolledAtUtc).HasColumnName("enrolled_at_utc");
            b.Property(x => x.SchemaVersion).HasColumnName("schema_version");
        });

        modelBuilder.Entity<CachedProduct>(b =>
        {
            b.ToTable("cached_products");
            b.HasKey(x => new { x.BusinessId, x.LocationId, x.ProductId });
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.LocationId).HasColumnName("location_id");
            b.Property(x => x.ProductId).HasColumnName("product_id");
            b.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(64);
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(256);
            b.Property(x => x.Price).HasColumnName("price_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.TaxRate).HasColumnName("tax_scaled").HasConversion(LocalValueConverters.Quantity);
            b.Property(x => x.TaxMode).HasColumnName("tax_mode").HasMaxLength(16).IsRequired();
            b.Property(x => x.Active).HasColumnName("active");
            b.Property(x => x.Revision).HasColumnName("revision");
            b.HasIndex(x => new { x.BusinessId, x.LocationId, x.Name });
            b.HasIndex(x => x.Price);
            b.ToTable(t => t.HasCheckConstraint("ck_cached_products_price_non_negative", "price_scaled >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_cached_products_tax_non_negative", "tax_scaled >= 0"));
        });

        modelBuilder.Entity<CachedCustomer>(b =>
        {
            b.ToTable("cached_customers");
            b.HasKey(x => new { x.BusinessId, x.CustomerId });
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.FieldsNeededOffline).HasColumnName("fields_needed_offline").HasMaxLength(4000);
            b.Property(x => x.Revision).HasColumnName("revision");
            b.Property(x => x.Deleted).HasColumnName("deleted");
            b.HasIndex(x => new { x.BusinessId, x.Revision });
        });

        modelBuilder.Entity<CachedStock>(b =>
        {
            b.ToTable("cached_stock");
            b.HasKey(x => new { x.BusinessId, x.LocationId, x.VariantId });
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.LocationId).HasColumnName("location_id");
            b.Property(x => x.VariantId).HasColumnName("variant_id");
            b.Property(x => x.ServerQuantity).HasColumnName("server_qty_scaled").HasConversion(LocalValueConverters.Quantity);
            b.Property(x => x.Revision).HasColumnName("revision");
            b.HasIndex(x => new { x.BusinessId, x.LocationId, x.Revision });
        });

        modelBuilder.Entity<CacheTombstone>(b =>
        {
            b.ToTable("cache_tombstones");
            b.HasKey(x => new { x.EntityType, x.Id });
            b.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.DeletedRevision).HasColumnName("deleted_revision");
            b.HasIndex(x => x.DeletedRevision);
        });

        modelBuilder.Entity<LocalSaleDraft>(b =>
        {
            b.ToTable("local_sale_drafts");
            b.HasKey(x => x.OperationId);
            b.Property(x => x.OperationId).HasColumnName("operation_id");
            b.Property(x => x.ProvisionalReference).HasColumnName("provisional_reference").HasMaxLength(64);
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.RegisterId).HasColumnName("register_id");
            b.Property(x => x.LinesJson).HasColumnName("lines_json").HasMaxLength(4000);
            b.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16);
            b.Property(x => x.Amount).HasColumnName("amount_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            b.HasIndex(x => x.ProvisionalReference).IsUnique();
            b.HasIndex(x => new { x.State, x.CreatedAtUtc });
            b.HasOne<OutboxOperation>().WithOne().HasForeignKey<LocalSaleDraft>(x => x.OperationId)
                .OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_drafts_cash_only", "payment_method = 'cash'"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_local_sale_drafts_state",
                "state IN ('Pending','Sending','Posted','Rejected')"));
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_drafts_amount_non_negative", "amount_scaled >= 0"));
        });

        modelBuilder.Entity<LocalSaleLine>(b =>
        {
            b.ToTable("local_sale_lines");
            b.HasKey(x => new { x.OperationId, x.Ordinal });
            b.Property(x => x.OperationId).HasColumnName("operation_id");
            b.Property(x => x.Ordinal).HasColumnName("ordinal");
            b.Property(x => x.VariantId).HasColumnName("variant_id");
            b.Property(x => x.Quantity).HasColumnName("qty_scaled").HasConversion(LocalValueConverters.Quantity);
            b.Property(x => x.UnitPrice).HasColumnName("unit_price_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.Tax).HasColumnName("tax_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.Discount).HasColumnName("discount_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.ExpectedCatalogRevision).HasColumnName("expected_catalog_revision");
            b.HasOne(x => x.Draft).WithMany(x => x.Lines).HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_lines_ordinal", "ordinal >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_lines_qty_positive", "qty_scaled > 0"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_local_sale_lines_unit_price_non_negative", "unit_price_scaled >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_lines_tax_non_negative", "tax_scaled >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_local_sale_lines_discount_non_negative", "discount_scaled >= 0"));
        });

        modelBuilder.Entity<LocalCashMovement>(b =>
        {
            b.ToTable("local_cash_movements");
            b.HasKey(x => x.OperationId);
            b.Property(x => x.OperationId).HasColumnName("operation_id");
            b.Property(x => x.RegisterId).HasColumnName("register_id");
            b.Property(x => x.Amount).HasColumnName("amount_scaled").HasConversion(LocalValueConverters.Money);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256);
            b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => x.State);
            b.HasOne<OutboxOperation>().WithOne().HasForeignKey<LocalCashMovement>(x => x.OperationId)
                .OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t => t.HasCheckConstraint(
                "ck_local_cash_movements_state",
                "state IN ('Pending','Sending','Posted','Rejected')"));
        });

        modelBuilder.Entity<OutboxOperation>(b =>
        {
            b.ToTable("outbox_operations");
            b.HasKey(x => x.OperationId);
            b.Property(x => x.OperationId).HasColumnName("operation_id");
            b.Property(x => x.DeviceId).HasColumnName("device_id");
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.LocationId).HasColumnName("location_id");
            b.Property(x => x.OperationType).HasColumnName("operation_type").HasMaxLength(64);
            b.Property(x => x.PayloadVersion).HasColumnName("payload_version");
            b.Property(x => x.CanonicalPayload).HasColumnName("canonical_payload");
            b.Property(x => x.Sha256Fingerprint).HasColumnName("sha256_fingerprint").HasMaxLength(64);
            b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.NextAttemptUtc).HasColumnName("next_attempt_utc");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasIndex(x => new { x.DeviceId, x.OperationId }).IsUnique();
            b.HasIndex(x => new { x.State, x.NextAttemptUtc, x.CreatedAtUtc });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_outbox_operations_state",
                "state IN ('Pending','Sending','Acknowledged','Rejected')"));
            b.ToTable(t => t.HasCheckConstraint("ck_outbox_operations_attempts", "attempts >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_outbox_operations_payload_version", "payload_version >= 1"));
        });

        modelBuilder.Entity<InboxReceipt>(b =>
        {
            b.ToTable("inbox_receipts");
            b.HasKey(x => x.OperationId);
            b.Property(x => x.OperationId).HasColumnName("operation_id");
            b.Property(x => x.ServerResult).HasColumnName("server_result").HasMaxLength(64);
            b.Property(x => x.ServerSaleId).HasColumnName("server_sale_id");
            b.Property(x => x.PostedNumber).HasColumnName("posted_number").HasMaxLength(64);
            b.Property(x => x.ServerRevision).HasColumnName("server_revision");
            b.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
            b.Property(x => x.AppliedAtUtc).HasColumnName("applied_at_utc");
            b.HasIndex(x => x.AppliedAtUtc);
            b.HasOne<OutboxOperation>().WithOne().HasForeignKey<InboxReceipt>(x => x.OperationId)
                .OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t => t.HasCheckConstraint("ck_inbox_receipts_server_result", "server_result <> ''"));
        });

        modelBuilder.Entity<SyncState>(b =>
        {
            b.ToTable("sync_state");
            b.HasKey(x => new { x.BusinessId, x.LocationId });
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.LocationId).HasColumnName("location_id");
            b.Property(x => x.LastRevision).HasColumnName("last_revision");
            b.Property(x => x.LastOrdinal).HasColumnName("last_ordinal");
            b.Property(x => x.LastSuccessUtc).HasColumnName("last_success_utc");
            b.Property(x => x.BaselineVersion).HasColumnName("baseline_version");
        });

        modelBuilder.Entity<SyncConflict>(b =>
        {
            b.ToTable("sync_conflicts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.BusinessId).HasColumnName("business_id");
            b.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64).IsRequired();
            b.Property(x => x.EntityId).HasColumnName("entity_id");
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256).IsRequired();
            b.Property(x => x.ServerValue).HasColumnName("server_value");
            b.Property(x => x.LocalValue).HasColumnName("local_value");
            b.Property(x => x.DetectedAtUtc).HasColumnName("detected_at_utc");
            b.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            b.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(32);
            b.HasIndex(x => new { x.BusinessId, x.ResolvedAtUtc });
        });
    }
}
