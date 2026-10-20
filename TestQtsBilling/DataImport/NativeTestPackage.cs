using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QtsBilling.DataImport;

namespace TestQtsBilling.DataImport;

/// <summary>Deterministic signed package builder used by the file-only and PostgreSQL import tests.</summary>
internal static class NativeTestData
{
    public const string ApprovalKeyId = "test-native-approval";

    public static readonly byte[] ApprovalKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("qts-native-package-tests-only"));

    public static Dictionary<string, byte[]> Trust() =>
        new(StringComparer.Ordinal) { [ApprovalKeyId] = ApprovalKey };

    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is UnauthorizedAccessException or IOException)
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(linkPath);
            start.ArgumentList.Add(targetPath);

            using var process = Process.Start(start);
            process!.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
    }
}

internal sealed class NativeTestPackage : IDisposable
{
    private readonly Dictionary<string, List<string>> _rows = new(StringComparer.Ordinal);

    private NativeTestPackage()
    {
        Container = Path.Combine(Path.GetTempPath(), "qts-native-package", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Container, "package");
        Directory.CreateDirectory(Root);
        Prefix = Guid.NewGuid().ToString("N")[..8];
    }

    public string Container { get; }

    public string Root { get; }

    public string Prefix { get; }

    public Guid BatchId { get; } = Guid.CreateVersion7();

    public Guid BusinessId { get; set; } = Guid.NewGuid();

    public DateOnly EffectiveDate { get; } = new(2026, 10, 20);

    public string CustomerExternalId => $"{Prefix}-cust";

    public string CustomerPhone { get; } = $"+974 5555 {Random.Shared.Next(1000000, 9999999)}";

    public string SupplierExternalId => $"{Prefix}-sup";

    public string CategoryExternalId => $"{Prefix}-cat";

    public string ProductExternalId => $"{Prefix}-prod";

    public string VariantExternalId => $"{Prefix}-var";

    public string OpeningExternalId => $"{Prefix}-open";

    public string LocationCode => $"NATIVE-{Prefix}";

    public string InventoryAccountCode { get; set; } = "1200";

    public string ReceivableAccountCode { get; set; } = "1100";

    public string PayableAccountCode { get; set; } = "2100";

    public string EquityAccountCode { get; set; } = "4000";

    public NativeManifest Manifest { get; private set; } = null!;

    public static NativeTestPackage Valid()
    {
        var package = new NativeTestPackage();

        package.Add(NativeResources.Categories, new
        {
            externalId = package.CategoryExternalId,
            name = $"Beverages {package.Prefix}",
            parentExternalId = (string?)null,
            isActive = true,
        });
        package.Add(NativeResources.Customers, new
        {
            externalId = package.CustomerExternalId,
            name = $"Acme Retail {package.Prefix}",
            email = $"accounts-{package.Prefix}@acme.test",
            phone = package.CustomerPhone,
            isActive = true,
        });
        package.Add(NativeResources.Suppliers, new
        {
            externalId = package.SupplierExternalId,
            name = $"Fresh Produce {package.Prefix}",
            isActive = true,
        });
        package.Add(NativeResources.Products, new
        {
            externalId = package.ProductExternalId,
            name = $"Mineral Water {package.Prefix}",
            sku = $"W-{package.Prefix}",
            categoryExternalId = package.CategoryExternalId,
            taxType = "Exclusive",
            trackStock = true,
            isActive = true,
        });
        package.Add(NativeResources.Variants, new
        {
            externalId = package.VariantExternalId,
            productExternalId = package.ProductExternalId,
            name = "Default",
            sku = $"W-{package.Prefix}-D",
            barcode = $"5901234{package.Prefix}",
            sellingPrice = 10m,
            purchasePrice = 6m,
            isDefault = true,
            isActive = true,
        });
        package.Add(NativeResources.Openings, new
        {
            externalId = package.OpeningExternalId,
            entryDate = "2026-10-20",
            currency = "QAR",
            memo = "Approved native opening",
            inventoryAccountCode = package.InventoryAccountCode,
            receivableAccountCode = package.ReceivableAccountCode,
            payableAccountCode = package.PayableAccountCode,
            lines = new object[]
            {
                new { accountCode = package.InventoryAccountCode, debit = 100m, credit = 0m, description = "Stock opening" },
                new { accountCode = package.ReceivableAccountCode, debit = 50m, credit = 0m, description = "Customer opening" },
                new { accountCode = package.PayableAccountCode, debit = 0m, credit = 30m, description = "Supplier opening" },
                new { accountCode = package.EquityAccountCode, debit = 0m, credit = 120m, description = "Opening equity" },
            },
            stock = new object[]
            {
                new { variantExternalId = package.VariantExternalId, locationCode = package.LocationCode, quantity = 10m, unitCost = 10m },
            },
            receivables = new object[]
            {
                new { partyExternalId = package.CustomerExternalId, amount = 50m, reference = "INV-OPEN-1" },
            },
            payables = new object[]
            {
                new { partyExternalId = package.SupplierExternalId, amount = 30m, reference = "BILL-OPEN-1" },
            },
        });

        return package;
    }

