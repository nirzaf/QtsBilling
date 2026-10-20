using System.Text.Json;
using QtsBilling.DataImport;
using Xunit;

namespace TestQtsBilling.DataImport;

/// <summary>
/// The native onboarding package (specification task 18, decision D13). These tests are deliberately
/// file-only: validation must never need a database, a source server or a provider decoder.
/// </summary>
public sealed class NativePackageTests
{
    [Fact]
    public async Task T18_01_missing_altered_and_undeclared_files_are_refused()
    {
        using var builder = NativeTestPackage.Valid();
        builder.Build();

        var reader = new NativePackageReader();
        var valid = await reader.ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(valid.IsValid);

        File.Delete(Path.Combine(builder.Root, "customers.ndjson"));
        var missing = await reader.ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.False(missing.IsValid);
        Assert.Contains("RESOURCE_MISSING", missing.Report.Rejections.Select(rejection => rejection.Reason));

        using var alteredBytes = NativeTestPackage.Valid();
        alteredBytes.Build();
        File.AppendAllText(Path.Combine(alteredBytes.Root, "customers.ndjson"), "\n");
        var wrongBytes = await reader.ValidateAsync(alteredBytes.Root, NativeTestData.Trust());
        Assert.False(wrongBytes.IsValid);
        Assert.Contains(
            wrongBytes.Report.Rejections.Select(rejection => rejection.Reason),
            reason => reason is "RESOURCE_LENGTH_MISMATCH" or "RESOURCE_HASH_MISMATCH" or "RESOURCE_COUNT_MISMATCH");

        using var wrongCount = NativeTestPackage.Valid();
        wrongCount.Build();
        wrongCount.Resign(manifest => manifest with
        {
            Resources = manifest.Resources
                .Select((resource, index) => index == 0 ? resource with { Count = resource.Count + 1 } : resource)
                .ToList(),
        });
        var counted = await reader.ValidateAsync(wrongCount.Root, NativeTestData.Trust());
        Assert.False(counted.IsValid);
        Assert.Contains("RESOURCE_COUNT_MISMATCH", counted.Report.Rejections.Select(rejection => rejection.Reason));

        using var wrongLength = NativeTestPackage.Valid();
        wrongLength.Build();
        wrongLength.Resign(manifest => manifest with
        {
            Resources = manifest.Resources
                .Select((resource, index) => index == 0 ? resource with { ByteLength = resource.ByteLength + 1 } : resource)
                .ToList(),
        });
        var length = await reader.ValidateAsync(wrongLength.Root, NativeTestData.Trust());
        Assert.False(length.IsValid);
        Assert.Contains("RESOURCE_LENGTH_MISMATCH", length.Report.Rejections.Select(rejection => rejection.Reason));

        using var wrongHash = NativeTestPackage.Valid();
        wrongHash.Build();
        wrongHash.Resign(manifest => manifest with
        {
            Resources = manifest.Resources
                .Select((resource, index) => index == 0 ? resource with { Sha256 = new string('a', 64) } : resource)
                .ToList(),
        });
        var hash = await reader.ValidateAsync(wrongHash.Root, NativeTestData.Trust());
        Assert.False(hash.IsValid);
        Assert.Contains("RESOURCE_HASH_MISMATCH", hash.Report.Rejections.Select(rejection => rejection.Reason));

        using var undeclared = NativeTestPackage.Valid();
        undeclared.Build();
        await File.WriteAllTextAsync(Path.Combine(undeclared.Root, "unexpected.ndjson"), "{}\n");
        var extra = await reader.ValidateAsync(undeclared.Root, NativeTestData.Trust());
        Assert.False(extra.IsValid);
        Assert.Contains("RESOURCE_UNDECLARED", extra.Report.Rejections.Select(rejection => rejection.Reason));

        Assert.Null(missing.Package);
        Assert.Null(counted.Package);
    }

    [Fact]
    public async Task T18_02_traversal_absolute_case_colliding_and_linked_paths_are_refused()
    {
        var reader = new NativePackageReader();

        using var traversal = NativeTestPackage.Valid();
        traversal.Build();
        traversal.Resign(manifest => ReplaceCustomerPath(manifest, "../customers.ndjson"));
        var traversalResult = await reader.ValidateAsync(traversal.Root, NativeTestData.Trust());
        Assert.False(traversalResult.IsValid);
        Assert.Contains("RESOURCE_PATH_UNSAFE", traversalResult.Report.Rejections.Select(rejection => rejection.Reason));

        using var absolute = NativeTestPackage.Valid();
        absolute.Build();
        absolute.Resign(manifest => ReplaceCustomerPath(manifest, Path.Combine(absolute.Root, "customers.ndjson")));
        var absoluteResult = await reader.ValidateAsync(absolute.Root, NativeTestData.Trust());
        Assert.False(absoluteResult.IsValid);
        Assert.Contains("RESOURCE_PATH_UNSAFE", absoluteResult.Report.Rejections.Select(rejection => rejection.Reason));

        using var collision = NativeTestPackage.Valid();
        collision.Build();
        collision.Resign(manifest => manifest with
        {
            Resources = manifest.Resources
                .Select(resource => resource.Resource == NativeResources.Suppliers
                    ? resource with { Path = "Customers.ndjson" }
                    : resource)
                .ToList(),
        });
        var collisionResult = await reader.ValidateAsync(collision.Root, NativeTestData.Trust());
        Assert.False(collisionResult.IsValid);
        Assert.Contains("RESOURCE_PATH_DUPLICATE", collisionResult.Report.Rejections.Select(rejection => rejection.Reason));

        // A directory junction/symlink is a path escape even when the leaf file looks ordinary.
        using var linked = NativeTestPackage.CustomersOnly();
        linked.Build();
        var outside = Path.Combine(linked.Container, "outside");
        Directory.CreateDirectory(outside);
        File.Move(Path.Combine(linked.Root, "customers.ndjson"), Path.Combine(outside, "customers.ndjson"));
        var link = Path.Combine(linked.Root, "linked");
        Assert.True(NativeTestData.TryCreateDirectoryLink(link, outside), "The test environment could not create a directory link; the escape path is not exercised.");
        linked.Resign(manifest => ReplaceCustomerPath(manifest, "linked/customers.ndjson"));
        var linkedResult = await reader.ValidateAsync(linked.Root, NativeTestData.Trust());
        Assert.False(linkedResult.IsValid);
        Assert.Contains(
            linkedResult.Report.Rejections.Select(rejection => rejection.Reason),
            reason => reason is "RESOURCE_PATH_UNSAFE" or "RESOURCE_LINK");
    }

