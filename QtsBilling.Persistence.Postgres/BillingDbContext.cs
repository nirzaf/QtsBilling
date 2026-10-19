using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// Authoritative PostgreSQL context (PostgreSQL 18, EF Core 10, Npgsql). One schema serves all
/// businesses; tenant isolation is enforced by explicit <c>business_id</c> columns, composite
/// foreign keys and server-side authorization (specification §3.1/§3.3, F-22).
/// Identity tables are mapped into the same schema (QB-04).
/// </summary>
public partial class BillingDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>
{
    /// <summary>Tables that are append-only: modifications and deletes are rejected by the context itself.</summary>
    private static readonly HashSet<Type> AppendOnlyTypes =
    [
        typeof(InventoryMovement),
        typeof(JournalLine),
        typeof(SaleLine),
        typeof(InvoiceLine),
        typeof(PurchaseOrderLine),
        typeof(CashRegisterMovement),
        typeof(AuditEvent),
        typeof(ProcessedOperation),
        typeof(SyncChange),
        typeof(LegacyIdentifierMap),
    ];

    public BillingDbContext(DbContextOptions<BillingDbContext> options)
        : base(options)
    {
    }

    public DbSet<Business> Businesses => Set<Business>();

    public DbSet<BusinessLocation> BusinessLocations => Set<BusinessLocation>();

    public DbSet<BusinessMembership> BusinessMemberships => Set<BusinessMembership>();

    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<ProductGroup> ProductGroups => Set<ProductGroup>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    public DbSet<Discount> Discounts => Set<Discount>();

    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();

    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<SaleLine> SaleLines => Set<SaleLine>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<CashRegister> CashRegisters => Set<CashRegister>();

    public DbSet<CashRegisterMovement> CashRegisterMovements => Set<CashRegisterMovement>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    public DbSet<InvoiceSequence> InvoiceSequences => Set<InvoiceSequence>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<ProcessedOperation> ProcessedOperations => Set<ProcessedOperation>();

    public DbSet<SyncRevision> SyncRevisions => Set<SyncRevision>();

    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();

    public DbSet<LegacyIdentifierMap> LegacyIdentifierMaps => Set<LegacyIdentifierMap>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // numeric(18,4) money; longer precisions are set explicitly for quantities and rates.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date");

        // Enums are stored as readable text (accounting_method = 'fifo', status = 'posted', ...).
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureIdentityTables(modelBuilder);
        ConfigureCore(modelBuilder);
        ConfigureMasterData(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureInventory(modelBuilder);
        ConfigureSales(modelBuilder);
        ConfigureAccounting(modelBuilder);
        ConfigurePurchasing(modelBuilder);
        ConfigurePlatform(modelBuilder);

        ApplyGlobalConventions(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareEntries();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareEntries();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareEntries()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ITimestamped added)
                    {
                        if (added.CreatedAtUtc == default)
                        {
                            added.CreatedAtUtc = now;
                        }

                        added.UpdatedAtUtc = now;
                    }

                    if (entry.Entity is IVersioned addedVersioned && addedVersioned.Version == 0)
                    {
                        addedVersioned.Version = 1;
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is ITimestamped modified)
                    {
                        modified.UpdatedAtUtc = now;
                    }

                    if (entry.Entity is IVersioned modifiedVersioned)
                    {
                        var version = entry.Property(nameof(IVersioned.Version));
                        version.CurrentValue = (long)version.OriginalValue! + 1;
                    }

                    break;
            }
        }

        EnforceFinancialInvariants();
    }

    /// <summary>
    /// Rejects context-level mutations that the domain forbids: append-only tables can never be updated
    /// or deleted, and posted financial records cannot be hard-deleted or have their money columns
    /// changed (specification §3.4, F-15).
    /// </summary>
    private void EnforceFinancialInvariants()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var type = entry.Entity.GetType();

            if (AppendOnlyTypes.Contains(type))
            {
                throw new InvalidOperationException(
                    $"{type.Name} rows are append-only; corrections must be recorded as new rows (specification §3.1/§3.4).");
            }

            switch (entry.Entity)
            {
                case Sale sale when entry.State == EntityState.Deleted:
                    throw new InvalidOperationException(
                        $"Posted sale {sale.Id} cannot be hard-deleted; post a reversal entry instead (F-15).");

                case JournalEntry journal when entry.State == EntityState.Deleted:
                    throw new InvalidOperationException(
                        $"Journal entry {journal.Id} cannot be hard-deleted; post a reversal entry instead (F-15).");

                case Invoice invoice when entry.State == EntityState.Deleted && invoice.Status != InvoiceStatus.Draft:
                    throw new InvalidOperationException(
                        $"Invoice {invoice.Id} is {invoice.Status} and cannot be hard-deleted; void it instead.");

                case Payment payment when entry.State == EntityState.Deleted
                    && payment.Status is not (PaymentStatus.Pending or PaymentStatus.Voided):
                    throw new InvalidOperationException(
                        $"Payment {payment.Id} is {payment.Status} and cannot be hard-deleted.");

                case Sale sale when entry.State == EntityState.Modified:
                    EnsureOnlyAllowedPropertiesChanged(
                        entry,
                        nameof(Sale.Status),
                        nameof(Sale.PaymentStatus),
                        nameof(Sale.Note));
                    _ = sale;
                    break;

                case JournalEntry journal when entry.State == EntityState.Modified:
                    EnsureOnlyAllowedPropertiesChanged(
                        entry,
                        nameof(JournalEntry.Status),
                        nameof(JournalEntry.PostedAtUtc),
                        nameof(JournalEntry.PostedByUserId),
                        nameof(JournalEntry.ReversalOfEntryId));
                    _ = journal;
                    break;
            }
        }
    }

    private static void EnsureOnlyAllowedPropertiesChanged(EntityEntry entry, params string[] allowedProperties)
    {
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal)
        {
            nameof(ITimestamped.UpdatedAtUtc),
            nameof(IVersioned.Version),
        };

        var illegal = entry.Properties
            .Where(p => p.IsModified && !allowed.Contains(p.Metadata.Name))
            .Select(p => p.Metadata.Name)
            .ToList();

        if (illegal.Count > 0)
        {
            throw new InvalidOperationException(
                $"{entry.Entity.GetType().Name} is a posted financial record; its '{string.Join("', '", illegal)}' "
                + "field(s) cannot be modified after posting (specification §3.4).");
        }
    }
}
