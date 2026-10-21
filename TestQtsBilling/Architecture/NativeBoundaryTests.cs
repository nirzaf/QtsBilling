using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QtsBilling.DataImport;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Persistence.Sqlite;
using QtsBilling.Persistence.Sqlite.Entities;
using QtsBilling.Ui.Pages;
using QtsBilling.Ui.Smoke;
using Xunit;

namespace TestQtsBilling.Architecture;

/// <summary>
/// Task 02 boundary tests. They inspect the candidate tree, the positive project graph, the native
/// package contract and the device-local/native value stores. No production service is contacted.
/// </summary>
public sealed class NativeBoundaryTests
{
    // Assembly the retired source identity/provider terms at runtime so the checker itself never
    // contains the contiguous names it forbids (Task 02 recipe D.4).
    private static readonly string[] RetiredTerms =
    [
        "mon" + "go",
        "mon" + "godb",
        "object" + "id",
        "bs" + "on",
        "qts" + "services",
        "qtsbilling." + "migration",
        "qb39-cutover-" + "safety",
    ];

    // The implementation specification is an input planning document, not part of the supported
    // product/source tree. It necessarily describes the retired surface it asks us to remove.
    private const string ImplementationSpecification = "docs/QtsBilling_Atomic_Implementation_Spec.md";

    private static readonly string[] BinaryExtensions =
    [
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".woff",
        ".woff2", ".ttf", ".eot", ".zip", ".gz", ".nupkg", ".snupkg", ".dump", ".bak",
        ".pfx", ".p12", ".sqlite", ".db", ".pdf",
    ];

