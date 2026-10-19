using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c>. It never needs a reachable database:
/// migrations are authored from the model, and only <c>database update</c> connects.
/// </summary>
public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(PostgresServiceCollectionExtensions.ResolveConnectionString())
            .Options;

        return new BillingDbContext(options);
    }
}
