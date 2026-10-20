using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The safety of the regression harness itself (specification task 01, T01.01 - T01.04).
/// </summary>
/// <remarks>
/// The harness is the foundation every financial test stands on: if it can touch a production database
/// or if its assertions can be satisfied by a global cancellation, every later claim is worthless.
/// </remarks>
public sealed class HarnessSafetyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public HarnessSafetyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T01_01_a_production_shaped_connection_is_refused_before_any_privileged_connection()
    {
        // A database that is not a test database is refused.
        var productionShape = Assert.Throws<InvalidOperationException>(
            () => PostgresApiFixture.ValidateTestConnection(
                "Host=localhost;Port=5432;Database=qtsbilling;Username=qtsbilling;Password=qtsbilling"));
        Assert.Contains("qts_test", productionShape.Message, StringComparison.Ordinal);

        // A host that is not on the allowlist is refused.
        var remoteShape = Assert.Throws<InvalidOperationException>(
            () => PostgresApiFixture.ValidateTestConnection(
                "Host=db.example.internal;Port=5432;Database=qts_test_x;Username=qtsbilling;Password=x"));
        Assert.Contains("allowlist", remoteShape.Message, StringComparison.Ordinal);

        // The production connection string itself is refused, even on a local host.
        var previous = Environment.GetEnvironmentVariable(PostgresServiceCollectionExtensions.ConnectionStringVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                PostgresServiceCollectionExtensions.ConnectionStringVariable,
                "Host=localhost;Port=5432;Database=qts_test_prod;Username=qtsbilling;Password=qtsbilling");

            var same = Assert.Throws<InvalidOperationException>(
                () => PostgresApiFixture.ValidateTestConnection(
                    "Host=localhost;Port=5432;Database=qts_test_prod;Username=qtsbilling;Password=qtsbilling"));
            Assert.Contains("production", same.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgresServiceCollectionExtensions.ConnectionStringVariable, previous);
        }
    }

    [Fact]
    public async Task T01_02_a_cross_business_resource_is_refused_and_changes_nothing()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        var before = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessA.BusinessId));

        var otherBefore = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessB.BusinessId));

        // The owner of business A posts a line that belongs to business B, in business A's location.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessA.LocationId,
                currency = "QAR",
                lines = new[]
                {
                    new { variantId = _fixture.BusinessB.VariantId, quantity = 1, unitPrice = 100m, discountAmount = 0m, taxRate = 0.15m },
                },
                payment = new { method = "Cash", amount = 115m, cashReceived = 115m },
                note = "T01.02 cross-tenant attempt",
            }),
        };

        request.Headers.Add("Idempotency-Key", "t01-02-" + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);

        var after = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessA.BusinessId));

        FinancialAssertions.AssertUnchanged(before, after);

        // The other business is untouched as well.
        var otherAfter = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessB.BusinessId));

        FinancialAssertions.AssertUnchanged(otherBefore, otherAfter);
    }

    [Fact]
    public async Task T01_03_an_exception_before_the_commit_leaves_no_partial_effect()
    {
        var before = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessA.BusinessId));

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

            // A financial fact that is written and then abandoned with the transaction.
            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                EntityType = "harness",
                EntityId = Guid.CreateVersion7(),
                Action = "injected_failure",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Summary = "{}",
            });

            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        });

        var after = await _fixture.WithFreshContextAsync(db =>
            FinancialAssertions.CaptureAsync(db, _fixture.BusinessA.BusinessId));

        FinancialAssertions.AssertUnchanged(before, after);
    }

    [Fact]
    public void T01_04_a_global_cancellation_never_hides_a_broken_entry()
    {
        var broken = Guid.NewGuid();
        var compensating = Guid.NewGuid();

        // The second entry compensates the first globally, but the first entry is still unbalanced.
        var entries = new[]
        {
            (EntryId: broken, Debit: 115m, Credit: 100m),
            (EntryId: compensating, Debit: 0m, Credit: 15m),
        };

        var error = Assert.Throws<Xunit.Sdk.TrueException>(
            () => FinancialAssertions.AssertEntriesBalance(entries));

        Assert.Contains(broken.ToString(), error.Message, StringComparison.Ordinal);

        // A balanced set passes.
        FinancialAssertions.AssertEntriesBalance(
        [
            (EntryId: broken, Debit: 115m, Credit: 115m),
        ]);
    }
}
