using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// Model configuration — core, master data, catalog and inventory aggregates.
/// Composite <c>(id, business_id)</c> foreign keys make cross-business linkage structurally
/// impossible for the tables where a mis-join would leak stock or money between tenants (§3.3).
/// </summary>
public partial class BillingDbContext
{
    /// <summary>Identity tables live in the same schema with snake_case names (QB-04).</summary>
    private static void ConfigureIdentityTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b => b.ToTable("users"));
        modelBuilder.Entity<IdentityRole<Guid>>(b => b.ToTable("roles"));
        modelBuilder.Entity<IdentityUserRole<Guid>>(b => b.ToTable("user_roles"));
        modelBuilder.Entity<IdentityUserClaim<Guid>>(b => b.ToTable("user_claims"));
        modelBuilder.Entity<IdentityUserLogin<Guid>>(b => b.ToTable("user_logins"));
        modelBuilder.Entity<IdentityUserToken<Guid>>(b => b.ToTable("user_tokens"));
        modelBuilder.Entity<IdentityRoleClaim<Guid>>(b => b.ToTable("role_claims"));
    }

    private void ConfigureCore(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Business>(b =>
        {
            b.ToTable("businesses");
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.LegalName).HasMaxLength(300);
            b.Property(e => e.BaseCurrency).HasMaxLength(3).IsRequired();
            b.Property(e => e.TimeZone).HasMaxLength(64).IsRequired();
            b.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<BusinessLocation>(b =>
        {
            b.ToTable("business_locations");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Code).HasMaxLength(64);
            b.Property(e => e.Address).HasColumnType("text");
            b.Property(e => e.TimeZone).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.Code })
                .IsUnique()
                .HasFilter("\"code\" IS NOT NULL");
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.Property(e => e.Email).HasMaxLength(320).IsRequired();
            b.Property(e => e.NormalizedEmail).HasMaxLength(320).IsRequired();
            b.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
            b.HasIndex(e => e.NormalizedEmail).IsUnique();
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.PreferredBusinessId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BusinessMembership>(b =>
        {
            b.ToTable("business_memberships");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.UserId }).IsUnique();
            b.Property(e => e.AuthorizationRevision).HasDefaultValue(1L);
            b.Property(e => e.MaxDiscountRate).HasDefaultValue(1.000000m);
            b.Property(e => e.AllLocations).HasDefaultValue(true);
            b.Property(e => e.MaxDiscountRate).HasPrecision(9, 6);
            b.ToTable(t => t.HasCheckConstraint("ck_business_memberships_max_discount_rate", "max_discount_rate >= 0 AND max_discount_rate <= 1"));
        });

        modelBuilder.Entity<BusinessMembershipLocation>(b =>
        {
            b.ToTable("business_membership_locations");
            b.HasKey(e => e.Id);
            b.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessMembership>()
                .WithMany()
                .HasForeignKey(e => new { e.MembershipId, e.BusinessId })
                .HasPrincipalKey(membership => new { membership.Id, membership.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(location => new { location.Id, location.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.MembershipId, e.LocationId }).IsUnique();
        });

        modelBuilder.Entity<DeviceEnrolmentChallenge>(b =>
        {
            b.ToTable("device_enrolment_challenges");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(location => new { location.Id, location.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DeviceRegistration>()
                .WithMany()
                .HasForeignKey(e => new { e.ConsumedByDeviceId, e.BusinessId })
                .HasPrincipalKey(device => new { device.Id, device.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.ChallengeHash).HasMaxLength(64).IsRequired();
            b.HasIndex(e => e.ChallengeHash).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.LocationId, e.ExpiresAtUtc });
        });

        modelBuilder.Entity<DeviceRegistration>(b =>
        {
            b.ToTable("device_registrations");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Platform).HasMaxLength(32).IsRequired();
            b.Property(e => e.AppVersion).HasMaxLength(64);
            b.HasIndex(e => new { e.BusinessId, e.Status });
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });
    }

    private void ConfigureMasterData(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Email).HasMaxLength(320);
            b.Property(e => e.Phone).HasMaxLength(32);
            b.Property(e => e.PhoneNormalized).HasMaxLength(32);
            b.Property(e => e.Address).HasColumnType("text");
            b.Property(e => e.CreditLimit).HasPrecision(18, 4);
            b.ToTable(t => t.HasCheckConstraint("ck_customers_credit_limit", "credit_limit IS NULL OR credit_limit >= 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_customers_payment_terms", "payment_terms_days >= 0 AND payment_terms_days <= 3650"));
            b.HasIndex(e => new { e.BusinessId, e.PhoneNormalized })
                .IsUnique()
                .HasFilter("\"phone_normalized\" IS NOT NULL");
            b.HasIndex(e => new { e.BusinessId, e.Name });
            b.HasIndex(e => new { e.BusinessId, e.IsActive });
        });

        modelBuilder.Entity<Supplier>(b =>
        {
            b.ToTable("suppliers");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Email).HasMaxLength(320);
            b.Property(e => e.Phone).HasMaxLength(32);
            b.Property(e => e.Address).HasColumnType("text");
            b.HasIndex(e => new { e.BusinessId, e.Name });
        });

        modelBuilder.Entity<Unit>(b =>
        {
            b.ToTable("units");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            b.Property(e => e.ShortName).HasMaxLength(16).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.ShortName }).IsUnique();
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("categories");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasOne<Category>()
                .WithMany()
                .HasForeignKey(e => new { e.ParentId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<Brand>(b =>
        {
            b.ToTable("brands");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<ProductGroup>(b =>
        {
            b.ToTable("product_groups");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("tax_rates");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Rate).HasPrecision(12, 6);
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("ck_tax_rates_rate_fraction", "rate >= 0 AND rate <= 1"));
        });

        modelBuilder.Entity<Discount>(b =>
        {
            b.ToTable("discounts");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint(
                "ck_discounts_value_range",
                "(type = 'Percentage' AND value >= 0 AND value <= 100) OR (type = 'Fixed' AND value >= 0)"));
        });

        modelBuilder.Entity<ExpenseCategory>(b =>
        {
            b.ToTable("expense_categories");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => new { e.AccountId, e.BusinessId })
                .HasPrincipalKey(a => new { a.Id, a.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.Name }).IsUnique();
        });
    }

    private void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Sku).HasMaxLength(64);
            b.Property(e => e.AlertQuantity).HasPrecision(18, 6);
            b.HasOne<Category>()
                .WithMany()
                .HasForeignKey(e => new { e.CategoryId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Brand>()
                .WithMany()
                .HasForeignKey(e => new { e.BrandId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductGroup>()
                .WithMany()
                .HasForeignKey(e => new { e.ProductGroupId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Unit>()
                .WithMany()
                .HasForeignKey(e => new { e.UnitId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxRate>()
                .WithMany()
                .HasForeignKey(e => new { e.TaxRateId, e.BusinessId })
                .HasPrincipalKey(c => new { c.Id, c.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.Sku })
                .IsUnique()
                .HasFilter("\"sku\" IS NOT NULL");
            b.HasIndex(e => new { e.BusinessId, e.Name });
            b.HasIndex(e => new { e.BusinessId, e.IsActive });
        });

        modelBuilder.Entity<ProductVariant>(b =>
        {
            b.ToTable("product_variants");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Sku).HasMaxLength(64);
            b.Property(e => e.Barcode).HasMaxLength(64);
            b.HasOne<Product>()
                .WithMany()
                .HasForeignKey(e => new { e.ProductId, e.BusinessId })
                .HasPrincipalKey(p => new { p.Id, p.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.ProductId, e.Name }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.Barcode })
                .IsUnique()
                .HasFilter("\"barcode\" IS NOT NULL");
            b.HasIndex(e => new { e.BusinessId, e.ProductId });
        });
    }

    private void ConfigureInventory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryBalance>(b =>
        {
            b.ToTable("inventory_balances");
            RequireTenantKey(b);
            b.Property(e => e.OnHand).HasPrecision(18, 6);
            b.Property(e => e.ValueOnHand).HasPrecision(18, 4);
            b.Property(e => e.AverageUnitCost).HasPrecision(18, 8);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.LocationId, e.VariantId }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint("ck_inventory_balances_on_hand_non_negative", "on_hand >= 0"));
        });

        modelBuilder.Entity<InventoryMovement>(b =>
        {
            b.ToTable("inventory_movements");
            HasBusinessFk(b);
            b.Property(e => e.QuantityDelta).HasPrecision(18, 6);
            b.Property(e => e.ValueDelta).HasPrecision(18, 4);
            b.Property(e => e.ReferenceType).HasMaxLength(64).IsRequired();
            b.Property(e => e.Memo).HasColumnType("text");
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(v => new { v.Id, v.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.ReferenceType, e.ReferenceId, e.MovementType }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId, e.OccurredAtUtc });
        });

        modelBuilder.Entity<StockTransfer>(b =>
        {
            b.ToTable("stock_transfers");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.TransferNumber).HasMaxLength(64).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.Note).HasColumnType("text");
            b.Property(e => e.IdempotencyKey).HasMaxLength(64);
            b.Property(e => e.PayloadHash).HasMaxLength(64);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.SourceLocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BusinessLocation>()
                .WithMany()
                .HasForeignKey(e => new { e.DestinationLocationId, e.BusinessId })
                .HasPrincipalKey(l => new { l.Id, l.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.DispatchJournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.ReceiveJournalEntryId, e.BusinessId })
                .HasPrincipalKey(j => new { j.Id, j.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.DispatchedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.TransferNumber }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_stock_transfers_business_idempotency_key");
            b.HasIndex(e => new { e.BusinessId, e.Status, e.CreatedAtUtc });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_stock_transfers_distinct_locations",
                "source_location_id <> destination_location_id"));
        });

        modelBuilder.Entity<StockTransferLine>(b =>
        {
            b.ToTable("stock_transfer_lines");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.Property(e => e.Quantity).HasPrecision(18, 6);
            b.Property(e => e.UnitCostAtDispatch).HasPrecision(18, 8);
            b.Property(e => e.ValueAtDispatch).HasPrecision(18, 4);
            b.HasOne<StockTransfer>()
                .WithMany()
                .HasForeignKey(e => new { e.StockTransferId, e.BusinessId })
                .HasPrincipalKey(transfer => new { transfer.Id, transfer.BusinessId })
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => new { e.VariantId, e.BusinessId })
                .HasPrincipalKey(variant => new { variant.Id, variant.BusinessId })
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.StockTransferId, e.LineNo }).IsUnique();
            b.HasIndex(e => new { e.BusinessId, e.VariantId });
            b.ToTable(t => t.HasCheckConstraint(
                "ck_stock_transfer_lines_quantity_positive",
                "quantity > 0 AND (unit_cost_at_dispatch IS NULL OR unit_cost_at_dispatch >= 0) AND (value_at_dispatch IS NULL OR value_at_dispatch >= 0)"));
        });
    }

    /// <summary>
    /// Applied after explicit configuration: concurrency tokens, enum storage and snake_case naming
    /// (generated key/index/constraint names are derived from the final table and column names).
    /// </summary>
    private static void ApplyGlobalConventions(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.Name == nameof(IVersioned.Version) && property.ClrType == typeof(long))
            {
                property.IsConcurrencyToken = true;
            }

            if (property.ClrType.IsEnum)
            {
                property.SetColumnType("text");
            }
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null)
            {
                entity.SetTableName(SnakeCaseNaming.ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();
                if (column is not null)
                {
                    property.SetColumnName(SnakeCaseNaming.ToSnakeCase(column));
                }
            }
        }
    }

    private static void RequireTenantKey<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder)
        where TEntity : class, ITenantScoped, IEntity
        => builder.HasAlternateKey(e => new { e.Id, e.BusinessId });

    private static void HasBusinessFk<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder)
        where TEntity : class, ITenantScoped
        => builder.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
}
