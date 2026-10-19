using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Features.Auth;

/// <summary>
/// Development-only bootstrap (never runs in Production): applies pending migrations and creates one
/// owner account whose credentials must still be established through the reset/invitation flow, so the
/// "no usable credential until safe enrolment" rule is exercised locally exactly as in production.
/// </summary>
public static class DevDataSeeder
{
    public const string DefaultOwnerEmail = "owner@qtsbilling.local";
    public const string DefaultOwnerPassword = "QtsBilling!Dev2026";
    public const string DefaultBusinessName = "QTS Demo Business";

    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!isDevelopment)
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var businessName = configuration["QtsBilling:DevOwner:Business"] ?? DefaultBusinessName;
        var email = (configuration["QtsBilling:DevOwner:Email"] ?? DefaultOwnerEmail).Trim();

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Name == businessName, cancellationToken).ConfigureAwait(false);

        if (business is null)
        {
            business = new Business
            {
                Name = businessName,
                LegalName = businessName,
                BaseCurrency = "QAR",
                TimeZone = "Asia/Qatar",
            };

            db.Businesses.Add(business);
            db.BusinessLocations.Add(new BusinessLocation { BusinessId = business.Id, Name = "Main", Code = "MAIN" });
            db.Units.Add(new Unit { BusinessId = business.Id, Name = "Piece", ShortName = "pc", AllowDecimals = false });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded development business {Business} ({BusinessId}).", business.Name, business.Id);
        }

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is null)
        {
            user = new User
            {
                UserName = email,
                Email = email,
                DisplayName = "Development Owner",
                IsActive = true,
                PasswordResetRequired = true,
            };

            var password = configuration["QtsBilling:DevOwner:Password"] ?? DefaultOwnerPassword;
            var created = await userManager.CreateAsync(user, password).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                logger.LogError(
                    "Development owner bootstrap failed: {Errors}",
                    string.Join("; ", created.Errors.Select(error => error.Description)));

                return;
            }

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = business.Id,
                UserId = user.Id,
                Role = MembershipRole.Owner,
                IsActive = true,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded development owner {Email} with role Owner (account setup pending).", email);
        }

        if (user.PasswordResetRequired)
        {
            var ticket = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);

            logger.LogWarning(
                "Account setup ticket for {Email}: POST /api/v1/auth/password-reset/complete {{ \"email\": \"{Email}\", \"token\": \"{Ticket}\", \"newPassword\": \"<new>\" }}",
                email,
                email,
                ticket);
        }
    }
}
