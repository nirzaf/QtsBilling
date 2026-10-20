using System.Globalization;
using System.Text.Json.Nodes;
using System.Text;

namespace QtsBilling.Migration;

public sealed record ValuationManifestValidationResult(
    int RequiredVariantCount,
    int ApprovedVariantCount,
    IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}

/// <summary>Checks that every positively valued opening-stock variant has an explicit finance approval.</summary>
public static class ValuationManifestValidator
{
    private static readonly string[] QuantityFields =
    [
        "OpeningQuantity",
        "OpeningStock",
        "OpeningOnHand",
        "QuantityOnHand",
        "OnHand",
        "StockOnHand",
        "CurrentStock",
    ];

    public static async Task<ValuationManifestValidationResult> ValidateAsync(
        string inputDirectory,
        string approvalCsvPath,
        CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var source = new SnapshotSource(inputDirectory);
        var snapshotProblems = await source.VerifyAsync(cancellationToken).ConfigureAwait(false);
        problems.AddRange(snapshotProblems.Select(problem => $"snapshot problem: {problem}"));
        if (problems.Count > 0)
        {
            return new ValuationManifestValidationResult(0, 0, problems);
        }

        var manifest = source.ReadManifest();
        var stockCollectionFiles = manifest.Files
            .Where(file => IsInventoryCollection(file.Collection))
            .ToArray();
        var variantFiles = manifest.Files
            .Where(file => IsVariantCollection(file.Collection))
            .ToArray();
        var requirements = new HashSet<string>(StringComparer.Ordinal);
        var foundQuantityField = false;

        // Prefer a stock/inventory collection, which can represent multiple locations. ProductVariant
        // opening fields are the supported fallback for exports without a separate stock collection.
        var sourceFiles = stockCollectionFiles.Length > 0 ? stockCollectionFiles : variantFiles;
        if (sourceFiles.Length == 0)
        {
            problems.Add("the snapshot has no ProductVariant, inventory, or stock collection to inspect");
        }

        foreach (var file in sourceFiles)
        {
            var documents = await SnapshotSource.ReadDocumentsAsync(file.Path, cancellationToken).ConfigureAwait(false);
            var isInventoryCollection = IsInventoryCollection(file.Collection);
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decimal quantity;
                try
                {
                    if (!TryReadQuantity(document, isInventoryCollection, out quantity))
                    {
                        continue;
                    }
                }
                catch (FormatException exception)
                {
                    problems.Add($"{file.Collection}: {exception.Message}");
                    continue;
                }

                foundQuantityField = true;
                var variantId = FindText(document, isInventoryCollection
                    ? ["VariantId", "ProductVariantId", "variant_id"]
                    : ["_id", "Id", "VariantId"]);
                if (string.IsNullOrWhiteSpace(variantId))
                {
                    problems.Add($"{file.Collection}: an opening quantity row has no variant identifier");
                    continue;
                }

                if (quantity > 0m)
                {
                    requirements.Add(variantId.Trim());
                }
            }
        }

        if (sourceFiles.Length > 0 && !foundQuantityField)
        {
            problems.Add("no recognized opening quantity field was found; export OpeningQuantity, QuantityOnHand, or OnHand values");
        }

        if (!File.Exists(approvalCsvPath))
        {
            problems.Add($"finance valuation manifest not found: {approvalCsvPath}");
            return new ValuationManifestValidationResult(requirements.Count, 0, problems);
        }