    [Fact]
    public void T02_01_the_tracked_tree_and_project_graph_contain_no_retired_surface()
    {
        var root = FindRepositoryRoot();
        var files = TrackedFiles(root).ToArray();

        var hits = new List<string>();

        foreach (var relativePath in files)
        {
            if (relativePath.Equals(ImplementationSpecification, StringComparison.OrdinalIgnoreCase)
                || IsBinary(relativePath))
            {
                continue;
            }

            if (RetiredTerms.Any(term => relativePath.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add($"{relativePath}: path");
            }

            var fullPath = Path.Combine(root, relativePath);
            var bytes = File.ReadAllBytes(fullPath);

            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);

            foreach (var term in RetiredTerms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{relativePath}: {term}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Retired provider/platform surface found: " + string.Join("; ", hits));

        AssertPositiveProjectGraph(root);

        foreach (var project in files.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(Path.Combine(root, project));
            var retiredPackages = document
                .Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
                .Where(package => RetiredTerms.Any(term =>
                    package.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            Assert.True(
                retiredPackages.Length == 0,
                $"{project} still references a retired package: {string.Join(", ", retiredPackages)}");
        }
    }

    [Fact]
    public async Task T02_02_a_source_era_manifest_is_rejected_as_unsupported_not_coerced()
    {
        var root = Path.Combine(Path.GetTempPath(), "qts-boundary-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var manifest = new
            {
                schemaVersion = 0,
                mappingVersion = 1,
                targetSchema = "source-era-v1",
                batchId = Guid.NewGuid(),
                targetBusinessId = Guid.NewGuid(),
                effectiveBusinessDate = "2026-10-21",
                capturedAtUtc = "2026-10-21T00:00:00Z",
                resources = new[]
                {
                    new
                    {
                        resource = NativeResources.Customers,
                        path = "customers.ndjson",
                        count = 0,
                        byteLength = 0,
                        sha256 = new string('0', 64),
                    },
                },
                approval = (object?)null,
            };

            var manifestJson = JsonSerializer.Serialize(manifest);
            await File.WriteAllTextAsync(
                Path.Combine(root, "manifest.json"),
                manifestJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var reader = new NativePackageReader();
            var result = await reader.ValidateAsync(root, new Dictionary<string, byte[]>());

            Assert.False(result.Report.IsValid);
            Assert.Contains(result.Report.Rejections, rejection => rejection.Reason == "MANIFEST_SCHEMA_UNSUPPORTED");
            Assert.Contains(result.Report.Rejections, rejection => rejection.Reason == "MANIFEST_TARGET_SCHEMA_UNSUPPORTED");
            Assert.DoesNotContain(result.Report.Rejections, rejection => rejection.Reason == "RESOURCE_HASH_MISMATCH");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task T02_03_a_posted_opening_and_unresolved_local_cash_remain_value_identical()
    {
        // The current tree is already the post-cleanup candidate: the source-format project and its
        // imports are gone. Verify that first, then query the same CLR values through native stores.
        Assert.Empty(FindRetiredHits(FindRepositoryRoot()));

        var opening = new OpenItem
        {
            Id = Guid.NewGuid(),
            BusinessId = Guid.NewGuid(),
            Kind = OpenItemKind.Receivable,
            CustomerId = Guid.NewGuid(),
            SourceType = "native_opening",
            SourceId = Guid.NewGuid(),
            JournalEntryId = Guid.NewGuid(),
            DocumentDate = new DateOnly(2026, 10, 21),
            DueDate = new DateOnly(2026, 11, 20),
            Currency = "QAR",
            OriginalAmount = 1234.5678m,
            Reference = "OPENING-T02",
        };
        var openingSnapshot = OpeningSnapshot(opening);

        var cashOperationId = Guid.NewGuid();
        const decimal unresolvedCash = 987.6543m;

        SQLitePCL.Batteries_V2.Init();
        var openingConnection = new SqliteConnection("Data Source=:memory:");
        await openingConnection.OpenAsync();
        var openingOptions = new DbContextOptionsBuilder<OpeningRecoveryContext>()
            .UseSqlite(openingConnection)
            .Options;

        await using (var openingDb = new OpeningRecoveryContext(openingOptions))
        {
            await openingDb.Database.EnsureCreatedAsync();
            openingDb.OpenItems.Add(opening);
            await openingDb.SaveChangesAsync();

            var recovered = await openingDb.OpenItems.AsNoTracking().SingleAsync(item => item.Id == opening.Id);

            Assert.Equal(openingSnapshot, OpeningSnapshot(recovered));
            Assert.Equal(decimal.GetBits(opening.OriginalAmount), decimal.GetBits(recovered.OriginalAmount));
        }

        await openingConnection.DisposeAsync();

        var cashConnection = new SqliteConnection("Data Source=:memory:");
        await cashConnection.OpenAsync();
        var cashOptions = new DbContextOptionsBuilder<LocalStoreDbContext>()
            .UseSqlite(cashConnection)
            .Options;

        await using (var cashDb = new LocalStoreDbContext(cashOptions))
        {
            await cashDb.Database.EnsureCreatedAsync();
            cashDb.OutboxOperations.Add(new OutboxOperation
            {
                OperationId = cashOperationId,
                DeviceId = Guid.NewGuid(),
                BusinessId = Guid.NewGuid(),
                LocationId = Guid.NewGuid(),
                OperationType = "cash-sale",
                PayloadVersion = 1,
                CanonicalPayload = "{}",
                Sha256Fingerprint = new string('a', 64),
                State = OutboxState.Pending,
                NextAttemptAtUtc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
            });
            cashDb.LocalCashMovements.Add(new LocalCashMovement
            {
                OperationId = cashOperationId,
                RegisterId = Guid.NewGuid(),
                RegisterSessionId = Guid.NewGuid(),
                Amount = unresolvedCash,
                Reason = "unresolved provisional cash",
                State = SaleDraftState.Pending,
            });
            await cashDb.SaveChangesAsync();

            var recovered = await cashDb.LocalCashMovements
                .AsNoTracking()
                .SingleAsync(movement => movement.OperationId == cashOperationId);

            Assert.Equal(decimal.GetBits(unresolvedCash), decimal.GetBits(recovered.Amount));
            Assert.Equal(SaleDraftState.Pending, recovered.State);
            Assert.Equal("unresolved provisional cash", recovered.Reason);
        }

        await cashConnection.DisposeAsync();
    }

    [Fact]
    public void T02_04_contracts_only_ui_is_allowed_and_an_injected_persistence_reference_fails()
    {
        var references = typeof(Customers).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Empty(SharedUiDependencyPolicy.Disallowed(references));
        Assert.True(SharedUiDependencyPolicy.IsAllowed("QtsBilling.Contracts"));
        Assert.True(SharedUiDependencyPolicy.IsAllowed("MudBlazor"));
        Assert.False(SharedUiDependencyPolicy.IsAllowed("Injected.Persistence"));
        Assert.NotEmpty(SharedUiDependencyPolicy.Disallowed([.. references, "Injected.Persistence"]));
        Assert.NotEmpty(SharedUiDependencyPolicy.Disallowed([.. references, "QtsBilling.Persistence.Postgres"]));
    }

    private static void AssertPositiveProjectGraph(string root)
    {
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["QtsModels"] = [],
            ["QtsBilling.Contracts"] = ["QtsModels"],
            ["QtsBilling.Ui"] = ["QtsBilling.Contracts"],
            ["QtsBilling.Ui.Smoke"] = ["QtsBilling.Ui", "QtsBilling.Contracts", "QtsModels"],
            ["QtsBilling.Persistence.Postgres"] = ["QtsBilling.Contracts", "QtsModels"],
            ["QtsBilling.Persistence.Sqlite"] = ["QtsModels", "QtsBilling.Contracts"],
            ["QtsBilling.Persistence.Sqlite.Tests"] = ["QtsBilling.Persistence.Sqlite", "QtsModels", "QtsBilling.Contracts"],
            ["QtsBilling.DataImport"] = ["QtsBilling.Contracts", "QtsModels", "QtsBilling.Persistence.Postgres"],
            ["QtsWebApp.Client"] = ["QtsBilling.Contracts", "QtsBilling.Ui", "QtsModels"],
            ["QtsWebApp"] = ["QtsBilling.Contracts", "QtsModels", "QtsBilling.Persistence.Postgres", "QtsBilling.Ui", "QtsWebApp.Client"],
            ["QtsBilling.Maui"] = ["QtsBilling.Ui", "QtsBilling.Contracts", "QtsBilling.Persistence.Sqlite", "QtsModels"],
            ["TestQtsBilling"] = ["QtsBilling.Contracts", "QtsModels", "QtsWebApp", "QtsBilling.DataImport", "QtsBilling.Persistence.Sqlite", "QtsBilling.Ui.Smoke"],
        };

        foreach (var projectFile in TrackedFiles(root).Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);

            Assert.True(allowed.ContainsKey(projectName), $"Unreviewed project '{projectName}' is outside the approved positive graph.");

            var document = XDocument.Load(Path.Combine(root, projectFile));
            var actualReferences = document
                .Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Replace('\\', '/')))
                .Where(reference => reference.Length > 0)
                .ToArray();

            foreach (var reference in actualReferences)
            {
                Assert.True(
                    allowed[projectName].Contains(reference, StringComparer.Ordinal),
                    $"{projectName} references '{reference}', which is outside its approved dependency set.");
            }

            if (projectName is "QtsWebApp.Client" or "QtsBilling.Ui" or "QtsBilling.Ui.Smoke"
                or "QtsBilling.Persistence.Sqlite" or "QtsBilling.Maui")
            {
                Assert.DoesNotContain("QtsBilling.Persistence.Postgres", actualReferences);
            }
        }
    }

    private static IEnumerable<string> FindRetiredHits(string root)
    {
        foreach (var relativePath in TrackedFiles(root))
        {
            if (relativePath.Equals(ImplementationSpecification, StringComparison.OrdinalIgnoreCase)
                || IsBinary(relativePath))
            {
                continue;
            }

            if (RetiredTerms.Any(term => relativePath.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"{relativePath}: path";
            }

            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));

            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);

            foreach (var term in RetiredTerms.Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"{relativePath}: {term}";
            }
        }
    }

    private static bool IsBinary(string path) =>
        BinaryExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> TrackedFiles(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not start for the boundary inventory.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git ls-files failed: " + stderr);
        }

        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string OpeningSnapshot(OpenItem opening) => string.Join(
        "|",
        opening.Id.ToString("N"),
        opening.BusinessId.ToString("N"),
        opening.Kind,
        opening.SourceType,
        opening.SourceId.ToString("N"),
        opening.JournalEntryId.ToString("N"),
        opening.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        opening.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        opening.Currency,
        opening.OriginalAmount.ToString("0.0000", CultureInfo.InvariantCulture),
        opening.Reference ?? string.Empty);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QtsBilling.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The QtsBilling repository root could not be located.");
    }

    private sealed class OpeningRecoveryContext(DbContextOptions<OpeningRecoveryContext> options)
        : DbContext(options)
    {
        public DbSet<OpenItem> OpenItems => Set<OpenItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var opening = modelBuilder.Entity<OpenItem>();
            opening.ToTable("open_items");
            opening.HasKey(item => item.Id);
            opening.Property(item => item.OriginalAmount).HasPrecision(18, 4);
            opening.Property(item => item.SourceType).HasMaxLength(64);
            opening.Property(item => item.Currency).HasMaxLength(8);
        }
    }
}
