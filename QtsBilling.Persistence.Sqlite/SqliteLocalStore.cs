using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Sqlite.Entities;
using QtsModels.ValueObjects;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// The device-local SQLite store (QB-17, specification 4.2). One WAL database file contains two clearly
/// separated table groups:
/// <list type="bullet">
///   <item>discardable cache tables (<c>cached_*</c>, <c>cache_tombstones</c>), and</item>
///   <item>durable pending tables (<c>local_sale_*</c>, <c>local_cash_movements</c>,
///   <c>outbox_operations</c>, <c>inbox_receipts</c>, <c>device_profile</c>, <c>sync_state</c>).</item>
/// </list>
/// A single file is required so an offline capture can atomically write the sale, its lines, the cash
/// movement and the outbox row in one transaction. Cache rebuilds only delete the cache tables; pending
/// money and outbox rows are never touched. Money and quantities cross the boundary as checked scaled
/// <c>INTEGER</c> values (never <c>REAL</c>).
/// </summary>
public sealed class SqliteLocalStore
{
    private readonly IDbContextFactory<LocalStoreDbContext> _factory;
    private readonly SqliteLocalStoreOptions _options;
    private readonly SqliteWriteGate _writeGate;
    private readonly TimeProvider _time;

    public SqliteLocalStore(
        IDbContextFactory<LocalStoreDbContext> factory,
        SqliteLocalStoreOptions options,
        SqliteWriteGate writeGate,
        TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _time = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("A local SQLite database path is required.", nameof(options));
        }
    }

    /// <summary>
    /// Runs before sync starts: integrity check, schema compatibility check, WAL, pre-migration backup and
    /// pending migrations. An unknown/newer migration history or a failed integrity check blocks startup and
    /// leaves the file (and any pending money rows) untouched.
    /// </summary>
    public async Task<SqliteStartupResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _writeGate.AcquireAsync(cancellationToken);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (_options.RunIntegrityCheckOnStartup)
            {
                string? integrity;
                try
                {
                    integrity = await ScalarAsync(db, "PRAGMA quick_check;", cancellationToken) as string;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new SqliteCorruptionException(
                        "The local SQLite database failed to open its integrity check. No automatic rebuild or deletion was attempted.",
                        ex);
                }

                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SqliteCorruptionException(
                        $"The local SQLite database failed PRAGMA quick_check (result: '{integrity ?? "<null>"}'). " +
                        "No automatic rebuild or deletion was attempted.");
                }
            }

            var known = db.Database.GetMigrations().ToArray();
            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0)
            {
                throw new SqliteSchemaCompatibilityException(
                    $"The local database contains migration(s) unknown to this application: {string.Join(", ", unknown)}. " +
                    "Startup is blocked and the database was not modified.");
            }

            var schemaVersion = known.Length;
            var databaseVersion = Convert.ToInt32(
                await ScalarAsync(db, "PRAGMA user_version;", cancellationToken) ?? 0L,
                CultureInfo.InvariantCulture);
            if (databaseVersion > schemaVersion)
            {
                throw new SqliteSchemaCompatibilityException(
                    $"The local database schema version {databaseVersion} is newer than the supported version {schemaVersion}. " +
                    "Startup is blocked and the database was not modified.");
            }

            var journalMode = ((await ScalarAsync(db, "PRAGMA journal_mode = WAL;", cancellationToken)) as string)
                ?.ToLowerInvariant() ?? "unknown";

            var pending = known.Except(applied, StringComparer.Ordinal).ToArray();
            string? backupPath = null;
            if (pending.Length > 0 &&
                applied.Length > 0 &&
                _options.CreatePreMigrationBackup &&
                File.Exists(_options.DatabasePath) &&
                new FileInfo(_options.DatabasePath).Length > 0)
            {
                backupPath = BuildBackupPath();
                await ExecuteAsync(db, $"VACUUM INTO '{backupPath.Replace("'", "''")}';", cancellationToken);
            }

            if (pending.Length > 0)
            {
                // Migrations run only here, at startup, before any sync operation can acquire the write gate.
                await db.Database.MigrateAsync(cancellationToken);
            }

            await ExecuteAsync(db, $"PRAGMA user_version = {schemaVersion};", cancellationToken);

            if (pending.Length > 0)
            {
                await db.DeviceProfiles.ExecuteUpdateAsync(
                    setters => setters.SetProperty(profile => profile.SchemaVersion, schemaVersion),
                    cancellationToken);
            }

            var recovered = await RecoverSendingCoreAsync(db, cancellationToken);

            return new SqliteStartupResult(
                _options.DatabasePath,
                schemaVersion,
                journalMode,
                IntegrityCheckPassed: _options.RunIntegrityCheckOnStartup,
                AppliedMigrations: pending,
                PreMigrationBackupPath: backupPath,
                RecoveredSendingOperations: recovered);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Sets suspended/in-flight operations back to Pending after a crash or restart.</summary>
    public async Task<int> RecoverInterruptedSendsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await RecoverSendingCoreAsync(db, cancellationToken);
    }

    /// <summary>
    /// Atomically captures a provisional cash sale: draft + fixed-price lines + provisional cash movement +
    /// outbox operation. If any value fails the checked scale conversion, the transaction rolls back and no
    /// receipt may be shown. Replaying the same operation id and payload is a no-op; a different payload is
    /// rejected.
    /// </summary>
    public async Task<Guid> CaptureProvisionalCashSaleAsync(
        ProvisionalCashSaleInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Lines);

        if (input.OperationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty operation id is required.", nameof(input));
        }

        RequireId(input.DeviceId, nameof(input.DeviceId));
        RequireId(input.BusinessId, nameof(input.BusinessId));
        RequireId(input.LocationId, nameof(input.LocationId));
        RequireId(input.UserId, nameof(input.UserId));
        RequireId(input.RegisterId, nameof(input.RegisterId));

        if (input.Lines.Count == 0)
        {
            throw new ArgumentException("A provisional sale requires at least one line.", nameof(input));
        }

        if (input.Amount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Cash collected cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(input.OperationType) || input.OperationType.Length > 64)
        {
            throw new ArgumentException("A bounded operation type is required.", nameof(input));
        }

        if (input.PayloadVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Payload version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(input.CanonicalPayload);

        var operationType = input.OperationType;
        var payloadVersion = input.PayloadVersion;
        var canonicalPayload = input.CanonicalPayload;
        var reference = input.ProvisionalReference ?? $"PROV-{input.OperationId:N}";
        if (reference.Length > 64)
        {
            throw new ArgumentException("The provisional reference must fit in 64 characters.", nameof(input));
        }

        var fingerprint = SqlitePayloadHash.Compute(canonicalPayload);
        if (!string.IsNullOrWhiteSpace(input.Sha256Fingerprint) &&
            !string.Equals(fingerprint, input.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The supplied SHA-256 fingerprint does not match the canonical payload.", nameof(input));
        }

        foreach (var line in input.Lines)
        {
            RequireId(line.VariantId, nameof(line.VariantId));
            if (line.Quantity <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(input), "Line quantity must be positive.");
            }

            if (line.UnitPrice < 0m || line.Tax < 0m || line.Discount < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input), "Line price, tax and discount cannot be negative.");
            }

            // Dry-run the checked scale conversions so a precision loss or overflow fails here, with a clear
            // exception, before the capture transaction starts (the converters enforce the same rule again).
            _ = ScaledDecimal.ToScaledQuantity(line.Quantity);
            _ = ScaledDecimal.ToScaledMoney(line.UnitPrice);
            _ = ScaledDecimal.ToScaledMoney(line.Tax);
            _ = ScaledDecimal.ToScaledMoney(line.Discount);
        }

        _ = ScaledDecimal.ToScaledMoney(input.Amount);

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.OutboxOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.OperationId == input.OperationId, cancellationToken);

        if (existing is not null)
        {
            EnsureSamePayload(existing, fingerprint);
            return existing.OperationId;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var draft = new LocalSaleDraft
        {
            OperationId = input.OperationId,
            ProvisionalReference = reference,
            UserId = input.UserId,
            RegisterId = input.RegisterId,
            PaymentMethod = "cash",
            Amount = input.Amount,
            State = SaleDraftState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Lines = input.Lines
                .Select((line, ordinal) => new LocalSaleLine
                {
                    OperationId = input.OperationId,
                    Ordinal = ordinal,
                    VariantId = line.VariantId,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Tax = line.Tax,
                    Discount = line.Discount,
                    ExpectedCatalogRevision = line.ExpectedCatalogRevision,
                })
                .ToList(),
        };

        db.LocalSaleDrafts.Add(draft);
        db.LocalCashMovements.Add(new LocalCashMovement
        {
            OperationId = input.OperationId,
            RegisterId = input.RegisterId,
            Amount = input.Amount,
            Reason = "provisional cash sale",
            State = SaleDraftState.Pending,
        });

        db.OutboxOperations.Add(new OutboxOperation
        {
            OperationId = input.OperationId,
            DeviceId = input.DeviceId,
            BusinessId = input.BusinessId,
            LocationId = input.LocationId,
            OperationType = operationType,
            PayloadVersion = payloadVersion,
            CanonicalPayload = canonicalPayload,
            Sha256Fingerprint = fingerprint,
            State = OutboxState.Pending,
            Attempts = 0,
            NextAttemptUtc = now,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return input.OperationId;
    }

    /// <summary>Enqueues a non-sale operation (for example a cash-session open/close).</summary>
    public async Task<OutboxOperation> EnqueueOutboxOperationAsync(
        OutboxOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireId(operation.OperationId, nameof(operation.OperationId));
        RequireId(operation.DeviceId, nameof(operation.DeviceId));
        RequireId(operation.BusinessId, nameof(operation.BusinessId));
        RequireId(operation.LocationId, nameof(operation.LocationId));

        if (string.IsNullOrWhiteSpace(operation.OperationType) || operation.OperationType.Length > 64)
        {
            throw new ArgumentException("A bounded operation type is required.", nameof(operation));
        }

        if (operation.PayloadVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), "Payload version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operation.CanonicalPayload);

        var fingerprint = SqlitePayloadHash.Compute(operation.CanonicalPayload);
        if (!string.IsNullOrWhiteSpace(operation.Sha256Fingerprint) &&
            !string.Equals(fingerprint, operation.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The supplied SHA-256 fingerprint does not match the canonical payload.", nameof(operation));
        }

        operation.Sha256Fingerprint = fingerprint;
        operation.State = OutboxState.Pending;
        var now = _time.GetUtcNow().UtcDateTime;
        if (operation.CreatedAtUtc == default)
        {
            operation.CreatedAtUtc = now;
        }

        if (operation.NextAttemptUtc == default)
        {
            operation.NextAttemptUtc = now;
        }

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.OutboxOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operation.OperationId, cancellationToken);

        if (existing is not null)
        {
            EnsureSamePayload(existing, fingerprint);
            return existing;
        }

        db.OutboxOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }

    /// <summary>Operations whose retry time has arrived, ordered for a single-writer sync coordinator.</summary>
    public async Task<IReadOnlyList<OutboxOperation>> GetDueOutboxAsync(
        DateTime now,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be between 1 and 500.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.OutboxOperations
            .AsNoTracking()
            .Where(operation => operation.State == OutboxState.Pending && operation.NextAttemptUtc <= now)
            .OrderBy(operation => operation.CreatedAtUtc)
            .ThenBy(operation => operation.OperationId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<OutboxOperation?> GetOutboxOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.OutboxOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.OperationId == operationId, cancellationToken);
    }

    /// <summary>Marks an operation as in flight and counts the attempt. Terminal rows are left untouched.</summary>
    public async Task<OutboxOperation?> MarkOutboxSendingAsync(
        Guid operationId,
        DateTime? nextAttemptUtc = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var operation = await db.OutboxOperations
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken);

        if (operation is null)
        {
            return null;
        }

        if (operation.State is OutboxState.Acknowledged or OutboxState.Rejected)
        {
            return operation;
        }

        operation.State = OutboxState.Sending;
        operation.Attempts = checked(operation.Attempts + 1);
        if (nextAttemptUtc is not null)
        {
            operation.NextAttemptUtc = nextAttemptUtc.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }

    /// <summary>
    /// Applies a server receipt transactionally: the inbox row, outbox state, draft/cash state and error are
    /// committed together. Re-applying the identical receipt is a no-op; a different outcome is refused.
    /// Applied receipts and rejected drafts are retained for review.
    /// </summary>
    public async Task<InboxReceipt> ApplyReceiptAsync(
        InboxReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireId(receipt.OperationId, nameof(receipt.OperationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.ServerResult);

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var operation = await db.OutboxOperations
            .SingleOrDefaultAsync(candidate => candidate.OperationId == receipt.OperationId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown local operation '{receipt.OperationId}'.");

        var existing = await db.InboxReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OperationId == receipt.OperationId, cancellationToken);

        if (existing is not null)
        {
            if (ReceiptMatches(existing, receipt))
            {
                return existing;
            }

            throw new SqliteReceiptConflictException(
                receipt.OperationId, existing.ServerResult, receipt.ServerResult);
        }

        if (receipt.AppliedAtUtc == default)
        {
            receipt.AppliedAtUtc = _time.GetUtcNow().UtcDateTime;
        }

        var (outboxState, draftState) = MapOutcome(receipt.ServerResult);
        db.InboxReceipts.Add(receipt);

        operation.State = outboxState;

        var draft = await db.LocalSaleDrafts
            .SingleOrDefaultAsync(candidate => candidate.OperationId == receipt.OperationId, cancellationToken);
        if (draft is not null)
        {
            draft.State = draftState;
            draft.UpdatedAtUtc = receipt.AppliedAtUtc;
            draft.LastError = outboxState == OutboxState.Rejected
                ? receipt.ErrorCode ?? receipt.ServerResult
                : null;
        }

        var cash = await db.LocalCashMovements
            .SingleOrDefaultAsync(candidate => candidate.OperationId == receipt.OperationId, cancellationToken);
        if (cash is not null)
        {
            cash.State = draftState;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task<InboxReceipt?> GetInboxReceiptAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.InboxReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.OperationId == operationId, cancellationToken);
    }

    /// <summary>Status summary used by the device UI: pending count, rejected count and provisional cash.</summary>
    public async Task<PendingSummary> GetPendingSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var pending = await db.OutboxOperations.CountAsync(
            operation => operation.State == OutboxState.Pending || operation.State == OutboxState.Sending,
            cancellationToken);
        var rejected = await db.OutboxOperations.CountAsync(
            operation => operation.State == OutboxState.Rejected,
            cancellationToken);
        var cashScaled = Convert.ToInt64(
            await ScalarAsync(
                db,
                "SELECT COALESCE(SUM(amount_scaled), 0) FROM local_sale_drafts WHERE state IN ('Pending','Sending');",
                cancellationToken) ?? 0L,
            CultureInfo.InvariantCulture);

        return new PendingSummary(pending, rejected, ScaledDecimal.FromScaledMoney(cashScaled));
    }

    // ----------------------------------------------------------------------------------------------
    // Discardable cache. RebuildCacheAsync never touches the pending/outbox table group.
    // ----------------------------------------------------------------------------------------------

    public async Task<int> ReplaceCachedProductsAsync(
        Guid businessId,
        Guid locationId,
        IReadOnlyCollection<CachedProduct> products,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        RequireId(businessId, nameof(businessId));
        RequireId(locationId, nameof(locationId));

        foreach (var product in products)
        {
            RequireId(product.ProductId, nameof(product.ProductId));
            if (product.BusinessId != businessId || product.LocationId != locationId)
            {
                throw new ArgumentException("Every cached product must match the requested business/location.");
            }
        }

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.CachedProducts
            .Where(product => product.BusinessId == businessId && product.LocationId == locationId)
            .ExecuteDeleteAsync(cancellationToken);

        db.CachedProducts.AddRange(products);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return products.Count;
    }

    /// <summary>
    /// Provider-proof price query: the converted decimal is compared as the stored scaled <c>INTEGER</c>.
    /// Tests also assert SQLite's <c>typeof()</c> for the column and the exact scaled value.
    /// </summary>
    public async Task<IReadOnlyList<CachedProduct>> GetCachedProductsAbovePriceAsync(
        decimal minimumPrice,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.CachedProducts
            .AsNoTracking()
            .Where(product => product.Price > minimumPrice)
            .OrderBy(product => product.Price)
            .ThenBy(product => product.ProductId)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetCachedProductCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.CachedProducts.CountAsync(cancellationToken);
    }

    public async Task<int> ReplaceCachedCustomersAsync(
        Guid businessId,
        IReadOnlyCollection<CachedCustomer> customers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customers);
        RequireId(businessId, nameof(businessId));

        foreach (var customer in customers)
        {
            RequireId(customer.CustomerId, nameof(customer.CustomerId));
            if (customer.BusinessId != businessId)
            {
                throw new ArgumentException("Every cached customer must match the requested business.");
            }
        }

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.CachedCustomers
            .Where(customer => customer.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        db.CachedCustomers.AddRange(customers);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return customers.Count;
    }

    public async Task<int> GetCachedCustomerCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.CachedCustomers.CountAsync(cancellationToken);
    }

    public async Task<int> ReplaceCachedStockAsync(
        Guid businessId,
        Guid locationId,
        IReadOnlyCollection<CachedStock> stock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stock);
        RequireId(businessId, nameof(businessId));
        RequireId(locationId, nameof(locationId));

        foreach (var row in stock)
        {
            RequireId(row.VariantId, nameof(row.VariantId));
            if (row.BusinessId != businessId || row.LocationId != locationId)
            {
                throw new ArgumentException("Every cached stock row must match the requested business/location.");
            }
        }

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.CachedStock
            .Where(row => row.BusinessId == businessId && row.LocationId == locationId)
            .ExecuteDeleteAsync(cancellationToken);

        db.CachedStock.AddRange(stock);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stock.Count;
    }

    public async Task<int> GetCachedStockCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.CachedStock.CountAsync(cancellationToken);
    }

    public async Task<int> ReplaceCacheTombstonesAsync(
        IReadOnlyCollection<CacheTombstone> tombstones,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tombstones);

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.CacheTombstones.ExecuteDeleteAsync(cancellationToken);
        db.CacheTombstones.AddRange(tombstones);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tombstones.Count;
    }

    /// <summary>
    /// Replaces only the discardable cache. The outbox, provisional sales, cash movements, inbox receipts,
    /// device profile and sync state are untouched (QB-17 acceptance).
    /// </summary>
    public async Task<CacheRebuildResult> RebuildCacheAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var products = await db.CachedProducts.ExecuteDeleteAsync(cancellationToken);
        var customers = await db.CachedCustomers.ExecuteDeleteAsync(cancellationToken);
        var stock = await db.CachedStock.ExecuteDeleteAsync(cancellationToken);
        var tombstones = await db.CacheTombstones.ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CacheRebuildResult(products, customers, stock, tombstones);
    }

    // ----------------------------------------------------------------------------------------------
    // Durable device/sync metadata.
    // ----------------------------------------------------------------------------------------------

    public async Task<DeviceProfile> SaveDeviceProfileAsync(
        DeviceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireId(profile.DeviceId, nameof(profile.DeviceId));
        RequireId(profile.BusinessId, nameof(profile.BusinessId));
        RequireId(profile.LocationId, nameof(profile.LocationId));

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var schemaVersion = db.Database.GetMigrations().Count();
        var existing = await db.DeviceProfiles
            .SingleOrDefaultAsync(candidate => candidate.DeviceId == profile.DeviceId, cancellationToken);

        if (existing is null)
        {
            profile.SchemaVersion = schemaVersion;
            if (profile.EnrolledAtUtc == default)
            {
                profile.EnrolledAtUtc = _time.GetUtcNow().UtcDateTime;
            }

            db.DeviceProfiles.Add(profile);
        }
        else
        {
            existing.BusinessId = profile.BusinessId;
            existing.LocationId = profile.LocationId;
            existing.EnrolledAtUtc = profile.EnrolledAtUtc == default
                ? existing.EnrolledAtUtc
                : profile.EnrolledAtUtc;
            existing.SchemaVersion = schemaVersion;
            profile = existing;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task<DeviceProfile?> GetDeviceProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.DeviceProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<SyncState> SaveSyncStateAsync(
        SyncState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireId(state.BusinessId, nameof(state.BusinessId));
        RequireId(state.LocationId, nameof(state.LocationId));

        using var lease = await _writeGate.AcquireAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.SyncStates.SingleOrDefaultAsync(
            candidate => candidate.BusinessId == state.BusinessId && candidate.LocationId == state.LocationId,
            cancellationToken);

        if (existing is null)
        {
            db.SyncStates.Add(state);
        }
        else
        {
            existing.LastRevision = state.LastRevision;
            existing.LastOrdinal = state.LastOrdinal;
            existing.LastSuccessUtc = state.LastSuccessUtc;
            existing.BaselineVersion = state.BaselineVersion;
            state = existing;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    public async Task<SyncState?> GetSyncStateAsync(
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.SyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                state => state.BusinessId == businessId && state.LocationId == locationId,
                cancellationToken);
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------

    private static async Task<int> RecoverSendingCoreAsync(
        LocalStoreDbContext db,
        CancellationToken cancellationToken)
        => await db.OutboxOperations
            .Where(operation => operation.State == OutboxState.Sending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(operation => operation.State, OutboxState.Pending),
                cancellationToken);

    private static async Task<object?> ScalarAsync(
        LocalStoreDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ExecuteAsync(
        LocalStoreDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private string BuildBackupPath()
    {
        var fullPath = Path.GetFullPath(_options.DatabasePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileName(fullPath);
        var stamp = _time.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{name}.pre-migration-{stamp}.bak");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name}.pre-migration-{stamp}-{suffix++}.bak");
        }

        return candidate;
    }

    private static void EnsureSamePayload(OutboxOperation existing, string fingerprint)
    {
        if (!string.Equals(existing.Sha256Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteSameKeyDifferentPayloadException(existing.OperationId);
        }
    }

    private static (OutboxState Outbox, SaleDraftState Draft) MapOutcome(string serverResult)
        => serverResult switch
        {
            "Posted" or "AlreadyPosted" => (OutboxState.Acknowledged, SaleDraftState.Posted),
            "TransientRetry" => (OutboxState.Pending, SaleDraftState.Pending),
            _ => (OutboxState.Rejected, SaleDraftState.Rejected),
        };

    private static bool ReceiptMatches(InboxReceipt left, InboxReceipt right)
        => string.Equals(left.ServerResult, right.ServerResult, StringComparison.Ordinal)
           && left.ServerSaleId == right.ServerSaleId
           && string.Equals(left.PostedNumber, right.PostedNumber, StringComparison.Ordinal)
           && left.ServerRevision == right.ServerRevision
           && string.Equals(left.ErrorCode, right.ErrorCode, StringComparison.Ordinal);

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty id is required.", name);
        }
    }
}
