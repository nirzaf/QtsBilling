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
        });

        modelBuilder.Entity<BusinessMembership>(b =>
        {
            b.ToTable("business_memberships");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.BusinessId, e.UserId }).IsUnique();
        });

        modelBuilder.Entity<DeviceRegistration>(b =>
        {
            b.ToTable("device_registrations");
            RequireTenantKey(b);
            HasBusinessFk(b);
            b.HasOne<BusinessLocation>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Category>().WithMany().HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Category>().WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Brand>().WithMany().HasForeignKey(e => e.BrandId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProductGroup>().WithMany().HasForeignKey(e => e.ProductGroupId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Unit>().WithMany().HasForeignKey(e => e.UnitId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxRate>().WithMany().HasForeignKey(e => e.TaxRateId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Product>().WithMany().HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Restrict);
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