    [Fact]
    public async Task T18_03_nested_credentials_and_card_fields_never_reach_the_report()
    {
        using var customer = NativeTestPackage.CustomersOnly();
        customer.AddRaw(
            NativeResources.Customers,
            """{"externalId":"cust-secret","name":"Secret Customer","password":"TopSecretValue-9"}""");
        customer.Build();

        var reader = new NativePackageReader();
        var customerResult = await reader.ValidateAsync(customer.Root, NativeTestData.Trust());
        Assert.False(customerResult.IsValid);
        Assert.Contains("PROHIBITED_FIELD", customerResult.Report.Rejections.Select(rejection => rejection.Reason));

        var customerJson = JsonSerializer.Serialize(customerResult.Report);
        Assert.DoesNotContain("TopSecretValue-9", customerJson, StringComparison.Ordinal);
        Assert.Null(customerResult.Package);

        using var opening = NativeTestPackage.OpeningOnly();
        opening.Build();
        opening.AddRaw(
            NativeResources.Openings,
            """
            {"externalId":"open-secret","entryDate":"2026-10-20","currency":"QAR","inventoryAccountCode":"1200","lines":[{"accountCode":"1200","debit":1,"credit":0,"cvv":"cvv-secret-marker-87654"},{"accountCode":"4000","debit":0,"credit":1}],"stock":[]}
            """);
        opening.Rebuild();

        var openingResult = await reader.ValidateAsync(opening.Root, NativeTestData.Trust());
        Assert.False(openingResult.IsValid);
        Assert.Contains("PROHIBITED_FIELD", openingResult.Report.Rejections.Select(rejection => rejection.Reason));

        var openingJson = JsonSerializer.Serialize(openingResult.Report);
        Assert.DoesNotContain("cvv-secret-marker-87654", openingJson, StringComparison.Ordinal);
        Assert.Null(openingResult.Package);
    }

    [Fact]
    public async Task T18_04_mutation_between_validation_and_apply_blocks_the_digest()
    {
        using var builder = NativeTestPackage.Valid();
        builder.Build();

        var reader = new NativePackageReader();
        var validation = await reader.ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(validation.IsValid);
        Assert.NotNull(validation.Package);

        var customerPath = Path.Combine(builder.Root, "customers.ndjson");
        await File.AppendAllTextAsync(customerPath, " ");

        var recheck = await reader.ReverifyAsync(validation.Package!);
        Assert.False(recheck.Ok);
        Assert.Contains("RESOURCE_CHANGED", recheck.Rejections.Select(rejection => rejection.Reason));

        // The same check must notice a manifest rewrite, not just a resource rewrite.
        var changed = await reader.ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.False(changed.IsValid);
    }

    [Fact]
    public async Task T18_05_preview_identifies_the_exact_resources_and_target_business()
    {
        using var builder = NativeTestPackage.Valid();
        builder.Build();

        var reader = new NativePackageReader();
        var validation = await reader.ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(validation.IsValid);

        var preview = reader.Preview(validation.Package!);

        Assert.Equal(builder.BusinessId, preview.TargetBusinessId);
        Assert.Equal(builder.BatchId, preview.BatchId);
        Assert.Equal(builder.EffectiveDate, preview.EffectiveBusinessDate);
        Assert.Equal(NativeTestData.ApprovalKeyId, preview.ApprovalKeyId);
        Assert.Equal(
            [
                NativeResources.Categories,
                NativeResources.Customers,
                NativeResources.Openings,
                NativeResources.Products,
                NativeResources.Suppliers,
                NativeResources.Variants,
            ],
            preview.Resources.Select(resource => resource.Resource).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(6, preview.TotalRows);
    }

    private static NativeManifest ReplaceCustomerPath(NativeManifest manifest, string path) =>
        manifest with
        {
            Resources = manifest.Resources
                .Select(resource => resource.Resource == NativeResources.Customers
                    ? resource with { Path = path }
                    : resource)
                .ToList(),
        };
}
