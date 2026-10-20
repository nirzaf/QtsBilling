using Npgsql;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The composite tenant keys (specification task 05, finding F08): a business-owned reference must
/// carry the business at both the foreign key and the principal key, so the database itself refuses
/// a row that points across tenants.
/// </summary>
/// <remarks>
/// Each wave proves the same rule at a different parent: the row is written with raw SQL, bypassing
/// the application entirely, and PostgreSQL must refuse it with a foreign key violation. The catalog
/// test proves the rule exhaustively: after all six waves no single-column foreign key to a
/// business-owned table is left.
/// </remarks>
public sealed class CompositeTenantKeyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public CompositeTenantKeyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T05_wave1_a_payment_may_not_reference_another_business_sale()
    {
        var saleB = Guid.CreateVersion7();
        var paymentA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                SaleSql(saleB, _fixture.BusinessB, "CROSS-W1-B", now),
                $"""
                insert into payments (id, method, status, amount, currency, received_at_utc,
                    created_at_utc, updated_at_utc, business_id, version, sale_id)
                values ('{paymentA}', 'Cash', 'Confirmed', 10, 'QAR', '{now:O}',
                    '{now:O}', '{now:O}', '{_fixture.BusinessA.BusinessId}', 0, '{saleB}');
                """,
            });
    }

    [Fact]
    public async Task T05_wave2_a_journal_entry_may_not_reverse_another_business_entry()
    {
        var entryB = Guid.CreateVersion7();
        var entryA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                JournalEntrySql(entryB, _fixture.BusinessB, "CROSS-W2-B", null, now),
                JournalEntrySql(entryA, _fixture.BusinessA, "CROSS-W2-A", entryB, now),
            });
    }

    [Fact]
    public async Task T05_wave3_a_device_may_not_be_enrolled_at_another_business_location()
    {
        var deviceA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                $"""
                insert into device_registrations (id, name, platform, status, enrolled_at_utc,
                    created_at_utc, updated_at_utc, business_id, version, location_id)
                values ('{deviceA}', 'Cross device', 'Windows', 'Active', '{now:O}',
                    '{now:O}', '{now:O}', '{_fixture.BusinessA.BusinessId}', 0,
                    '{_fixture.BusinessB.LocationId}');
                """,
            });
    }

    [Fact]
    public async Task T05_wave4_a_sale_may_not_belong_to_another_business_customer()
    {
        var customerB = Guid.CreateVersion7();
        var saleA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                $"""
                insert into customers (id, name, is_active, created_at_utc, updated_at_utc,
                    business_id, version)
                values ('{customerB}', 'Cross customer', true, '{now:O}', '{now:O}',
                    '{_fixture.BusinessB.BusinessId}', 0);
                """,
                SaleSql(saleA, _fixture.BusinessA, "CROSS-W4-A", now, customerB),
            });
    }

    [Fact]
    public async Task T05_wave5_a_category_may_not_have_another_business_parent()
    {
        var categoryB = Guid.CreateVersion7();
        var categoryA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                CategorySql(categoryB, _fixture.BusinessB, "CROSS-W5-B", null, now),
                CategorySql(categoryA, _fixture.BusinessA, "CROSS-W5-A", categoryB, now),
            });
    }

    [Fact]
    public async Task T05_wave6_an_account_may_not_have_another_business_parent()
    {
        var accountB = Guid.CreateVersion7();
        var accountA = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await AssertCrossTenantInsertRefusedAsync(
            new[]
            {
                AccountSql(accountB, _fixture.BusinessB, "CROSS-W6-B", null, now),
                AccountSql(accountA, _fixture.BusinessA, "CROSS-W6-A", accountB, now),
            });
    }

    [Fact]
    public async Task T05_no_single_column_foreign_key_to_a_business_owned_table_remains()
    {
        const string sql = """
            select count(*)
            from pg_constraint con
            where con.contype = 'f'
              and array_length(con.conkey, 1) = 1
              and con.confrelid <> 'businesses'::regclass
              and exists (
                  select 1
                  from information_schema.columns c
                  where c.table_schema = 'public'
                    and c.table_name = con.confrelid::regclass::text
                    and c.column_name = 'business_id');
            """;

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var remaining = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task T05_every_wave_can_be_reverted_and_reapplied()
    {
        // The schema the waves started from: the migration before wave 1.
        const string beforeWaves = "20261020083815_AddInventoryValuation";

        await using var database = await _fixture.CreateUpgradeDatabaseAsync(beforeWaves);
        await database.MigrateToLatestAsync();

        Assert.True(await ForeignKeyExistsAsync(database, "FK_payments_sales_sale_id_business_id"));

        // Six Down methods run in reverse order: the database returns to the previous schema,
        // with the single-column key and without the composite one.
        await database.MigrateToAsync(beforeWaves);

        Assert.True(await ForeignKeyExistsAsync(database, "FK_payments_sales_sale_id"));
        Assert.False(await ForeignKeyExistsAsync(database, "FK_payments_sales_sale_id_business_id"));

        // And the six Up methods run again without error.
        await database.MigrateToLatestAsync();

        Assert.True(await ForeignKeyExistsAsync(database, "FK_payments_sales_sale_id_business_id"));
        Assert.False(await ForeignKeyExistsAsync(database, "FK_payments_sales_sale_id"));
    }

    private static Task<bool> ForeignKeyExistsAsync(UpgradeDatabase database, string constraintName)
        => database.ScalarAsync<bool>(
            $"select exists (select 1 from pg_constraint where conname = '{constraintName}');");

    // ------------------------------------------------------------------ helpers

    private async Task AssertCrossTenantInsertRefusedAsync(IReadOnlyList<string> statements)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Everything except the probe: the rows the probe points at, created in the same
            // transaction so both sides exist when the foreign key is checked.
            foreach (var sql in statements.Take(statements.Count - 1))
            {
                await using var setup = connection.CreateCommand();
                setup.Transaction = transaction;
                setup.CommandText = sql;
                await setup.ExecuteNonQueryAsync();
            }

            var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var probe = connection.CreateCommand();
                probe.Transaction = transaction;
                probe.CommandText = statements[^1];
                await probe.ExecuteNonQueryAsync();
            });

            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);

            await transaction.RollbackAsync();
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
    }

    private static string SaleSql(
        Guid id,
        FixtureBusiness business,
        string number,
        DateTimeOffset now,
        Guid? customerId = null)
        => $"""
           insert into sales (id, location_id, sale_number, status, currency, subtotal_amount,
               discount_amount, tax_amount, total_amount, payment_status, posted_at_utc,
               created_at_utc, updated_at_utc, business_id, version, customer_id)
           values ('{id}', '{business.LocationId}', '{number}', 'Posted', 'QAR', 10,
               0, 0, 10, 'Pending', '{now:O}', '{now:O}', '{now:O}', '{business.BusinessId}', 0,
               {(customerId is null ? "null" : $"'{customerId}'")});
           """;

    private static string JournalEntrySql(
        Guid id,
        FixtureBusiness business,
        string number,
        Guid? reversalOfEntryId,
        DateTimeOffset now)
        => $"""
           insert into journal_entries (id, entry_number, entry_date, source_type, status, currency,
               total_debit, total_credit, created_at_utc, updated_at_utc, business_id, version,
               reversal_of_entry_id)
           values ('{id}', '{number}', current_date, 'Manual', 'Posted', 'QAR', 0, 0,
               '{now:O}', '{now:O}', '{business.BusinessId}', 0,
               {(reversalOfEntryId is null ? "null" : $"'{reversalOfEntryId}'")});
           """;

    private static string CategorySql(
        Guid id,
        FixtureBusiness business,
        string name,
        Guid? parentId,
        DateTimeOffset now)
        => $"""
           insert into categories (id, name, is_active, created_at_utc, updated_at_utc,
               business_id, version, parent_id)
           values ('{id}', '{name}', true, '{now:O}', '{now:O}', '{business.BusinessId}', 0,
               {(parentId is null ? "null" : $"'{parentId}'")});
           """;

    private static string AccountSql(
        Guid id,
        FixtureBusiness business,
        string code,
        Guid? parentId,
        DateTimeOffset now)
        => $"""
           insert into accounts (id, code, name, account_type, is_active, created_at_utc,
               updated_at_utc, business_id, version, parent_id)
           values ('{id}', '{code}', 'Cross account', 'Asset', true, '{now:O}', '{now:O}',
               '{business.BusinessId}', 0, {(parentId is null ? "null" : $"'{parentId}'")});
           """;
}