    public static NativeTestPackage CustomersOnly()
    {
        var package = new NativeTestPackage();
        package.Add(NativeResources.Customers, new
        {
            externalId = package.CustomerExternalId,
            name = $"Acme Retail {package.Prefix}",
            phone = package.CustomerPhone,
            isActive = true,
        });
        return package;
    }

    public static NativeTestPackage OpeningOnly()
    {
        var package = new NativeTestPackage();
        package.Add(NativeResources.Openings, new
        {
            externalId = package.OpeningExternalId,
            entryDate = "2026-10-20",
            currency = "QAR",
            inventoryAccountCode = package.InventoryAccountCode,
            lines = new object[]
            {
                new { accountCode = package.InventoryAccountCode, debit = 1m, credit = 0m },
                new { accountCode = package.EquityAccountCode, debit = 0m, credit = 1m },
            },
        });
        return package;
    }

    public void Add(string resource, object row) =>
        AddRaw(resource, JsonSerializer.Serialize(row, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));

    public void Replace(string resource, object row)
    {
        _rows[resource] = [JsonSerializer.Serialize(row, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        })];
        Build();
    }

    public void AddRaw(string resource, string row)
    {
        if (!_rows.TryGetValue(resource, out var rows))
        {
            rows = [];
            _rows[resource] = rows;
        }

        rows.Add(row);
    }

    public void Build()
    {
        var resources = new List<NativeManifestResource>();

        foreach (var (resource, rows) in _rows)
        {
            var path = $"{resource}.ndjson";
            var text = rows.Count == 0 ? string.Empty : string.Join("\n", rows) + "\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(Path.Combine(Root, path), bytes);
            resources.Add(new NativeManifestResource
            {
                Resource = resource,
                Path = path,
                Count = rows.Count,
                ByteLength = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            });
        }

        Manifest = new NativeManifest
        {
            BatchId = BatchId,
            TargetBusinessId = BusinessId,
            EffectiveBusinessDate = EffectiveDate,
            CapturedAtUtc = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
            Resources = resources.OrderBy(resource => resource.Resource, StringComparer.Ordinal).ToList(),
        };

        WriteSignedManifest();
    }

    public void Rebuild() => Build();

    public void Resign(Func<NativeManifest, NativeManifest> mutate)
    {
        Manifest = mutate(Manifest);
        WriteSignedManifest();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Container))
            {
                DeleteTree(Container);
            }
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the test result; the temp directory is disposable.
        }
        catch (UnauthorizedAccessException)
        {
            // A directory link is removed by its target-aware branch; a residual temp path is harmless.
        }
    }

    private static void DeleteTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    private void WriteSignedManifest()
    {
        Manifest = NativePackageSigner.Sign(Manifest, NativeTestData.ApprovalKeyId, NativeTestData.ApprovalKey);
        File.WriteAllText(
            Path.Combine(Root, "manifest.json"),
            JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
