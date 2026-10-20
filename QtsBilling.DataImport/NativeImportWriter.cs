using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.DataImport;

/// <summary>
/// Applies one verified native package (specification task 19). Masters and approved openings are written
/// in one caller-owned transaction with an immutable source link per external identity. A repeated body
/// reuses its target; a changed body, a foreign tenant or an unlinked existing target is refused. No
/// posted row is ever updated and no running stock balance is patched from an arbitrary file.
/// </summary>
public sealed class NativeImportWriter
{
    private const string OpeningReferenceType = "native_opening";

    private readonly NativePackageReader _reader;

    public NativeImportWriter(NativePackageReader? reader = null) =>
        _reader = reader ?? new NativePackageReader();

    public async Task<NativeImportResult> ApplyAsync(
        BillingDbContext db,
        ValidatedPackage package,
        NativeImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);

        if (package.Manifest.TargetBusinessId != options.TargetBusinessId)
        {
            return NativeImportResult.FailureResult(
                package.Manifest.BatchId,
                options.TargetBusinessId,
                [new NativeImportQuarantine(string.Empty, 0, null, "BUSINESS_MISMATCH", "targetBusinessId")],
                "BUSINESS_MISMATCH");
        }

        var recheck = await _reader.ReverifyAsync(package, cancellationToken).ConfigureAwait(false);

        if (!recheck.Ok)
        {
            return NativeImportResult.FailureResult(
                package.Manifest.BatchId,
                options.TargetBusinessId,
                recheck.Rejections
                    .Select(rejection => new NativeImportQuarantine(
                        rejection.Resource,
                        rejection.LineNumber,
                        rejection.ExternalId,
                        rejection.Reason,
                        rejection.Field))
                    .ToList(),
                "PACKAGE_CHANGED");
        }

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var run = new ImportRun(db, package, options, cancellationToken);
                var result = await run.ExecuteAsync().ConfigureAwait(false);

