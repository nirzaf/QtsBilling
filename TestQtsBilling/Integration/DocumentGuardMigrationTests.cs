using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The posted-document and period guards are data migrations and must be reversible (task 09/10).
/// </summary>
public sealed class DocumentGuardMigrationTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public DocumentGuardMigrationTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T09_06_the_guards_can_be_reverted_and_reapplied()
    {
        const string beforeGuards = "20261020140848_AddNativeAuthorizationStore";

        await using var database = await _fixture.CreateUpgradeDatabaseAsync(beforeGuards);
        await database.MigrateToLatestAsync();

        Assert.True(await TriggerExistsAsync(database, "trg_invoices_immutable"));
        Assert.True(await TriggerExistsAsync(database, "trg_invoice_lines_immutable"));
        Assert.True(await TriggerExistsAsync(database, "trg_journal_lines_insert_draft_only"));
        Assert.True(await TriggerExistsAsync(database, "trg_accounting_periods_integrity"));
        Assert.True(await TriggerExistsAsync(database, "trg_audit_events_no_truncate"));

        // Both Down methods run, restoring the previous line trigger and removing the new guards.
        await database.MigrateToAsync(beforeGuards);

        Assert.False(await TriggerExistsAsync(database, "trg_invoices_immutable"));
        Assert.False(await TriggerExistsAsync(database, "trg_accounting_periods_integrity"));
        Assert.True(await TriggerExistsAsync(database, "trg_invoice_lines_draft_only"));
        Assert.True(await TriggerExistsAsync(database, "trg_purchase_order_lines_draft_only"));

        // And both Up methods run again over the restored schema.
        await database.MigrateToLatestAsync();

        Assert.True(await TriggerExistsAsync(database, "trg_invoices_immutable"));
        Assert.True(await TriggerExistsAsync(database, "trg_accounting_periods_integrity"));
    }

    private static Task<bool> TriggerExistsAsync(UpgradeDatabase database, string name)
        => database.ScalarAsync<bool>(
            $"select exists (select 1 from pg_trigger where tgname = '{name}' and not tgisinternal);");
}