        IReadOnlyList<string[]> rows;
        try
        {
            rows = ParseCsv(await File.ReadAllTextAsync(approvalCsvPath, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException exception)
        {
            problems.Add($"finance valuation manifest is invalid: {exception.Message}");
            return new ValuationManifestValidationResult(requirements.Count, 0, problems);
        }

        if (rows.Count == 0)
        {
            problems.Add("finance valuation manifest is empty");
            return new ValuationManifestValidationResult(requirements.Count, 0, problems);
        }

        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in rows[0].Select((value, index) => (value.Trim(), index)))
        {
            if (!header.TryAdd(name, index))
            {
                problems.Add($"duplicate CSV header '{name}'");
            }
        }

        if (problems.Count > 0)
        {
            return new ValuationManifestValidationResult(requirements.Count, 0, problems);
        }
        var idColumn = HeaderIndex(header, "VariantId", "SourceVariantId");
        var costColumn = HeaderIndex(header, "ApprovedUnitCost");
        var approverColumn = HeaderIndex(header, "FinanceApprovedBy", "ApprovedBy");
        var approvedAtColumn = HeaderIndex(header, "ApprovedAtUtc", "FinanceApprovedAtUtc");
        if (idColumn < 0 || costColumn < 0 || approverColumn < 0 || approvedAtColumn < 0)
        {
            problems.Add("CSV headers must include VariantId, ApprovedUnitCost, FinanceApprovedBy, and ApprovedAtUtc");
            return new ValuationManifestValidationResult(requirements.Count, 0, problems);
        }

        var approvals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var seenApprovals = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var sourceVariantId = Cell(row, idColumn).Trim();
            if (sourceVariantId.Length == 0)
            {
                problems.Add("an approval row has an empty VariantId");
                continue;
            }

            if (!seenApprovals.Add(sourceVariantId))
            {
                problems.Add($"duplicate finance approval for variant '{sourceVariantId}'");
                continue;
            }

            if (!requirements.Contains(sourceVariantId))
            {
                problems.Add($"finance approval references variant '{sourceVariantId}', which has no positive opening quantity");
                continue;
            }

            if (!decimal.TryParse(Cell(row, costColumn), NumberStyles.Number, CultureInfo.InvariantCulture, out var unitCost)
                || unitCost < 0m
                || decimal.Round(unitCost, 4, MidpointRounding.AwayFromZero) != unitCost)
            {
                problems.Add($"variant '{sourceVariantId}' must have an ApprovedUnitCost >= 0 with at most 4 decimal places");
                continue;
            }

            if (string.IsNullOrWhiteSpace(Cell(row, approverColumn)))
            {
                problems.Add($"variant '{sourceVariantId}' has no finance approver");
                continue;
            }

            if (!DateTimeOffset.TryParse(Cell(row, approvedAtColumn), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var approvedAt)
                || approvedAt > now)
            {
                problems.Add($"variant '{sourceVariantId}' must have a valid, non-future ApprovedAtUtc sign-off time");
                continue;
            }

            approvals[sourceVariantId] = unitCost;
        }

        foreach (var missing in requirements.Where(id => !approvals.ContainsKey(id)).Order(StringComparer.Ordinal))
        {
            problems.Add($"variant '{missing}' has positive opening quantity but no valid finance-approved unit cost");
        }

        return new ValuationManifestValidationResult(requirements.Count, approvals.Count, problems);
    }

    private static bool IsInventoryCollection(string name)
        => name.Contains("inventory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("stock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("openingbalance", StringComparison.OrdinalIgnoreCase);

    private static bool IsVariantCollection(string name)
        => name.Contains("variant", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadQuantity(JsonObject row, bool allowGenericQuantity, out decimal quantity)
    {
        var names = allowGenericQuantity ? QuantityFields.Append("Quantity").Append("Stock") : QuantityFields;
        foreach (var name in names)
        {
            var value = row.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is null)
            {
                continue;
            }

            if (decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out quantity))
            {
                return true;
            }

            quantity = 0m;
            throw new FormatException($"'{name}' contains a non-numeric opening quantity");
        }

        quantity = 0m;
        return false;
    }

    private static string? FindText(JsonObject row, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var value = row.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static int HeaderIndex(IReadOnlyDictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
        {
            if (header.TryGetValue(name, out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] : string.Empty;

    private static IReadOnlyList<string[]> ParseCsv(string csv)
    {
        csv = csv.TrimStart('\uFEFF');
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (inQuotes)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                rows.Add(fields.ToArray());
                fields.Clear();
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new FormatException("a quoted field was not closed");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }
}