                if (options.BeforeCommit is not null)
                {
                    await options.BeforeCommit(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result with { Committed = true };
            }
            catch (NativeImportAbortException abort)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return NativeImportResult.FailureResult(
                    package.Manifest.BatchId,
                    options.TargetBusinessId,
                    abort.Quarantines,
                    abort.Failure);
            }
            catch (OperationCanceledException)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await SafeRollbackAsync(transaction).ConfigureAwait(false);
                return NativeImportResult.FailureResult(
                    package.Manifest.BatchId,
                    options.TargetBusinessId,
                    [new NativeImportQuarantine(string.Empty, 0, null, "DATABASE_OR_UNEXPECTED_FAILURE", null)],
                    "DATABASE_OR_UNEXPECTED_FAILURE");
            }
        }).ConfigureAwait(false);
    }

    private static async Task SafeRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The provider already rolled the transaction back (for example after a connection failure).
        }
    }

    private sealed class ImportRun
    {
        private const string CustomerTarget = "customer";
        private const string SupplierTarget = "supplier";
        private const string CategoryTarget = "category";
        private const string ProductTarget = "product";
        private const string VariantTarget = "variant";
        private const string JournalTarget = "journal_entry";

        private readonly BillingDbContext _db;
        private readonly ValidatedPackage _package;
        private readonly NativeImportOptions _options;
        private readonly CancellationToken _ct;
        private readonly Guid _businessId;
        private readonly Dictionary<(string Resource, string ExternalId), ExternalRecordLink> _links = [];
        private readonly Dictionary<string, Guid> _customerIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _supplierIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _categoryIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _productIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _variantIds = new(StringComparer.Ordinal);
        private readonly List<NativeOpeningEvidence> _openings = [];
        private HashSet<Guid> _customerTargets = [];
        private HashSet<Guid> _supplierTargets = [];
        private HashSet<Guid> _categoryTargets = [];
        private HashSet<Guid> _productTargets = [];
        private HashSet<Guid> _variantTargets = [];
        private HashSet<Guid> _journalTargets = [];
        private Business _business = null!;
        private int _imported;
        private int _reused;

        public ImportRun(
            BillingDbContext db,
            ValidatedPackage package,
            NativeImportOptions options,
            CancellationToken cancellationToken)
        {
            _db = db;
            _package = package;
            _options = options;
            _ct = cancellationToken;
            _businessId = options.TargetBusinessId;
        }

        public async Task<NativeImportResult> ExecuteAsync()
        {
            _business = await _db.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(business => business.Id == _businessId, _ct)
                .ConfigureAwait(false)
                ?? throw Abort(string.Empty, 0, null, "BUSINESS_MISSING", "targetBusinessId");

            await LoadLinksAsync().ConfigureAwait(false);
            await ApplyMastersAsync().ConfigureAwait(false);
            await ApplyOpeningsAsync().ConfigureAwait(false);

            return new NativeImportResult(
                _package.Manifest.BatchId,
                _businessId,
                false,
                _imported,
                _reused,
                [],
                _openings,
                null);
        }

        private async Task LoadLinksAsync()
        {
            var links = await _db.ExternalRecordLinks.AsNoTracking()
                .Where(link => link.BusinessId == _businessId
                    && link.DatasetNamespace == NativeStableIdentity.DatasetNamespace)
                .ToListAsync(_ct)
                .ConfigureAwait(false);

            foreach (var link in links)
            {
                _links[(link.ResourceType, link.ExternalId)] = link;
            }

            _customerTargets = await LoadIdsAsync(
                _db.Customers.Where(customer => customer.BusinessId == _businessId),
                TargetIds(links, NativeResources.Customers)).ConfigureAwait(false);
            _supplierTargets = await LoadIdsAsync(
                _db.Suppliers.Where(supplier => supplier.BusinessId == _businessId),
                TargetIds(links, NativeResources.Suppliers)).ConfigureAwait(false);
            _categoryTargets = await LoadIdsAsync(
                _db.Categories.Where(category => category.BusinessId == _businessId),
                TargetIds(links, NativeResources.Categories)).ConfigureAwait(false);
            _productTargets = await LoadIdsAsync(
                _db.Products.Where(product => product.BusinessId == _businessId),
                TargetIds(links, NativeResources.Products)).ConfigureAwait(false);
            _variantTargets = await LoadIdsAsync(
                _db.ProductVariants.Where(variant => variant.BusinessId == _businessId),
                TargetIds(links, NativeResources.Variants)).ConfigureAwait(false);
            _journalTargets = await LoadIdsAsync(
                _db.JournalEntries.Where(entry => entry.BusinessId == _businessId),
                TargetIds(links, NativeResources.Openings)).ConfigureAwait(false);
        }

        private static HashSet<Guid> TargetIds(
            IEnumerable<ExternalRecordLink> links,
            string resource) =>
            links.Where(link => string.Equals(link.ResourceType, resource, StringComparison.Ordinal))
                .Select(link => link.TargetId)
                .ToHashSet();

        private async Task<HashSet<Guid>> LoadIdsAsync<TEntity>(
            IQueryable<TEntity> query,
            HashSet<Guid> ids)
            where TEntity : class, IEntity
        {
            if (ids.Count == 0)
            {
                return [];
            }

            var found = await query
                .Where(entity => ids.Contains(entity.Id))
                .Select(entity => entity.Id)
                .ToListAsync(_ct)
                .ConfigureAwait(false);

            return found.ToHashSet();
        }

        private async Task ApplyMastersAsync()
        {
            await ApplyCustomersAsync().ConfigureAwait(false);
            await ApplySuppliersAsync().ConfigureAwait(false);
            await ApplyCategoriesAsync().ConfigureAwait(false);
            await ApplyProductsAsync().ConfigureAwait(false);
            await ApplyVariantsAsync().ConfigureAwait(false);
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private async Task ApplyCustomersAsync()
        {
            for (var index = 0; index < _package.Customers.Count; index++)
            {
                var row = _package.Customers[index];
                var line = index + 1;

                if (TryResolveLink(NativeResources.Customers, row.ExternalId, CustomerTarget, _customerTargets, out var targetId))
                {
                    _customerIds[row.ExternalId] = targetId;
                    _reused++;
                    continue;
                }

                var id = NativeStableIdentity.Derive(_businessId, "customer", row.ExternalId);

                if (await _db.Customers.AnyAsync(customer => customer.Id == id, _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Customers, line, row.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                var normalized = row.Phone is null ? null : NativePackageReader.NormalizePhone(row.Phone);

                if (!string.IsNullOrEmpty(normalized)
                    && await _db.Customers.AnyAsync(
                        customer => customer.BusinessId == _businessId && customer.PhoneNormalized == normalized,
                        _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Customers, line, row.ExternalId, "NATURAL_KEY_CONFLICT", "phone");
                }

                _db.Customers.Add(new Customer
                {
                    Id = id,
                    BusinessId = _businessId,
                    Name = row.Name,
                    Email = row.Email,
                    Phone = row.Phone,
                    PhoneNormalized = string.IsNullOrEmpty(normalized) ? null : normalized,
                    Address = row.Address,
                    IsActive = row.IsActive,
                    Version = 0,
                });

                AddLink(NativeResources.Customers, row.ExternalId, CustomerTarget, id);
                _customerIds[row.ExternalId] = id;
                _imported++;
            }
        }

        private async Task ApplySuppliersAsync()
        {
            for (var index = 0; index < _package.Suppliers.Count; index++)
            {
                var row = _package.Suppliers[index];
                var line = index + 1;

                if (TryResolveLink(NativeResources.Suppliers, row.ExternalId, SupplierTarget, _supplierTargets, out var targetId))
                {
                    _supplierIds[row.ExternalId] = targetId;
                    _reused++;
                    continue;
                }

                var id = NativeStableIdentity.Derive(_businessId, "supplier", row.ExternalId);

                if (await _db.Suppliers.AnyAsync(supplier => supplier.Id == id, _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Suppliers, line, row.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                _db.Suppliers.Add(new Supplier
                {
                    Id = id,
                    BusinessId = _businessId,
                    Name = row.Name,
                    Email = row.Email,
                    Phone = row.Phone,
                    Address = row.Address,
                    IsActive = row.IsActive,
                    Version = 0,
                });

                AddLink(NativeResources.Suppliers, row.ExternalId, SupplierTarget, id);
                _supplierIds[row.ExternalId] = id;
                _imported++;
            }
        }

        private async Task ApplyCategoriesAsync()
        {
            var parentByExternalId = new Dictionary<string, string?>(StringComparer.Ordinal);

            for (var index = 0; index < _package.Categories.Count; index++)
            {
                var row = _package.Categories[index];
                var line = index + 1;

                if (TryResolveLink(NativeResources.Categories, row.ExternalId, CategoryTarget, _categoryTargets, out var targetId))
                {
                    _categoryIds[row.ExternalId] = targetId;
                    _reused++;
                    continue;
                }

                var id = NativeStableIdentity.Derive(_businessId, "category", row.ExternalId);

                if (await _db.Categories.AnyAsync(category => category.Id == id, _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Categories, line, row.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                if (await _db.Categories.AnyAsync(
                        category => category.BusinessId == _businessId && category.Name == row.Name,
                        _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Categories, line, row.ExternalId, "NATURAL_KEY_CONFLICT", "name");
                }

                _categoryIds[row.ExternalId] = id;
                parentByExternalId[row.ExternalId] = row.ParentExternalId;
                _imported++;
            }

            var depths = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var externalId in parentByExternalId.Keys)
            {
                CalculateDepth(externalId, parentByExternalId, depths, []);
            }

            foreach (var row in _package.Categories
                         .Where(category => parentByExternalId.ContainsKey(category.ExternalId))
                         .OrderBy(category => depths[category.ExternalId]))
            {
                var parentId = await ResolveCategoryParentAsync(row, parentByExternalId).ConfigureAwait(false);

                _db.Categories.Add(new Category
                {
                    Id = _categoryIds[row.ExternalId],
                    BusinessId = _businessId,
                    Name = row.Name,
                    ParentId = parentId,
                    IsActive = row.IsActive,
                    Version = 0,
                });

                AddLink(NativeResources.Categories, row.ExternalId, CategoryTarget, _categoryIds[row.ExternalId]);
            }
        }

        private void CalculateDepth(
            string externalId,
            IReadOnlyDictionary<string, string?> parentByExternalId,
            IDictionary<string, int> depths,
            HashSet<string> visiting)
        {
            if (depths.ContainsKey(externalId))
            {
                return;
            }

            if (!visiting.Add(externalId))
            {
                throw Abort(NativeResources.Categories, 0, externalId, "CATEGORY_CYCLE", "parentExternalId");
            }

            var parent = parentByExternalId.GetValueOrDefault(externalId);

            if (parent is not null && parentByExternalId.ContainsKey(parent))
            {
                CalculateDepth(parent, parentByExternalId, depths, visiting);
                depths[externalId] = depths[parent] + 1;
            }
            else
            {
                depths[externalId] = 0;
            }

            visiting.Remove(externalId);
        }

        private async Task<Guid?> ResolveCategoryParentAsync(
            NativeCategoryRow row,
            IReadOnlyDictionary<string, string?> parentByExternalId)
        {
            if (row.ParentExternalId is null)
            {
                return null;
            }

            if (_categoryIds.TryGetValue(row.ParentExternalId, out var packageParent))
            {
                return packageParent;
            }

            return await ResolveExternalReferenceAsync(
                NativeResources.Categories,
                row.ParentExternalId,
                CategoryTarget,
                _categoryTargets,
                "parentExternalId",
                row.ExternalId).ConfigureAwait(false);
        }

        private async Task ApplyProductsAsync()
        {
            for (var index = 0; index < _package.Products.Count; index++)
            {
                var row = _package.Products[index];
                var line = index + 1;

                if (TryResolveLink(NativeResources.Products, row.ExternalId, ProductTarget, _productTargets, out var targetId))
                {
                    _productIds[row.ExternalId] = targetId;
                    _reused++;
                    continue;
                }

                var id = NativeStableIdentity.Derive(_businessId, "product", row.ExternalId);

                if (await _db.Products.AnyAsync(product => product.Id == id, _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Products, line, row.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                if (!string.IsNullOrWhiteSpace(row.Sku))
                {
                    var sku = row.Sku.Trim().ToUpperInvariant();

                    if (await _db.Products.AnyAsync(
                            product => product.BusinessId == _businessId
                                && product.Sku != null
                                && product.Sku.ToUpper() == sku,
                            _ct).ConfigureAwait(false))
                    {
                        throw Abort(NativeResources.Products, line, row.ExternalId, "NATURAL_KEY_CONFLICT", "sku");
                    }
                }

                Guid? categoryId = null;

                if (row.CategoryExternalId is not null)
                {
                    categoryId = _categoryIds.TryGetValue(row.CategoryExternalId, out var packageCategory)
                        ? packageCategory
                        : await ResolveExternalReferenceAsync(
                            NativeResources.Categories,
                            row.CategoryExternalId,
                            CategoryTarget,
                            _categoryTargets,
                            "categoryExternalId",
                            row.ExternalId).ConfigureAwait(false);
                }

                _db.Products.Add(new Product
                {
                    Id = id,
                    BusinessId = _businessId,
                    Name = row.Name,
                    Sku = string.IsNullOrWhiteSpace(row.Sku) ? null : row.Sku.Trim(),
                    CategoryId = categoryId,
                    TaxType = row.TaxType == "Inclusive" ? TaxType.Inclusive : TaxType.Exclusive,
                    ProductType = ProductType.Single,
                    TrackStock = row.TrackStock,
                    IsActive = row.IsActive,
                    Version = 0,
                });

                AddLink(NativeResources.Products, row.ExternalId, ProductTarget, id);
                _productIds[row.ExternalId] = id;
                _imported++;
            }
        }

        private async Task ApplyVariantsAsync()
        {
            foreach (var group in _package.Variants.GroupBy(variant => variant.ProductExternalId, StringComparer.Ordinal))
            {
                var defaults = group.Count(variant => variant.IsDefault);

                if (defaults > 1)
                {
                    throw Abort(NativeResources.Variants, 0, group.Key, "DEFAULT_VARIANT_CONFLICT", "isDefault");
                }

                var productIsNew = _package.Products.Any(product => product.ExternalId == group.Key)
                    && !_links.ContainsKey((NativeResources.Products, group.Key));

                if (productIsNew && defaults != 1)
                {
                    throw Abort(NativeResources.Variants, 0, group.Key, "DEFAULT_VARIANT_MISSING", "isDefault");
                }

                if (!productIsNew && defaults == 1)
                {
                    var productId = await ResolveProductIdAsync(group.Key).ConfigureAwait(false);

                    if (await _db.ProductVariants.AnyAsync(
                            variant => variant.BusinessId == _businessId
                                && variant.ProductId == productId
                                && variant.IsDefault,
                            _ct).ConfigureAwait(false))
                    {
                        throw Abort(NativeResources.Variants, 0, group.Key, "DEFAULT_VARIANT_CONFLICT", "isDefault");
                    }
                }
            }

            for (var index = 0; index < _package.Variants.Count; index++)
            {
                var row = _package.Variants[index];
                var line = index + 1;

                if (TryResolveLink(NativeResources.Variants, row.ExternalId, VariantTarget, _variantTargets, out var targetId))
                {
                    _variantIds[row.ExternalId] = targetId;
                    _reused++;
                    continue;
                }

                var productId = await ResolveProductIdAsync(row.ProductExternalId).ConfigureAwait(false);
                var id = NativeStableIdentity.Derive(_businessId, "variant", row.ExternalId);

                if (await _db.ProductVariants.AnyAsync(variant => variant.Id == id, _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Variants, line, row.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                if (await _db.ProductVariants.AnyAsync(
                        variant => variant.BusinessId == _businessId
                            && variant.ProductId == productId
                            && variant.Name == row.Name,
                        _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Variants, line, row.ExternalId, "NATURAL_KEY_CONFLICT", "name");
                }

                if (!string.IsNullOrWhiteSpace(row.Barcode)
                    && await _db.ProductVariants.AnyAsync(
                        variant => variant.BusinessId == _businessId && variant.Barcode == row.Barcode,
                        _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Variants, line, row.ExternalId, "NATURAL_KEY_CONFLICT", "barcode");
                }

                _db.ProductVariants.Add(new ProductVariant
                {
                    Id = id,
                    BusinessId = _businessId,
                    ProductId = productId,
                    Name = row.Name,
                    Sku = string.IsNullOrWhiteSpace(row.Sku) ? null : row.Sku.Trim(),
                    Barcode = string.IsNullOrWhiteSpace(row.Barcode) ? null : row.Barcode.Trim(),
                    SellingPrice = row.SellingPrice,
                    PurchasePrice = row.PurchasePrice,
                    IsDefault = row.IsDefault,
                    IsActive = row.IsActive,
                    Version = 0,
                });

                AddLink(NativeResources.Variants, row.ExternalId, VariantTarget, id);
                _variantIds[row.ExternalId] = id;
                _imported++;
            }
        }

        private async Task<Guid> ResolveProductIdAsync(string productExternalId)
        {
            if (_productIds.TryGetValue(productExternalId, out var packageProduct))
            {
                return packageProduct;
            }

            return await ResolveExternalReferenceAsync(
                NativeResources.Products,
                productExternalId,
                ProductTarget,
                _productTargets,
                "productExternalId",
                null).ConfigureAwait(false);
        }

        private async Task ApplyOpeningsAsync()
        {
            var accounts = (await _db.Accounts.AsNoTracking()
                    .Where(account => account.BusinessId == _businessId)
                    .ToListAsync(_ct)
                    .ConfigureAwait(false))
                .ToDictionary(account => account.Code, StringComparer.Ordinal);

            var locations = (await _db.BusinessLocations.AsNoTracking()
                    .Where(location => location.BusinessId == _businessId)
                    .ToListAsync(_ct)
                    .ConfigureAwait(false))
                .Where(location => location.Code is not null)
                .ToDictionary(location => location.Code!, StringComparer.Ordinal);

            for (var index = 0; index < _package.Openings.Count; index++)
            {
                var opening = _package.Openings[index];
                var lineNumber = index + 1;

                if (TryResolveLink(NativeResources.Openings, opening.ExternalId, JournalTarget, _journalTargets, out var linkedEntryId))
                {
                    var journal = await _db.JournalEntries.AsNoTracking()
                        .FirstOrDefaultAsync(entry => entry.Id == linkedEntryId && entry.BusinessId == _businessId, _ct)
                        .ConfigureAwait(false);

                    if (journal is null || journal.Status != EntryStatus.Posted)
                    {
                        throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "LINK_TARGET_NOT_POSTED", null);
                    }

                    _reused++;
                    continue;
                }

                if (!DateOnly.TryParseExact(
                        opening.EntryDate,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var entryDate)
                    || entryDate != _package.Manifest.EffectiveBusinessDate)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "OPENING_DATE_MISMATCH", "entryDate");
                }

                if (!string.Equals(opening.Currency, _business.BaseCurrency, StringComparison.Ordinal))
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CURRENCY_MISMATCH", "currency");
                }

                var period = await LoadOpenPeriodAsync(entryDate, opening.ExternalId, lineNumber).ConfigureAwait(false);
                var resolvedLines = ResolveOpeningLines(opening, accounts, lineNumber);
                var inventoryValue = await ResolveStockAsync(opening, locations, lineNumber).ConfigureAwait(false);
                var (receivables, receivablesTotal) = await ResolveReceivablesAsync(opening, lineNumber).ConfigureAwait(false);
                var (payables, payablesTotal) = await ResolvePayablesAsync(opening, lineNumber).ConfigureAwait(false);

                ValidateControlTotals(opening, resolvedLines.Lines, inventoryValue, receivablesTotal, payablesTotal, lineNumber);

                var entryId = NativeStableIdentity.Derive(_businessId, "opening_journal", opening.ExternalId);
                var sourceId = NativeStableIdentity.Derive(_businessId, "opening_source", opening.ExternalId);

                if (await _db.JournalEntries.AnyAsync(
                        entry => entry.BusinessId == _businessId
                            && (entry.Id == entryId
                                || (entry.SourceType == JournalSourceType.MigrationOpening && entry.SourceId == sourceId)),
                        _ct).ConfigureAwait(false))
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "TARGET_EXISTS_UNLINKED", "externalId");
                }

                var entry = new JournalEntry
                {
                    Id = entryId,
                    BusinessId = _businessId,
                    EntryNumber = $"OPEN-{_package.Manifest.BatchId:N}-{lineNumber:D4}",
                    EntryDate = entryDate,
                    AccountingPeriodId = period.Id,
                    Memo = string.IsNullOrWhiteSpace(opening.Memo) ? $"Native opening {opening.ExternalId}" : opening.Memo.Trim(),
                    SourceType = JournalSourceType.MigrationOpening,
                    SourceId = sourceId,
                    Status = EntryStatus.Draft,
                    Currency = opening.Currency,
                    TotalDebit = resolvedLines.TotalDebit,
                    TotalCredit = resolvedLines.TotalCredit,
                    PostedAtUtc = DateTimeOffset.UtcNow,
                    PostedByUserId = _options.UserId,
                };

                _db.JournalEntries.Add(entry);

                var lineNo = 1;

                foreach (var line in resolvedLines.Lines)
                {
                    _db.JournalLines.Add(new JournalLine
                    {
                        Id = Guid.CreateVersion7(),
                        BusinessId = _businessId,
                        JournalEntryId = entryId,
                        LineNo = lineNo++,
                        AccountId = line.AccountId,
                        DebitAmount = line.Debit,
                        CreditAmount = line.Credit,
                        Currency = opening.Currency,
                        Description = line.Description,
                    });
                }

                var occurredAt = new DateTimeOffset(entryDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

                for (var stockIndex = 0; stockIndex < opening.Stock.Count; stockIndex++)
                {
                    var stock = opening.Stock[stockIndex];
                    var variantId = await ResolveVariantIdAsync(stock.VariantExternalId, opening.ExternalId, lineNumber).ConfigureAwait(false);
                    var location = locations.TryGetValue(stock.LocationCode, out var foundLocation)
                        ? foundLocation
                        : throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "LOCATION_MISSING", "stock.locationCode");

                    if (await _db.InventoryBalances.AnyAsync(
                            balance => balance.BusinessId == _businessId
                                && balance.LocationId == location.Id
                                && balance.VariantId == variantId,
                            _ct).ConfigureAwait(false))
                    {
                        throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "STOCK_CONFLICT", "stock");
                    }

                    var value = Round(stock.Quantity * stock.UnitCost);

                    _db.InventoryMovements.Add(new InventoryMovement
                    {
                        Id = NativeStableIdentity.Derive(
                            _businessId,
                            "opening_movement",
                            $"{opening.ExternalId}|{stockIndex}|{stock.VariantExternalId}|{stock.LocationCode}"),
                        BusinessId = _businessId,
                        LocationId = location.Id,
                        VariantId = variantId,
                        MovementType = MovementType.Opening,
                        QuantityDelta = stock.Quantity,
                        UnitCost = stock.UnitCost,
                        ValueDelta = value,
                        ReferenceType = OpeningReferenceType,
                        ReferenceId = NativeStableIdentity.Derive(
                            _businessId,
                            "opening_movement_reference",
                            $"{opening.ExternalId}|{stockIndex}"),
                        OccurredAtUtc = occurredAt,
                        CreatedByUserId = _options.UserId,
                        Memo = $"Native opening {opening.ExternalId}",
                    });

                    _db.InventoryBalances.Add(new InventoryBalance
                    {
                        Id = NativeStableIdentity.Derive(
                            _businessId,
                            "opening_balance",
                            $"{opening.ExternalId}|{stock.VariantExternalId}|{location.Id}"),
                        BusinessId = _businessId,
                        LocationId = location.Id,
                        VariantId = variantId,
                        OnHand = stock.Quantity,
                        ValueOnHand = value,
                        AverageUnitCost = decimal.Round(value / stock.Quantity, 8, MidpointRounding.AwayFromZero),
                        LastMovementAtUtc = occurredAt,
                    });
                }

                foreach (var receivable in receivables)
                {
                    _db.OpenItems.Add(CreateOpenItem(OpenItemKind.Receivable, receivable, opening, entryId, entryDate));
                }

                foreach (var payable in payables)
                {
                    _db.OpenItems.Add(CreateOpenItem(OpenItemKind.Payable, payable, opening, entryId, entryDate));
                }

                AddLink(NativeResources.Openings, opening.ExternalId, JournalTarget, entryId);

                // The ledger only accepts lines while the entry is Draft. The whole opening (journal, stock,
                // party items and source link) is still one transaction; the deferred balance check runs at
                // COMMIT.
                await _db.SaveChangesAsync(_ct).ConfigureAwait(false);

                entry.Status = EntryStatus.Posted;
                await _db.SaveChangesAsync(_ct).ConfigureAwait(false);

                _openings.Add(new NativeOpeningEvidence(
                    opening.ExternalId,
                    entryId,
                    inventoryValue,
                    receivablesTotal,
                    payablesTotal,
                    receivables.Select(item => item.Evidence).ToList(),
                    payables.Select(item => item.Evidence).ToList()));

                _imported++;
            }
        }

        private async Task<AccountingPeriod> LoadOpenPeriodAsync(DateOnly entryDate, string externalId, int line)
        {
            var periods = await _db.AccountingPeriods
                .FromSql($"SELECT * FROM accounting_periods WHERE business_id = {_businessId} AND start_date <= {entryDate} AND end_date >= {entryDate} FOR SHARE")
                .ToListAsync(_ct)
                .ConfigureAwait(false);

            if (periods.Count == 0)
            {
                throw Abort(NativeResources.Openings, line, externalId, "ACCOUNTING_PERIOD_MISSING", "entryDate");
            }

            if (periods.Count > 1)
            {
                throw Abort(NativeResources.Openings, line, externalId, "ACCOUNTING_PERIOD_AMBIGUOUS", "entryDate");
            }

            var period = periods[0];

            if (period.Status != AccountingPeriodStatus.Open)
            {
                throw Abort(NativeResources.Openings, line, externalId, "ACCOUNTING_PERIOD_CLOSED", "entryDate");
            }

            return period;
        }

        private ResolvedOpeningLines ResolveOpeningLines(
            NativeOpeningRow opening,
            IReadOnlyDictionary<string, Account> accounts,
            int lineNumber)
        {
            var lines = new List<ResolvedOpeningLine>();
            decimal totalDebit = 0;
            decimal totalCredit = 0;

            foreach (var line in opening.Lines)
            {
                if (!accounts.TryGetValue(line.AccountCode, out var account) || !account.IsActive)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "ACCOUNT_MISSING", "lines.accountCode");
                }

                var debit = Round(line.Debit);
                var credit = Round(line.Credit);
                totalDebit += debit;
                totalCredit += credit;

                lines.Add(new ResolvedOpeningLine(
                    account.Id,
                    account.AccountType,
                    debit,
                    credit,
                    string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim(),
                    line.AccountCode));
            }

            if (totalDebit != totalCredit)
            {
                throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "OPENING_UNBALANCED", "lines");
            }

            return new ResolvedOpeningLines(lines, totalDebit, totalCredit);
        }

        private async Task<decimal> ResolveStockAsync(
            NativeOpeningRow opening,
            IReadOnlyDictionary<string, BusinessLocation> locations,
            int lineNumber)
        {
            decimal total = 0;

            foreach (var stock in opening.Stock)
            {
                var variantId = await ResolveVariantIdAsync(stock.VariantExternalId, opening.ExternalId, lineNumber).ConfigureAwait(false);
                var location = locations.TryGetValue(stock.LocationCode, out var foundLocation)
                    ? foundLocation
                    : throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "LOCATION_MISSING", "stock.locationCode");

                var tracksStock = await _db.ProductVariants
                    .Where(variant => variant.Id == variantId && variant.BusinessId == _businessId)
                    .Join(
                        _db.Products.Where(product => product.BusinessId == _businessId),
                        variant => variant.ProductId,
                        product => product.Id,
                        (variant, product) => product.TrackStock)
                    .FirstOrDefaultAsync(_ct)
                    .ConfigureAwait(false);

                if (!tracksStock)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "PRODUCT_NOT_STOCK_TRACKED", "stock");
                }

                if (location is null)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "LOCATION_MISSING", "stock.locationCode");
                }

                total += Round(stock.Quantity * stock.UnitCost);
            }

            return total;
        }

        private async Task<Guid> ResolveVariantIdAsync(string variantExternalId, string openingExternalId, int lineNumber)
        {
            if (_variantIds.TryGetValue(variantExternalId, out var packageVariant))
            {
                return packageVariant;
            }

            return await ResolveExternalReferenceAsync(
                NativeResources.Variants,
                variantExternalId,
                VariantTarget,
                _variantTargets,
                "stock.variantExternalId",
                openingExternalId,
                lineNumber).ConfigureAwait(false);
        }

        private async Task<(List<ResolvedParty> Items, decimal Total)> ResolveReceivablesAsync(
            NativeOpeningRow opening,
            int lineNumber)
        {
            var items = new List<ResolvedParty>();
            decimal total = 0;

            for (var index = 0; index < opening.Receivables.Count; index++)
            {
                var party = opening.Receivables[index];
                var customerId = await ResolvePartyIdAsync(
                    NativeResources.Customers,
                    CustomerTarget,
                    _customerTargets,
                    _customerIds,
                    party.PartyExternalId,
                    opening.ExternalId,
                    lineNumber).ConfigureAwait(false);

                var amount = Round(party.Amount);
                total += amount;
                var openItemId = NativeStableIdentity.Derive(
                    _businessId,
                    "open_item",
                    $"{opening.ExternalId}|receivable|{index}");

                items.Add(new ResolvedParty(
                    OpenItemKind.Receivable,
                    customerId,
                    null,
                    amount,
                    party.Reference,
                    openItemId,
                    new NativePartyOpeningEvidence(
                        OpenItemKind.Receivable,
                        customerId,
                        amount,
                        opening.Currency,
                        openItemId,
                        party.Reference)));
            }

            return (items, total);
        }

        private async Task<(List<ResolvedParty> Items, decimal Total)> ResolvePayablesAsync(
            NativeOpeningRow opening,
            int lineNumber)
        {
            var items = new List<ResolvedParty>();
            decimal total = 0;

            for (var index = 0; index < opening.Payables.Count; index++)
            {
                var party = opening.Payables[index];
                var supplierId = await ResolvePartyIdAsync(
                    NativeResources.Suppliers,
                    SupplierTarget,
                    _supplierTargets,
                    _supplierIds,
                    party.PartyExternalId,
                    opening.ExternalId,
                    lineNumber).ConfigureAwait(false);

                var amount = Round(party.Amount);
                total += amount;
                var openItemId = NativeStableIdentity.Derive(
                    _businessId,
                    "open_item",
                    $"{opening.ExternalId}|payable|{index}");

                items.Add(new ResolvedParty(
                    OpenItemKind.Payable,
                    null,
                    supplierId,
                    amount,
                    party.Reference,
                    openItemId,
                    new NativePartyOpeningEvidence(
                        OpenItemKind.Payable,
                        supplierId,
                        amount,
                        opening.Currency,
                        openItemId,
                        party.Reference)));
            }

            return (items, total);
        }

        private async Task<Guid> ResolvePartyIdAsync(
            string resource,
            string targetType,
            HashSet<Guid> targets,
            IReadOnlyDictionary<string, Guid> packageIds,
            string externalId,
            string openingExternalId,
            int lineNumber)
        {
            if (packageIds.TryGetValue(externalId, out var packageId))
            {
                return packageId;
            }

            return await ResolveExternalReferenceAsync(
                resource,
                externalId,
                targetType,
                targets,
                resource == NativeResources.Customers ? "receivables.partyExternalId" : "payables.partyExternalId",
                openingExternalId,
                lineNumber).ConfigureAwait(false);
        }

        private void ValidateControlTotals(
            NativeOpeningRow opening,
            IReadOnlyList<ResolvedOpeningLine> lines,
            decimal inventoryValue,
            decimal receivableTotal,
            decimal payableTotal,
            int lineNumber)
        {
            if (!string.IsNullOrWhiteSpace(opening.InventoryAccountCode))
            {
                var account = FindAccount(opening.InventoryAccountCode, lines, lineNumber, opening.ExternalId);

                if (account.AccountType != AccountType.Asset)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "ACCOUNT_TYPE_MISMATCH", "inventoryAccountCode");
                }

                var net = Round(lines.Where(line => line.AccountId == account.AccountId).Sum(line => line.Debit - line.Credit));

                if (net != inventoryValue)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "inventoryAccountCode");
                }
            }
            else if (inventoryValue != 0)
            {
                throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "inventoryAccountCode");
            }

            if (!string.IsNullOrWhiteSpace(opening.ReceivableAccountCode))
            {
                var account = FindAccount(opening.ReceivableAccountCode, lines, lineNumber, opening.ExternalId);

                if (account.AccountType != AccountType.Asset)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "ACCOUNT_TYPE_MISMATCH", "receivableAccountCode");
                }

                var net = Round(lines.Where(line => line.AccountId == account.AccountId).Sum(line => line.Debit - line.Credit));

                if (net != receivableTotal)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "receivableAccountCode");
                }
            }
            else if (receivableTotal != 0)
            {
                throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "receivableAccountCode");
            }

            if (!string.IsNullOrWhiteSpace(opening.PayableAccountCode))
            {
                var account = FindAccount(opening.PayableAccountCode, lines, lineNumber, opening.ExternalId);

                if (account.AccountType != AccountType.Liability)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "ACCOUNT_TYPE_MISMATCH", "payableAccountCode");
                }

                var net = Round(lines.Where(line => line.AccountId == account.AccountId).Sum(line => line.Credit - line.Debit));

                if (net != payableTotal)
                {
                    throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "payableAccountCode");
                }
            }
            else if (payableTotal != 0)
            {
                throw Abort(NativeResources.Openings, lineNumber, opening.ExternalId, "CONTROL_TOTAL_MISMATCH", "payableAccountCode");
            }
        }

        private static ResolvedOpeningLine FindAccount(
            string code,
            IReadOnlyList<ResolvedOpeningLine> lines,
            int lineNumber,
            string externalId)
        {
            var line = lines.FirstOrDefault(candidate => candidate.AccountCode == code);

            if (line is null)
            {
                throw new NativeImportAbortException(
                    [new NativeImportQuarantine(NativeResources.Openings, lineNumber, externalId, "CONTROL_ACCOUNT_NOT_POSTED", "accountCode")],
                    "CONTROL_ACCOUNT_NOT_POSTED");
            }

            return line;
        }

        private OpenItem CreateOpenItem(
            OpenItemKind kind,
            ResolvedParty party,
            NativeOpeningRow opening,
            Guid journalEntryId,
            DateOnly documentDate) =>
            new()
            {
                Id = party.OpenItemId,
                BusinessId = _businessId,
                Kind = kind,
                CustomerId = party.CustomerId,
                SupplierId = party.SupplierId,
                SourceType = OpeningReferenceType,
                SourceId = NativeStableIdentity.Derive(
                    _businessId,
                    $"open_item_source|{kind}",
                    $"{opening.ExternalId}|{party.OpenItemId:N}"),
                JournalEntryId = journalEntryId,
                DocumentDate = documentDate,
                DueDate = null,
                Currency = opening.Currency,
                OriginalAmount = party.Amount,
                Reference = string.IsNullOrWhiteSpace(party.Reference) ? null : party.Reference.Trim(),
                Version = 0,
            };

        private async Task<Guid> ResolveExternalReferenceAsync(
            string resource,
            string externalId,
            string targetType,
            HashSet<Guid> targets,
            string field,
            string? ownerExternalId,
            int lineNumber = 0)
        {
            if (!_links.TryGetValue((resource, externalId), out var link))
            {
                throw Abort(resource, lineNumber, ownerExternalId ?? externalId, "REFERENCE_MISSING", field);
            }

            if (!string.Equals(link.TargetType, targetType, StringComparison.Ordinal)
                || !targets.Contains(link.TargetId))
            {
                throw Abort(resource, lineNumber, ownerExternalId ?? externalId, "REFERENCE_CONFLICT", field);
            }

            return link.TargetId;
        }

        private bool TryResolveLink(
            string resource,
            string externalId,
            string targetType,
            HashSet<Guid> targets,
            out Guid targetId)
        {
            targetId = Guid.Empty;

            if (!_links.TryGetValue((resource, externalId), out var link))
            {
                return false;
            }

            var fingerprint = _package.Fingerprint(resource, externalId);

            if (!string.Equals(link.PayloadSha256, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw Abort(resource, 0, externalId, "LINK_CONFLICT", "payload");
            }

            if (!string.Equals(link.TargetType, targetType, StringComparison.Ordinal)
                || !targets.Contains(link.TargetId))
            {
                throw Abort(resource, 0, externalId, "LINK_TARGET_CONFLICT", "target");
            }

            targetId = link.TargetId;
            return true;
        }

        private void AddLink(string resource, string externalId, string targetType, Guid targetId)
        {
            var fingerprint = _package.Fingerprint(resource, externalId);

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                throw Abort(resource, 0, externalId, "LINK_FINGERPRINT_MISSING", null);
            }

            _db.ExternalRecordLinks.Add(new ExternalRecordLink
            {
                Id = Guid.CreateVersion7(),
                BusinessId = _businessId,
                BatchId = _package.Manifest.BatchId,
                DatasetNamespace = NativeStableIdentity.DatasetNamespace,
                ResourceType = resource,
                ExternalId = externalId,
                PayloadSha256 = fingerprint,
                TargetType = targetType,
                TargetId = targetId,
            });
        }

        private static decimal Round(decimal value) =>
            decimal.Round(value, 4, MidpointRounding.AwayFromZero);

        private NativeImportAbortException Abort(
            string resource,
            long lineNumber,
            string? externalId,
            string reason,
            string? field) =>
            new(
                [new NativeImportQuarantine(
                    resource,
                    lineNumber,
                    NativePackageReader.IsSafeExternalId(externalId) ? externalId : null,
                    reason,
                    field)],
                reason);
    }

    private sealed record ResolvedOpeningLine(
        Guid AccountId,
        AccountType AccountType,
        decimal Debit,
        decimal Credit,
        string? Description,
        string AccountCode = "");

    private sealed record ResolvedOpeningLines(
        IReadOnlyList<ResolvedOpeningLine> Lines,
        decimal TotalDebit,
        decimal TotalCredit);

    private sealed record ResolvedParty(
        OpenItemKind Kind,
        Guid? CustomerId,
        Guid? SupplierId,
        decimal Amount,
        string? Reference,
        Guid OpenItemId,
        NativePartyOpeningEvidence Evidence);

    private sealed class NativeImportAbortException : Exception
    {
        public NativeImportAbortException(
            IReadOnlyList<NativeImportQuarantine> quarantines,
            string failure)
            : base(failure)
        {
            Quarantines = quarantines;
            Failure = failure;
        }

        public IReadOnlyList<NativeImportQuarantine> Quarantines { get; }

        public string Failure { get; }
    }
}
