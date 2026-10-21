using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.DataImport;

/// <summary>
/// Builds the task-20 reconciliation from the exact validated package, the immutable source links and
/// the persisted native rows. It never writes and never repairs: every difference is an exception.
/// </summary>
public sealed class NativeReconciliationBuilder
{
    private const string NativeNamespace = "native";
    private const string OpeningSourceType = "native_opening";

    private readonly TimeProvider _clock;

    public NativeReconciliationBuilder(TimeProvider? clock = null) =>
        _clock = clock ?? TimeProvider.System;

    public async Task<ReconciliationReportBody> BuildAsync(
        BillingDbContext db,
        ValidatedPackage package,
        NativeImportResult? importResult = null,
        string? toolSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(package);

        var businessId = package.Manifest.TargetBusinessId;
        var links = await db.ExternalRecordLinks.AsNoTracking()
            .Where(link => link.BusinessId == businessId
                && link.DatasetNamespace == NativeNamespace)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var linksByResource = links
            .GroupBy(link => link.ResourceType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var quarantined = importResult?.Quarantined ?? [];
        var exceptions = new List<ReconciliationException>();
        var resources = new List<ReconciliationResourceComparison>();

        foreach (var resource in NativeResources.All)
        {
            var expectedRows = ExpectedRows(package, resource).ToList();
            var expectedIds = expectedRows.Select(row => row.ExternalId).ToHashSet(StringComparer.Ordinal);
            var resourceLinks = linksByResource.TryGetValue(resource, out var foundLinks)
                ? foundLinks.Where(link => expectedIds.Contains(link.ExternalId)).ToList()
                : [];
            var unexpectedCount = links.Count(link =>
                string.Equals(link.ResourceType, resource, StringComparison.Ordinal)
                && link.BatchId == package.Manifest.BatchId
                && !expectedIds.Contains(link.ExternalId));
            var sourceCount = expectedRows.Count;
            var quarantineCount = quarantined.Count(item =>
                string.Equals(item.Resource, resource, StringComparison.Ordinal));
            var acceptedCount = Math.Max(0, sourceCount - quarantineCount);
            var targets = await LoadTargetsAsync(db, resource, resourceLinks, cancellationToken)
                .ConfigureAwait(false);
            var expectedHash = ReconciliationIdentityHash.Of(expectedRows);
            var actualHash = ReconciliationIdentityHash.Of(
                resourceLinks.Select(link => (link.ExternalId, link.PayloadSha256)));
            var source = package.Resources.TryGetValue(resource, out var snapshot)
                ? snapshot.Sha256
                : string.Empty;

            if (resourceLinks.Count != acceptedCount)
            {
                AddException(
                    exceptions,
                    $"resource-count:{resource}",
                    "resource",
                    "Persisted source links do not equal the accepted package rows.",
                    financial: true,
                    resource);
            }

            if (unexpectedCount > 0)
            {
                AddException(
                    exceptions,
                    $"resource-extra:{resource}",
                    "resource",
                    "The same batch contains links that are not in the approved package.",
                    financial: true,
                    resource);
            }

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                AddException(
                    exceptions,
                    $"resource-identity:{resource}",
                    "identity",
                    "The accepted package identity and its source links differ.",
                    financial: true,
                    resource);
            }

            foreach (var target in targets.Where(target => !target.Exists))
            {
                AddException(
                    exceptions,
                    $"target-missing:{resource}:{target.ExternalId}",
                    "target",
                    "An accepted source identity has no persisted target.",
                    financial: true,
                    target.ExternalId);
            }

            foreach (var target in targets.Where(target => target.Exists && target.BusinessId != businessId))
            {
                AddException(
                    exceptions,
                    $"target-tenant:{resource}:{target.ExternalId}",
                    "target",
                    "A persisted target belongs to another business.",
                    financial: true,
                    target.ExternalId);
            }

            resources.Add(new ReconciliationResourceComparison
            {
                Resource = resource,
                SourceCount = sourceCount,
                AcceptedCount = acceptedCount,
                QuarantinedCount = quarantineCount,
                PersistedCount = resourceLinks.Count,
                UnexpectedCount = unexpectedCount,
                SourceSha256 = source,
                ExpectedIdentitySha256 = expectedHash,
                ActualIdentitySha256 = actualHash,
                ExpectedBusinessId = businessId,
                Targets = targets,
            });
        }

        foreach (var unknown in quarantined
                     .Where(item => !NativeResources.IsKnown(item.Resource))
                     .Select(item => item.Resource)
                     .Distinct(StringComparer.Ordinal))
        {
            AddException(
                exceptions,
                $"quarantine-resource:{unknown}",
                "quarantine",
                "A rejected row names an unknown native resource.",
                financial: true,
                unknown);
        }

        var allLinksPresent = resources.All(resource =>
            resource.PersistedCount == resource.AcceptedCount
            && resource.Targets.Count == resource.AcceptedCount
            && resource.Targets.All(target => target.Exists));
        var importCommitted = importResult?.Committed ?? allLinksPresent;

        if (!importCommitted)
        {
            AddException(
                exceptions,
                "import-not-committed",
                "import",
                "The import is not committed or not every accepted row has a source link.",
                financial: true);
        }

        var openingLinks = linksByResource.TryGetValue(NativeResources.Openings, out var openingPool)
            ? openingPool
            : [];
        var openingEntryIds = openingLinks.Select(link => link.TargetId).Distinct().ToList();
        var entries = openingEntryIds.Count == 0
            ? []
            : await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.BusinessId == businessId && openingEntryIds.Contains(entry.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var entriesById = entries.ToDictionary(entry => entry.Id);
        var journalLines = openingEntryIds.Count == 0
            ? []
            : await db.JournalLines.AsNoTracking()
                .Where(line => line.BusinessId == businessId && openingEntryIds.Contains(line.JournalEntryId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var openItems = openingEntryIds.Count == 0
            ? []
            : await db.OpenItems.AsNoTracking()
                .Where(item => item.BusinessId == businessId
                    && item.SourceType == OpeningSourceType
                    && openingEntryIds.Contains(item.JournalEntryId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var accountByCode = accounts.ToDictionary(account => account.Code, StringComparer.Ordinal);
        var accountById = accounts.ToDictionary(account => account.Id);
        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(location => location.BusinessId == businessId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var locationByCode = locations
            .Where(location => !string.IsNullOrWhiteSpace(location.Code))
            .ToDictionary(location => location.Code!, StringComparer.Ordinal);

        var openingsByExternalId = package.Openings
            .ToDictionary(opening => opening.ExternalId, StringComparer.Ordinal);
        var openingLinkByExternalId = openingLinks
            .Where(link => openingsByExternalId.ContainsKey(link.ExternalId))
            .ToDictionary(link => link.ExternalId, StringComparer.Ordinal);

        foreach (var opening in package.Openings)
        {
            if (!openingLinkByExternalId.ContainsKey(opening.ExternalId))
            {
                continue;
            }

            var entryId = openingLinkByExternalId[opening.ExternalId].TargetId;

            if (!entriesById.TryGetValue(entryId, out var entry))
            {
                AddException(
                    exceptions,
                    $"journal-missing:{opening.ExternalId}",
                    "journal",
                    "The opening journal entry is missing.",
                    financial: true,
                    opening.ExternalId);
                continue;
            }

            if (entry.Status != EntryStatus.Posted)
            {
                AddException(
                    exceptions,
                    $"journal-not-posted:{opening.ExternalId}",
                    "journal",
                    "The opening journal entry is not posted.",
                    financial: true,
                    opening.ExternalId);
            }

            var entryLines = journalLines.Where(line => line.JournalEntryId == entryId).ToList();
            var debit = entryLines.Sum(line => line.DebitAmount);
            var credit = entryLines.Sum(line => line.CreditAmount);

            if (entry.TotalDebit != entry.TotalCredit
                || debit != credit
                || entry.TotalDebit != debit
                || entry.TotalCredit != credit)
            {
                AddException(
                    exceptions,
                    $"journal-unbalanced:{opening.ExternalId}",
                    "journal",
                    "The opening journal entry is not balanced or its header differs from its lines.",
                    financial: true,
                    opening.ExternalId);
            }
        }

        var variantLinks = linksByResource.TryGetValue(NativeResources.Variants, out var variantPool)
            ? variantPool
            : [];
        var variantByExternalId = variantLinks.ToDictionary(link => link.ExternalId, StringComparer.Ordinal);
        var variantIds = variantByExternalId.Values.Select(link => link.TargetId).ToHashSet();
        var locationIds = locations.Select(location => location.Id).ToHashSet();
        var balances = variantIds.Count == 0
            ? []
            : await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == businessId
                    && variantIds.Contains(balance.VariantId)
                    && locationIds.Contains(balance.LocationId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var expectedStock = new Dictionary<(string VariantExternalId, string LocationCode), (decimal Quantity, decimal Value)>();
        var expectedStockByAccount = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var expectedPartyAmounts = new Dictionary<(string Kind, string ExternalId), decimal>();
        var expectedPartyByAccount = new Dictionary<(string Kind, string AccountCode), decimal>();
        var expectedLedger = new Dictionary<string, (decimal Debit, decimal Credit)>();

        foreach (var opening in package.Openings)
        {
            foreach (var stock in opening.Stock)
            {
                var key = (stock.VariantExternalId, stock.LocationCode);
                var existing = expectedStock.TryGetValue(key, out var current)
                    ? current
                    : (Quantity: 0m, Value: 0m);
                var value = Round(stock.Quantity * stock.UnitCost);
                expectedStock[key] = (existing.Quantity + stock.Quantity, existing.Value + value);

                if (!string.IsNullOrWhiteSpace(opening.InventoryAccountCode))
                {
                    expectedStockByAccount[opening.InventoryAccountCode] =
                        expectedStockByAccount.GetValueOrDefault(opening.InventoryAccountCode) + value;
                }
            }

            foreach (var receivable in opening.Receivables)
            {
                var key = ("receivable", receivable.PartyExternalId);
                expectedPartyAmounts[key] = expectedPartyAmounts.GetValueOrDefault(key) + Round(receivable.Amount);

                if (!string.IsNullOrWhiteSpace(opening.ReceivableAccountCode))
                {
                    var accountKey = ("receivable", opening.ReceivableAccountCode);
                    expectedPartyByAccount[accountKey] =
                        expectedPartyByAccount.GetValueOrDefault(accountKey) + Round(receivable.Amount);
                }
            }

            foreach (var payable in opening.Payables)
            {
                var key = ("payable", payable.PartyExternalId);
                expectedPartyAmounts[key] = expectedPartyAmounts.GetValueOrDefault(key) + Round(payable.Amount);

                if (!string.IsNullOrWhiteSpace(opening.PayableAccountCode))
                {
                    var accountKey = ("payable", opening.PayableAccountCode);
                    expectedPartyByAccount[accountKey] =
                        expectedPartyByAccount.GetValueOrDefault(accountKey) + Round(payable.Amount);
                }
            }

            foreach (var line in opening.Lines)
            {
                var existing = expectedLedger.TryGetValue(line.AccountCode, out var current)
                    ? current
                    : (Debit: 0m, Credit: 0m);
                expectedLedger[line.AccountCode] = (existing.Debit + Round(line.Debit), existing.Credit + Round(line.Credit));
            }
        }

        var aggregates = new List<ReconciliationAggregateComparison>();

        foreach (var ((variantExternalId, locationCode), expected) in expectedStock.OrderBy(item => item.Key.VariantExternalId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.LocationCode, StringComparer.Ordinal))
        {
            var variantId = variantByExternalId.TryGetValue(variantExternalId, out var variantLink)
                ? variantLink.TargetId
                : Guid.Empty;
            var locationId = locationByCode.TryGetValue(locationCode, out var location)
                ? location.Id
                : Guid.Empty;
            var actual = balances.FirstOrDefault(balance => balance.VariantId == variantId && balance.LocationId == locationId);
            var actualBusinessId = actual is null ? Guid.Empty : businessId;

            if (variantId == Guid.Empty || locationId == Guid.Empty)
            {
                AddException(
                    exceptions,
                    $"stock-target-missing:{variantExternalId}:{locationCode}",
                    "inventory",
                    "A stock opening references a variant or location with no persisted target.",
                    financial: true,
                    variantExternalId);
            }

            if (actual is null
                || actual.OnHand != expected.Quantity
                || actual.ValueOnHand != expected.Value)
            {
                AddException(
                    exceptions,
                    $"stock-mismatch:{variantExternalId}:{locationCode}",
                    "inventory",
                    "The persisted stock quantity/value differs from the approved opening.",
                    financial: true,
                    variantExternalId);
            }

            aggregates.Add(new ReconciliationAggregateComparison
            {
                Name = $"{variantExternalId}@{locationCode}",
                Kind = "inventory",
                LocationId = locationId == Guid.Empty ? null : locationId,
                ExpectedBusinessId = businessId,
                ActualBusinessId = actualBusinessId,
                ExpectedQuantity = expected.Quantity,
                ActualQuantity = actual?.OnHand ?? 0m,
                ExpectedValue = expected.Value,
                ActualValue = actual?.ValueOnHand ?? 0m,
            });
        }

        var parties = new List<ReconciliationPartyComparison>();
        var customerLinks = linksByResource.TryGetValue(NativeResources.Customers, out var customerPool)
            ? customerPool
            : [];
        var supplierLinks = linksByResource.TryGetValue(NativeResources.Suppliers, out var supplierPool)
            ? supplierPool
            : [];
        var customerByExternalId = customerLinks.ToDictionary(link => link.ExternalId, StringComparer.Ordinal);
        var supplierByExternalId = supplierLinks.ToDictionary(link => link.ExternalId, StringComparer.Ordinal);
        var actualPartyAmounts = openItems
            .GroupBy(item => (Kind: item.Kind == OpenItemKind.Receivable ? "receivable" : "payable",
                PartyId: item.Kind == OpenItemKind.Receivable ? item.CustomerId : item.SupplierId))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.OriginalAmount));

        foreach (var ((kind, externalId), expected) in expectedPartyAmounts
                     .OrderBy(item => item.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.ExternalId, StringComparer.Ordinal))
        {
            var linked = kind == "receivable"
                ? customerByExternalId.TryGetValue(externalId, out var customerLink) ? customerLink : null
                : supplierByExternalId.TryGetValue(externalId, out var supplierLink) ? supplierLink : null;
            var partyId = linked?.TargetId ?? Guid.Empty;
            var actual = actualPartyAmounts.GetValueOrDefault((kind, partyId));

            if (partyId == Guid.Empty)
            {
                AddException(
                    exceptions,
                    $"party-target-missing:{kind}:{externalId}",
                    "party",
                    "An opening party has no persisted native target.",
                    financial: true,
                    externalId);
            }

            if (actual != expected)
            {
                AddException(
                    exceptions,
                    $"party-mismatch:{kind}:{externalId}",
                    "party",
                    "The persisted open item differs from the approved party opening.",
                    financial: true,
                    externalId);
            }

            parties.Add(new ReconciliationPartyComparison
            {
                Kind = kind,
                ExternalId = externalId,
                PartyId = partyId,
                BusinessId = partyId == Guid.Empty ? Guid.Empty : businessId,
                ExpectedAmount = expected,
                ActualAmount = actual,
            });
        }

        var expectedPartyIds = parties.Select(party => party.PartyId).ToHashSet();

        foreach (var unexpected in actualPartyAmounts.Where(item =>
                     item.Key.PartyId is null || !expectedPartyIds.Contains(item.Key.PartyId.Value)))
        {
            // The native open items are already limited to the approved openings, so an open item
            // whose party has no approved source identity is a hard provenance failure.
            AddException(
                exceptions,
                $"party-unexpected:{unexpected.Key.Kind}:{unexpected.Key.PartyId}",
                "party",
                "A native opening open item has no approved source identity.",
                financial: true);
        }

        var actualLedger = journalLines
            .GroupBy(line => line.AccountId)
            .ToDictionary(
                group => group.Key,
                group => (Debit: group.Sum(line => line.DebitAmount), Credit: group.Sum(line => line.CreditAmount)));
        var ledger = new List<ReconciliationLedgerComparison>();

        foreach (var (accountCode, expected) in expectedLedger.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var account = accountByCode.GetValueOrDefault(accountCode);
            var actual = account is not null
                ? actualLedger.GetValueOrDefault(account.Id)
                : (Debit: 0m, Credit: 0m);

            if (account is null)
            {
                AddException(
                    exceptions,
                    $"ledger-account-missing:{accountCode}",
                    "ledger",
                    "An approved opening line references an account with no persisted target.",
                    financial: true,
                    accountCode);
            }

            if (actual.Debit != expected.Debit || actual.Credit != expected.Credit)
            {
                AddException(
                    exceptions,
                    $"ledger-mismatch:{accountCode}",
                    "ledger",
                    "The persisted opening ledger differs from the approved package lines.",
                    financial: true,
                    accountCode);
            }

            ledger.Add(new ReconciliationLedgerComparison
            {
                AccountCode = accountCode,
                AccountId = account?.Id,
                BusinessId = account is null ? Guid.Empty : businessId,
                ExpectedDebit = expected.Debit,
                ExpectedCredit = expected.Credit,
                ActualDebit = actual.Debit,
                ActualCredit = actual.Credit,
            });
        }

        foreach (var actual in actualLedger.Keys)
        {
            if (!accountById.TryGetValue(actual, out var account)
                || expectedLedger.ContainsKey(account.Code))
            {
                continue;
            }

            AddException(
                exceptions,
                $"ledger-unexpected:{account.Code}",
                "ledger",
                "A native opening journal line has no approved source line.",
                financial: true,
                account.Code);
        }

        var controls = new List<ReconciliationControlComparison>();

        foreach (var (accountCode, detail) in expectedStockByAccount.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            controls.Add(Control(
                $"inventory:{accountCode}",
                businessId,
                detail,
                actualLedger,
                accountByCode,
                accountCode,
                asset: true,
                exceptions));
        }

        foreach (var (key, detail) in expectedPartyByAccount.OrderBy(item => item.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.AccountCode, StringComparer.Ordinal))
        {
            controls.Add(Control(
                $"{key.Kind}:{key.AccountCode}",
                businessId,
                detail,
                actualLedger,
                accountByCode,
                key.AccountCode,
                asset: key.Kind == "receivable",
                exceptions));
        }

        var toolHash = string.IsNullOrWhiteSpace(toolSha256) ? CurrentToolSha256() : toolSha256;
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(migration => migration, StringComparer.Ordinal)
            .ToList();

        return new ReconciliationReportBody
        {
            ReportSchema = ReconciliationReportBody.CurrentSchema,
            ToolSha256 = toolHash,
            ManifestSha256 = package.ManifestSha256,
            TargetSchema = package.Manifest.TargetSchema,
            SchemaHead = appliedMigrations.Count == 0 ? null : appliedMigrations[^1],
            BatchId = package.Manifest.BatchId,
            TargetBusinessId = businessId,
            GeneratedAtUtc = _clock.GetUtcNow(),
            ImportCommitted = importCommitted,
            Resources = resources,
            Aggregates = aggregates,
            Parties = parties,
            Ledger = ledger,
            Controls = controls,
            Exceptions = exceptions
                .GroupBy(exception => exception.ExceptionId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(exception => exception.ExceptionId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static ReconciliationControlComparison Control(
        string name,
        Guid businessId,
        decimal detail,
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> actualLedger,
        IReadOnlyDictionary<string, Account> accountByCode,
        string accountCode,
        bool asset,
        List<ReconciliationException> exceptions)
    {
        var account = accountByCode.GetValueOrDefault(accountCode);
        var actual = account is null
            ? (Debit: 0m, Credit: 0m)
            : actualLedger.GetValueOrDefault(account.Id);
        var ledgerAmount = asset ? actual.Debit - actual.Credit : actual.Credit - actual.Debit;

        if (account is null || detail != ledgerAmount)
        {
            AddException(
                exceptions,
                $"control-mismatch:{name}",
                "control",
                "The detailed opening does not equal its general-ledger control account.",
                financial: true,
                accountCode);
        }

        return new ReconciliationControlComparison
        {
            Name = name,
            BusinessId = account is null ? Guid.Empty : businessId,
            DetailAmount = detail,
            LedgerAmount = ledgerAmount,
        };
    }

    private static async Task<List<ReconciliationTargetReference>> LoadTargetsAsync(
        BillingDbContext db,
        string resource,
        IReadOnlyList<ExternalRecordLink> links,
        CancellationToken cancellationToken)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var ids = links.Select(link => link.TargetId).Distinct().ToList();
        var targets = resource switch
        {
            NativeResources.Customers => await db.Customers.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            NativeResources.Suppliers => await db.Suppliers.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            NativeResources.Categories => await db.Categories.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            NativeResources.Products => await db.Products.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            NativeResources.Variants => await db.ProductVariants.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            NativeResources.Openings => await db.JournalEntries.AsNoTracking()
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => new TargetRow(entity.Id, entity.BusinessId))
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            _ => [],
        };
        var byId = targets.ToDictionary(target => target.Id);

        return links
            .Select(link => new ReconciliationTargetReference
            {
                ExternalId = link.ExternalId,
                TargetId = link.TargetId,
                BusinessId = byId.TryGetValue(link.TargetId, out var target) ? target.BusinessId : Guid.Empty,
                Exists = byId.ContainsKey(link.TargetId),
            })
            .OrderBy(target => target.ExternalId, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<(string ExternalId, string Fingerprint)> ExpectedRows(
        ValidatedPackage package,
        string resource)
    {
        var rows = resource switch
        {
            NativeResources.Customers => package.Customers.Select(row => row.ExternalId),
            NativeResources.Suppliers => package.Suppliers.Select(row => row.ExternalId),
            NativeResources.Categories => package.Categories.Select(row => row.ExternalId),
            NativeResources.Products => package.Products.Select(row => row.ExternalId),
            NativeResources.Variants => package.Variants.Select(row => row.ExternalId),
            NativeResources.Openings => package.Openings.Select(row => row.ExternalId),
            _ => [],
        };

        return rows.Select(externalId => (externalId, package.Fingerprint(resource, externalId)));
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static void AddException(
        List<ReconciliationException> exceptions,
        string exceptionId,
        string scope,
        string reason,
        bool financial,
        string? externalId = null)
    {
        if (exceptions.Any(exception => string.Equals(exception.ExceptionId, exceptionId, StringComparison.Ordinal)))
        {
            return;
        }

        exceptions.Add(new ReconciliationException
        {
            ExceptionId = exceptionId,
            Scope = scope,
            Reason = reason,
            Financial = financial,
            ExternalId = externalId,
        });
    }

    private static string CurrentToolSha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;

        if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))).ToLowerInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                typeof(NativeReconciliationBuilder).AssemblyQualifiedName ?? "QtsBilling.DataImport")))
            .ToLowerInvariant();
    }

    private sealed record TargetRow(Guid Id, Guid BusinessId);
}
