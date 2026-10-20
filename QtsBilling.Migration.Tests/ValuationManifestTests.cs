using System.Security.Cryptography;
using System.Text.Json;
using QtsBilling.Migration;
using Xunit;

namespace QtsBilling.Migration.Tests;

public sealed class ValuationManifestTests
{
    [Fact]
    public async Task Zero_cost_is_accepted_only_with_finance_signoff_for_each_positive_variant()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qts-valuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var variantPath = Path.Combine(directory, "ProductVariant.ndjson");
            await File.WriteAllLinesAsync(variantPath,
            [
                "{\"_id\":\"variant-zero-cost\",\"OpeningQuantity\":4}",
                "{\"_id\":\"variant-no-stock\",\"OpeningQuantity\":0}",
            ]);
            await WriteSnapshotManifestAsync(directory, variantPath);

            var approvalPath = Path.Combine(directory, "valuation.csv");
            await File.WriteAllTextAsync(approvalPath,
                $"VariantId,ApprovedUnitCost,FinanceApprovedBy,ApprovedAtUtc\r\n\"variant-zero-cost\",0,\"Finance, Controller\",\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"\r\n");

            var result = await ValuationManifestValidator.ValidateAsync(directory, approvalPath);

            Assert.True(result.Passed, string.Join(Environment.NewLine, result.Problems));
            Assert.Equal(1, result.RequiredVariantCount);
            Assert.Equal(1, result.ApprovedVariantCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_or_negative_approval_blocks_cutover_validation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qts-valuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var variantPath = Path.Combine(directory, "ProductVariant.ndjson");
            await File.WriteAllTextAsync(variantPath, "{\"_id\":\"variant-1\",\"OpeningQuantity\":1}\r\n");
            await WriteSnapshotManifestAsync(directory, variantPath);
            var approvalPath = Path.Combine(directory, "valuation.csv");
            await File.WriteAllTextAsync(approvalPath,
                $"VariantId,ApprovedUnitCost,FinanceApprovedBy,ApprovedAtUtc\r\nvariant-1,-0.01,Finance,\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"\r\n");

            var result = await ValuationManifestValidator.ValidateAsync(directory, approvalPath);

            Assert.False(result.Passed);
            Assert.Contains(result.Problems, problem => problem.Contains("ApprovedUnitCost", StringComparison.Ordinal));
            Assert.Contains(result.Problems, problem => problem.Contains("no valid finance-approved unit cost", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteSnapshotManifestAsync(string directory, string dataPath)
    {
        var bytes = await File.ReadAllBytesAsync(dataPath);
        var manifest = new ExportManifest(
            "valuation-test",
            DateTimeOffset.UtcNow,
            [new ManifestFile("ProductVariant", dataPath, File.ReadAllLines(dataPath).Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
    }
}
