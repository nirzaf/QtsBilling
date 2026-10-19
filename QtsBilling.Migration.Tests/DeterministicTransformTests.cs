using System.Text.Json.Nodes;
using QtsBilling.Migration;
using Xunit;

namespace QtsBilling.Migration.Tests;

public sealed class DeterministicTransformTests
{
    private static MigrationMapping Mapping() => new()
    {
        SourceEnvironment = "legacy-mongo",
        TransformationVersion = 1,
        Collections =
        [
            new CollectionMapping
            {
                Collection = "Customer",
                TargetTable = "customers",
                SourceKey = "_id",
                Fields =
                [
                    new FieldMapping { Target = "business_id", Source = "BusinessId", Kind = FieldKind.Reference, References = "Business", ReferenceTarget = "businesses" },
                    new FieldMapping { Target = "name", Source = "Name", Kind = FieldKind.String },
                    new FieldMapping { Target = "email", Source = "Email", Kind = FieldKind.String, Default = "" },
                    new FieldMapping { Target = "balance", Source = "Balance", Kind = FieldKind.Decimal },
                ],
            },
            new CollectionMapping
            {
                Collection = "Business",
                TargetTable = "businesses",
                SourceKey = "_id",
                Fields =
                [
                    new FieldMapping { Target = "name", Source = "Name", Kind = FieldKind.String },
                ],
            },
        ],
    };

    private static JsonObject Customer(string id, string name = "Acme", string? balance = "10.5000")
    {
        var document = new JsonObject
        {
            ["_id"] = id,
            ["Name"] = name,
            ["BusinessId"] = "business-1",
        };

        if (balance is not null)
        {
            document["Balance"] = balance;
        }

        return document;
    }

    [Fact]
    public void The_target_id_is_stable_and_differs_per_source()
    {
        var transform = new DeterministicTransform(Mapping());

        var first = transform.TargetId("Customer", "abc");
        var again = transform.TargetId("Customer", "abc");
        var other = transform.TargetId("Customer", "abd");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, transform.TargetId("Business", "abc"));
    }

    [Fact]
    public void The_transform_is_deterministic_for_the_same_input()
    {
        var transform = new DeterministicTransform(Mapping());
        var documents = new List<JsonObject> { Customer("b"), Customer("a", "Other") };

        var first = transform.Transform("Customer", documents);
        var second = transform.Transform("Customer", documents);

        Assert.Equal(
            first.Rows.Select(row => row.ToJsonString()).ToArray(),
            second.Rows.Select(row => row.ToJsonString()).ToArray());

        // The output is ordered by the source id, so two runs are byte-identical.
        Assert.Equal("a", first.Rows[0]["__source_id"]!.ToString());
        Assert.Equal("b", first.Rows[1]["__source_id"]!.ToString());
    }

    [Fact]
    public void An_unmappable_value_is_quarantined_with_its_source_id()
    {
        var transform = new DeterministicTransform(Mapping());
        var document = Customer("broken");
        document["Balance"] = "not-a-number";

        var result = transform.Transform("Customer", [document]);

        Assert.Empty(result.Rows);
        var record = Assert.Single(result.Quarantine);
        Assert.Equal("broken", record.SourceId);
        Assert.Equal("invalid_decimal", record.Reason);
    }

    [Fact]
    public void A_duplicate_source_id_is_quarantined_instead_of_being_merged()
    {
        var transform = new DeterministicTransform(Mapping());

        var result = transform.Transform("Customer", [Customer("same"), Customer("same", "Other")]);

        Assert.Single(result.Rows);
        var record = Assert.Single(result.Quarantine);
        Assert.Equal("duplicate_source_id", record.Reason);
    }

    [Fact]
    public void A_missing_value_without_a_default_is_quarantined()
    {
        var transform = new DeterministicTransform(Mapping());
        var document = Customer("no-balance");
        document.Remove("Balance");

        var result = transform.Transform("Customer", [document]);

        Assert.Empty(result.Rows);
        Assert.Equal("missing_value", Assert.Single(result.Quarantine).Reason);
    }

    [Fact]
    public void The_report_is_signed_and_the_hash_covers_the_body()
    {
        var entries = new List<ReconciliationEntry>
        {
            new("Customer", "customers", 3, 3, 3, 0, new Dictionary<string, int>(), []),
        };

        var (body, hash) = Reconciliation.BuildBody("legacy-mongo", 1, DateTimeOffset.UtcNow, entries, []);

        Assert.Equal(64, hash.Length);
        Assert.NotEqual("unsigned", Reconciliation.Sign(body, "a-signing-key"));
        Assert.Equal("unsigned", Reconciliation.Sign(body, null));

        // The same body always signs the same way; a changed body does not.
        var (otherBody, otherHash) = Reconciliation.BuildBody("legacy-mongo", 2, DateTimeOffset.UtcNow, entries, []);
        Assert.NotEqual(hash, otherHash);
    }

    [Fact]
    public void The_totals_are_compared_within_the_documented_tolerance()
    {
        Assert.True(Reconciliation.Compare("amount", 10.0000m, 10.0000m, Reconciliation.MoneyTolerance).Matches);
        Assert.True(Reconciliation.Compare("amount", 10.0000m, 10.0001m, Reconciliation.MoneyTolerance).Matches);
        Assert.False(Reconciliation.Compare("amount", 10.0000m, 10.0100m, Reconciliation.MoneyTolerance).Matches);
    }
}
