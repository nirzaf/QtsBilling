using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace QtsBilling.Persistence.Postgres;

/// <summary>
/// The native authorization store (specification task 22): OpenIddict applications, authorizations,
/// scopes and tokens live in the same PostgreSQL database as the business, so issuance and revocation
/// are transactional with the membership that grants them.
/// </summary>
public partial class BillingDbContext
{
    public DbSet<OpenIddictEntityFrameworkCoreApplication> OpenIddictApplications
        => Set<OpenIddictEntityFrameworkCoreApplication>();

    public DbSet<OpenIddictEntityFrameworkCoreAuthorization> OpenIddictAuthorizations
        => Set<OpenIddictEntityFrameworkCoreAuthorization>();

    public DbSet<OpenIddictEntityFrameworkCoreScope> OpenIddictScopes
        => Set<OpenIddictEntityFrameworkCoreScope>();

    public DbSet<OpenIddictEntityFrameworkCoreToken> OpenIddictTokens
        => Set<OpenIddictEntityFrameworkCoreToken>();

    private static void ConfigureOpenIddict(ModelBuilder modelBuilder)
    {
        modelBuilder.UseOpenIddict();

        // The store is infrastructure, not tenant data: its tables keep explicit names and are not
        // reached by the business-owned composite keys. The snake_case convention still names columns.
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("openiddict_applications");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("openiddict_authorizations");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("openiddict_scopes");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("openiddict_tokens");

        // The global 400-character string limit is for business text, not for the serialized store:
        // a token payload (the protected principal) and the JSON collections are unbounded documents.
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>(b =>
        {
            b.Property(entity => entity.DisplayNames).HasColumnType("text");
            b.Property(entity => entity.Permissions).HasColumnType("text");
            b.Property(entity => entity.PostLogoutRedirectUris).HasColumnType("text");
            b.Property(entity => entity.Properties).HasColumnType("text");
            b.Property(entity => entity.RedirectUris).HasColumnType("text");
            b.Property(entity => entity.Requirements).HasColumnType("text");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>(b =>
        {
            b.Property(entity => entity.Properties).HasColumnType("text");
            b.Property(entity => entity.Scopes).HasColumnType("text");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>(b =>
        {
            b.Property(entity => entity.DisplayNames).HasColumnType("text");
            b.Property(entity => entity.Properties).HasColumnType("text");
            b.Property(entity => entity.Resources).HasColumnType("text");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>(b =>
        {
            b.Property(entity => entity.Payload).HasColumnType("text");
            b.Property(entity => entity.Properties).HasColumnType("text");
        });
    }
}
