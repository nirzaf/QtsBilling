using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QtsBilling.DataImport;

/// <summary>
/// Reads exactly the native onboarding format of decision D13. The order is deliberate:
/// the manifest signature is verified before a business resource is read, paths are resolved under the
/// package root, every file is bounded and hashed, and the parsed rows are the same staged bytes that
/// were hashed. A second read at apply time must reproduce the digest or the import is blocked.
/// </summary>
public sealed class NativePackageReader
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly IReadOnlySet<string> CustomerFields = Fields(
        "externalId", "name", "email", "phone", "address", "isActive");

    private static readonly IReadOnlySet<string> SupplierFields = CustomerFields;

    private static readonly IReadOnlySet<string> CategoryFields = Fields(
        "externalId", "name", "parentExternalId", "isActive");

    private static readonly IReadOnlySet<string> ProductFields = Fields(
        "externalId", "name", "sku", "categoryExternalId", "taxType", "trackStock", "isActive");

    private static readonly IReadOnlySet<string> VariantFields = Fields(
        "externalId", "productExternalId", "name", "sku", "barcode", "sellingPrice", "purchasePrice", "isDefault", "isActive");

    private static readonly IReadOnlySet<string> OpeningFields = Fields(
        "externalId", "entryDate", "currency", "memo", "inventoryAccountCode", "receivableAccountCode",
        "payableAccountCode", "lines", "stock", "receivables", "payables");

    private static readonly IReadOnlySet<string> OpeningLineFields = Fields(
        "accountCode", "debit", "credit", "description");

    private static readonly IReadOnlySet<string> StockOpeningFields = Fields(
        "variantExternalId", "locationCode", "quantity", "unitCost");

    private static readonly IReadOnlySet<string> PartyOpeningFields = Fields(
        "partyExternalId", "amount", "reference");

    private static readonly string[] SensitiveTokens =
    [
        "password", "passwd", "passcode", "secret", "token", "accesstoken", "refreshtoken",
        "apikey", "privatekey", "cvv", "cvv2", "cvc", "cvc2", "pan", "cardnumber",
        "creditcard", "debitcard", "pin", "pinblock", "track1", "track2", "securitycode",
    ];

    private readonly NativePackageLimits _limits;

    public NativePackageReader(NativePackageLimits? limits = null)
    {
        _limits = limits ?? NativePackageLimits.Default;
    }

    public async Task<NativePackageValidationResult> ValidateAsync(
        string packageRoot,
        IReadOnlyDictionary<string, byte[]> trustKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(trustKeys);

        var rejections = new List<NativeRejection>();
        var root = Path.GetFullPath(packageRoot);

        if (!Directory.Exists(root))
        {
            rejections.Add(Reject(string.Empty, 0, null, "PACKAGE_MISSING", null));
            return Invalid(root, rejections);
        }

        if (HasReparsePoint(root))
        {
            rejections.Add(Reject(string.Empty, 0, null, "PACKAGE_LINK", null));
            return Invalid(root, rejections);
        }

        var manifestPath = Path.Combine(root, "manifest.json");
        var manifestInfo = new FileInfo(manifestPath);

        if (!manifestInfo.Exists)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_MISSING", null));
            return Invalid(root, rejections);
        }

        if (manifestInfo.Length > 1024 * 1024)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_TOO_LARGE", null));
            return Invalid(root, rejections);
        }

        if (IsLink(manifestInfo))
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_LINK", null));
            return Invalid(root, rejections);
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        NativeManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<NativeManifest>(manifestBytes, ManifestOptions);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_INVALID", null));
            return Invalid(root, rejections);
        }

        ValidateManifestShape(manifest, rejections, root);

        if (rejections.Count > 0)
        {
            return Invalid(root, rejections);
        }

        if (!VerifyApproval(manifest, trustKeys, rejections))
        {
            return Invalid(root, rejections);
        }

        var resolved = new List<(NativeManifestResource Declared, string FullPath)>();
        var declaredFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in manifest.Resources)
        {
            if (!NativeResources.IsKnown(resource.Resource))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_UNKNOWN", "resource"));
                continue;
            }

            if (!declaredPaths.Add(resource.Path))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_PATH_DUPLICATE", "path"));
                continue;
            }

            if (!TryResolveResourcePath(root, resource.Path, out var fullPath, out var reason))
            {
                rejections.Add(Reject(resource.Resource, 0, null, reason, "path"));
                continue;
            }

            if (!declaredFullPaths.Add(fullPath))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_PATH_DUPLICATE", "path"));
                continue;
            }

            if (!File.Exists(fullPath))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_MISSING", "path"));
                continue;
            }

            if (HasReparsePoint(fullPath))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_LINK", "path"));
                continue;
            }

            resolved.Add((resource, fullPath));
        }

        if (rejections.Count > 0)
        {
            return Invalid(root, rejections);
        }

        if (!CheckUndeclaredFiles(root, declaredFullPaths, rejections))
        {
            return Invalid(root, rejections);
        }

        var snapshots = new Dictionary<string, ResourceSnapshot>(StringComparer.Ordinal);
        var resources = new Dictionary<string, NativeResourceSummary>(StringComparer.Ordinal);
        var customers = new List<NativeCustomerRow>();
        var suppliers = new List<NativeSupplierRow>();
        var categories = new List<NativeCategoryRow>();
        var products = new List<NativeProductRow>();
        var variants = new List<NativeVariantRow>();
        var openings = new List<NativeOpeningRow>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;

        foreach (var (declared, fullPath) in resolved)
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            totalBytes += bytes.LongLength;

            if (bytes.LongLength > _limits.MaxFileBytes || totalBytes > _limits.MaxTotalBytes)
            {
                rejections.Add(Reject(declared.Resource, 0, null, "RESOURCE_TOO_LARGE", null));
                continue;
            }

            if (declared.ByteLength != bytes.LongLength)
            {
                rejections.Add(Reject(declared.Resource, 0, null, "RESOURCE_LENGTH_MISMATCH", null));
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (!string.Equals(hash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add(Reject(declared.Resource, 0, null, "RESOURCE_HASH_MISMATCH", null));
                continue;
            }

            if (!TrySplitLines(bytes, declared.Resource, rejections, out var lines))
            {
                continue;
            }

            if (lines.Count > _limits.MaxRowsPerResource)
            {
                rejections.Add(Reject(declared.Resource, 0, null, "RESOURCE_TOO_MANY_ROWS", null));
                continue;
            }

            if (declared.Count != lines.Count)
            {
                rejections.Add(Reject(declared.Resource, 0, null, "RESOURCE_COUNT_MISMATCH", null));
                continue;
            }

            var externalIds = new HashSet<string>(StringComparer.Ordinal);
            var readRows = ReadResource(
                declared.Resource,
                lines,
                externalIds,
                fingerprints,
                rejections,
                customers,
                suppliers,
                categories,
                products,
                variants,
                openings);

            if (!readRows)
            {
                continue;
            }

            snapshots[declared.Resource] = new ResourceSnapshot(
                declared.Resource,
                declared.Path,
                fullPath,
                bytes,
                hash);

            resources[declared.Resource] = new NativeResourceSummary(
                declared.Resource,
                declared.Path,
                lines.Count,
                bytes.LongLength,
                hash);
        }

        ValidatePackageNaturalKeys(customers, suppliers, categories, products, variants, rejections);

        if (rejections.Count > 0)
        {
            return Invalid(root, rejections);
        }

        var package = new ValidatedPackage
        {
            Manifest = manifest,
            Root = root,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            Resources = snapshots,
            Customers = customers,
            Suppliers = suppliers,
            Categories = categories,
            Products = products,
            Variants = variants,
            Openings = openings,
            RowFingerprints = fingerprints,
        };

        var report = new NativeValidationReport(
            root,
            manifest.BatchId,
            manifest.TargetBusinessId,
            manifest.EffectiveBusinessDate,
            manifest.Approval?.KeyId,
            resources.Values.ToList(),
            []);

        return new NativePackageValidationResult(report, package);
    }

    /// <summary>
    /// The apply-time proof that the verified bytes still describe the files on disk. It rechecks the
    /// whole file set, so an added undeclared resource also blocks apply.
    /// </summary>
    public async Task<NativeReverificationResult> ReverifyAsync(
        ValidatedPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var rejections = new List<NativeRejection>();

        if (!Directory.Exists(package.Root) || HasReparsePoint(package.Root))
        {
            rejections.Add(Reject(string.Empty, 0, null, "PACKAGE_MISSING", null));
            return new NativeReverificationResult(false, rejections);
        }

        var manifestPath = Path.Combine(package.Root, "manifest.json");

        if (!File.Exists(manifestPath) || IsLink(new FileInfo(manifestPath)))
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_MISSING", null));
            return new NativeReverificationResult(false, rejections);
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

        if (!string.Equals(manifestHash, package.ManifestSha256, StringComparison.Ordinal))
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_CHANGED", null));
        }

        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in package.Resources.Values)
        {
            if (!TryResolveResourcePath(package.Root, snapshot.RelativePath, out var fullPath, out var reason))
            {
                rejections.Add(Reject(snapshot.Resource, 0, null, reason, "path"));
                continue;
            }

            declaredPaths.Add(fullPath);

            if (!string.Equals(fullPath, snapshot.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add(Reject(snapshot.Resource, 0, null, "RESOURCE_PATH_CHANGED", "path"));
                continue;
            }

            if (!File.Exists(fullPath) || HasReparsePoint(fullPath))
            {
                rejections.Add(Reject(snapshot.Resource, 0, null, "RESOURCE_MISSING", "path"));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (bytes.LongLength != snapshot.Bytes.LongLength
                || !string.Equals(hash, snapshot.Sha256, StringComparison.Ordinal))
            {
                rejections.Add(Reject(snapshot.Resource, 0, null, "RESOURCE_CHANGED", null));
            }
        }

        CheckUndeclaredFiles(package.Root, declaredPaths, rejections);

        return new NativeReverificationResult(rejections.Count == 0, rejections);
    }

    public NativePackagePreview Preview(ValidatedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return new NativePackagePreview(
            package.Manifest.BatchId,
            package.Manifest.TargetBusinessId,
            package.Manifest.EffectiveBusinessDate,
            package.Manifest.Approval?.KeyId ?? string.Empty,
            package.Resources.Values
                .Select(snapshot => new NativeResourceSummary(
                    snapshot.Resource,
                    snapshot.RelativePath,
                    CountRows(snapshot.Bytes),
                    snapshot.Bytes.LongLength,
                    snapshot.Sha256))
                .OrderBy(resource => resource.Resource, StringComparer.Ordinal)
                .ToList());
    }

    private static long CountRows(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var start = 0;

        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            var end = index;

            if (end > start && bytes[end - 1] == (byte)'\r')
            {
                end--;
            }

            if (end > start)
            {
                count++;
            }

            start = index + 1;
        }

        if (start < bytes.Length)
        {
            count++;
        }

        return count;
    }

    private void ValidateManifestShape(NativeManifest manifest, List<NativeRejection> rejections, string root)
    {
        if (manifest.SchemaVersion != NativeManifest.CurrentSchemaVersion)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_SCHEMA_UNSUPPORTED", "schemaVersion"));
        }

        if (manifest.MappingVersion <= 0)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_MAPPING_UNSUPPORTED", "mappingVersion"));
        }

        if (!string.Equals(manifest.TargetSchema, NativeManifest.CurrentTargetSchema, StringComparison.Ordinal))
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_TARGET_SCHEMA_UNSUPPORTED", "targetSchema"));
        }

        if (manifest.BatchId == Guid.Empty)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_BATCH_MISSING", "batchId"));
        }

        if (manifest.TargetBusinessId == Guid.Empty)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_BUSINESS_MISSING", "targetBusinessId"));
        }

        if (manifest.EffectiveBusinessDate == default)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_DATE_MISSING", "effectiveBusinessDate"));
        }

        if (manifest.CapturedAtUtc == default)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_CAPTURE_MISSING", "capturedAtUtc"));
        }

        if (manifest.Resources.Count == 0)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_RESOURCES_MISSING", "resources"));
        }

        if (manifest.Resources.Count > _limits.MaxResources)
        {
            rejections.Add(Reject(string.Empty, 0, null, "MANIFEST_TOO_MANY_RESOURCES", "resources"));
        }

        var resourceNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in manifest.Resources)
        {
            if (!resourceNames.Add(resource.Resource))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_DUPLICATE", "resource"));
            }

            if (resource.Count < 0 || resource.ByteLength < 0)
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_BOUNDS_INVALID", null));
            }

            if (!IsHexSha256(resource.Sha256))
            {
                rejections.Add(Reject(resource.Resource, 0, null, "RESOURCE_HASH_INVALID", "sha256"));
            }
        }

        if (manifest.Approval is null)
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_MISSING", "approval"));
            return;
        }

        if (!string.Equals(manifest.Approval.Algorithm, NativeManifestApproval.HmacSha256, StringComparison.Ordinal))
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_ALGORITHM_UNSUPPORTED", "approval.algorithm"));
        }

        if (string.IsNullOrWhiteSpace(manifest.Approval.KeyId) || manifest.Approval.KeyId.Length > 128)
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_KEY_INVALID", "approval.keyId"));
        }

        if (!IsHexSha256(manifest.Approval.Signature))
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_SIGNATURE_INVALID", "approval.signature"));
        }

        if (!Directory.Exists(root))
        {
            rejections.Add(Reject(string.Empty, 0, null, "PACKAGE_MISSING", null));
        }
    }

    private static bool VerifyApproval(
        NativeManifest manifest,
        IReadOnlyDictionary<string, byte[]> trustKeys,
        List<NativeRejection> rejections)
    {
        var approval = manifest.Approval!;

        if (!trustKeys.TryGetValue(approval.KeyId, out var key) || key is null || key.Length < 16)
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_UNKNOWN_KEY", "approval.keyId"));
            return false;
        }

        byte[] supplied;

        try
        {
            supplied = Convert.FromHexString(approval.Signature);
        }
        catch (FormatException)
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_SIGNATURE_INVALID", "approval.signature"));
            return false;
        }

        var expected = HMACSHA256.HashData(key, manifest.CanonicalBody());

        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            rejections.Add(Reject(string.Empty, 0, null, "APPROVAL_INVALID", "approval.signature"));
            return false;
        }

        return true;
    }

    private bool TrySplitLines(
        byte[] bytes,
        string resource,
        List<NativeRejection> rejections,
        out List<byte[]> lines)
    {
        lines = [];
        var start = 0;

        for (var index = 0; index <= bytes.Length; index++)
        {
            if (index < bytes.Length && bytes[index] != (byte)'\n')
            {
                continue;
            }

            var end = index;

            if (end > start && bytes[end - 1] == (byte)'\r')
            {
                end--;
            }

            if (end == start)
            {
                // Only the empty segment after a trailing newline is legal.
                if (index != bytes.Length)
                {
                    rejections.Add(Reject(resource, lines.Count + 1, null, "ROW_EMPTY", null));
                    return false;
                }

                start = index + 1;
                continue;
            }

            if (end - start > _limits.MaxLineBytes)
            {
                rejections.Add(Reject(resource, lines.Count + 1, null, "ROW_TOO_LONG", null));
                return false;
            }

            lines.Add(bytes[start..end]);
            start = index + 1;
        }

        return true;
    }

    private bool ReadResource(
        string resource,
        IReadOnlyList<byte[]> lines,
        HashSet<string> externalIds,
        Dictionary<string, string> fingerprints,
        List<NativeRejection> rejections,
        List<NativeCustomerRow> customers,
        List<NativeSupplierRow> suppliers,
        List<NativeCategoryRow> categories,
        List<NativeProductRow> products,
        List<NativeVariantRow> variants,
        List<NativeOpeningRow> openings)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];

            string text;

            try
            {
                text = StrictUtf8.GetString(line);
            }
            catch (DecoderFallbackException)
            {
                rejections.Add(Reject(resource, lineNumber, null, "ROW_ENCODING_INVALID", null));
                return false;
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = _limits.MaxJsonDepth });
            }
            catch (JsonException)
            {
                rejections.Add(Reject(resource, lineNumber, null, "ROW_INVALID_JSON", null));
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    rejections.Add(Reject(resource, lineNumber, null, "ROW_NOT_OBJECT", null));
                    return false;
                }

                var fieldError = ScanFields(resource, document.RootElement, lineNumber, rejections);

                if (fieldError)
                {
                    return false;
                }

                try
                {
                    switch (resource)
                    {
                        case NativeResources.Customers:
                            var customer = JsonSerializer.Deserialize<NativeCustomerRow>(line, RowOptions)!;
                            ValidateCustomer(customer, lineNumber, rejections);
                            AddUnique(resource, customer.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            customers.Add(customer);
                            break;

                        case NativeResources.Suppliers:
                            var supplier = JsonSerializer.Deserialize<NativeSupplierRow>(line, RowOptions)!;
                            ValidateSupplier(supplier, lineNumber, rejections);
                            AddUnique(resource, supplier.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            suppliers.Add(supplier);
                            break;

                        case NativeResources.Categories:
                            var category = JsonSerializer.Deserialize<NativeCategoryRow>(line, RowOptions)!;
                            ValidateCategory(category, lineNumber, rejections);
                            AddUnique(resource, category.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            categories.Add(category);
                            break;

                        case NativeResources.Products:
                            var product = JsonSerializer.Deserialize<NativeProductRow>(line, RowOptions)!;
                            ValidateProduct(product, lineNumber, rejections);
                            AddUnique(resource, product.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            products.Add(product);
                            break;

                        case NativeResources.Variants:
                            var variant = JsonSerializer.Deserialize<NativeVariantRow>(line, RowOptions)!;
                            ValidateVariant(variant, lineNumber, rejections);
                            AddUnique(resource, variant.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            variants.Add(variant);
                            break;

                        case NativeResources.Openings:
                            var opening = JsonSerializer.Deserialize<NativeOpeningRow>(line, RowOptions)!;
                            ValidateOpening(opening, lineNumber, rejections);
                            AddUnique(resource, opening.ExternalId, lineNumber, externalIds, fingerprints, line, rejections);
                            openings.Add(opening);
                            break;

                        default:
                            rejections.Add(Reject(resource, lineNumber, null, "RESOURCE_UNKNOWN", "resource"));
                            return false;
                    }
                }
                catch (JsonException exception)
                {
                    var field = SafeJsonPath(exception.Path);
                    rejections.Add(Reject(resource, lineNumber, null, "ROW_INVALID", field));
                    return false;
                }
            }

            if (rejections.Count > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScanFields(
        string resource,
        JsonElement element,
        long lineNumber,
        List<NativeRejection> rejections)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var allowed = AllowedFields(resource);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    rejections.Add(Reject(resource, lineNumber, null, "DUPLICATE_FIELD", SafeFieldName(property.Name)));
                    return true;
                }

                if (IsSensitiveName(property.Name))
                {
                    rejections.Add(Reject(resource, lineNumber, null, "PROHIBITED_FIELD", SafeFieldName(property.Name)));
                    return true;
                }

                if (!allowed.Contains(property.Name))
                {
                    rejections.Add(Reject(resource, lineNumber, null, "UNKNOWN_FIELD", SafeFieldName(property.Name)));
                    return true;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var nested = NestedFields(resource, property.Name);

                    if (!ScanNested(property.Value, nested, resource, lineNumber, 1, rejections))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ScanNested(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string resource,
        long lineNumber,
        int depth,
        List<NativeRejection> rejections)
    {
        if (depth > 16)
        {
            rejections.Add(Reject(resource, lineNumber, null, "ROW_TOO_DEEP", null));
            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!ScanNested(item, allowed, resource, lineNumber, depth + 1, rejections))
                {
                    return false;
                }
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                rejections.Add(Reject(resource, lineNumber, null, "DUPLICATE_FIELD", SafeFieldName(property.Name)));
                return false;
            }

            if (IsSensitiveName(property.Name))
            {
                rejections.Add(Reject(resource, lineNumber, null, "PROHIBITED_FIELD", SafeFieldName(property.Name)));
                return false;
            }

            if (!allowed.Contains(property.Name))
            {
                rejections.Add(Reject(resource, lineNumber, null, "UNKNOWN_FIELD", SafeFieldName(property.Name)));
                return false;
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                && !ScanNested(property.Value, NestedFields(resource, property.Name), resource, lineNumber, depth + 1, rejections))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlySet<string> AllowedFields(string resource) => resource switch
    {
        NativeResources.Customers => CustomerFields,
        NativeResources.Suppliers => SupplierFields,
        NativeResources.Categories => CategoryFields,
        NativeResources.Products => ProductFields,
        NativeResources.Variants => VariantFields,
        NativeResources.Openings => OpeningFields,
        _ => Fields(),
    };

    private static IReadOnlySet<string> NestedFields(string resource, string field) =>
        (resource, field) switch
        {
            (NativeResources.Openings, "lines") => OpeningLineFields,
            (NativeResources.Openings, "stock") => StockOpeningFields,
            (NativeResources.Openings, "receivables") => PartyOpeningFields,
            (NativeResources.Openings, "payables") => PartyOpeningFields,
            _ => Fields(),
        };

    private static void AddUnique(
        string resource,
        string externalId,
        long lineNumber,
        HashSet<string> externalIds,
        Dictionary<string, string> fingerprints,
        byte[] line,
        List<NativeRejection> rejections)
    {
        if (!externalIds.Add(externalId))
        {
            rejections.Add(Reject(resource, lineNumber, externalId, "DUPLICATE_EXTERNAL_ID", "externalId"));
            return;
        }

        fingerprints[ValidatedPackage.RowKey(resource, externalId)] =
            Convert.ToHexString(SHA256.HashData(line)).ToLowerInvariant();
    }

    private static void ValidateCustomer(NativeCustomerRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Customers, rejections);
        ValidateText(row.Name, "name", 200, line, NativeResources.Customers, rejections);
        ValidateOptionalText(row.Email, "email", 320, line, NativeResources.Customers, rejections);
        ValidateOptionalText(row.Phone, "phone", 32, line, NativeResources.Customers, rejections);
        ValidateOptionalText(row.Address, "address", 4000, line, NativeResources.Customers, rejections, allowNewlines: true);
    }

    private static void ValidateSupplier(NativeSupplierRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Suppliers, rejections);
        ValidateText(row.Name, "name", 200, line, NativeResources.Suppliers, rejections);
        ValidateOptionalText(row.Email, "email", 320, line, NativeResources.Suppliers, rejections);
        ValidateOptionalText(row.Phone, "phone", 32, line, NativeResources.Suppliers, rejections);
        ValidateOptionalText(row.Address, "address", 4000, line, NativeResources.Suppliers, rejections, allowNewlines: true);
    }

    private static void ValidateCategory(NativeCategoryRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Categories, rejections);
        ValidateText(row.Name, "name", 200, line, NativeResources.Categories, rejections);
        ValidateOptionalExternalId(row.ParentExternalId, "parentExternalId", line, NativeResources.Categories, rejections);

        if (StringComparer.Ordinal.Equals(row.ExternalId, row.ParentExternalId))
        {
            rejections.Add(Reject(NativeResources.Categories, line, row.ExternalId, "FIELD_INVALID", "parentExternalId"));
        }
    }

    private static void ValidateProduct(NativeProductRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Products, rejections);
        ValidateText(row.Name, "name", 200, line, NativeResources.Products, rejections);
        ValidateOptionalText(row.Sku, "sku", 64, line, NativeResources.Products, rejections);
        ValidateOptionalExternalId(row.CategoryExternalId, "categoryExternalId", line, NativeResources.Products, rejections);

        if (row.TaxType is not null
            && row.TaxType is not ("Exclusive" or "Inclusive"))
        {
            rejections.Add(Reject(NativeResources.Products, line, row.ExternalId, "FIELD_INVALID", "taxType"));
        }
    }

    private static void ValidateVariant(NativeVariantRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Variants, rejections);
        ValidateExternalId(row.ProductExternalId, line, NativeResources.Variants, rejections, "productExternalId", row.ExternalId);
        ValidateText(row.Name, "name", 200, line, NativeResources.Variants, rejections);
        ValidateOptionalText(row.Sku, "sku", 64, line, NativeResources.Variants, rejections);
        ValidateOptionalText(row.Barcode, "barcode", 64, line, NativeResources.Variants, rejections);

        if (!HasScale(row.SellingPrice, 4) || row.SellingPrice < 0)
        {
            rejections.Add(Reject(NativeResources.Variants, line, row.ExternalId, "FIELD_INVALID", "sellingPrice"));
        }

        if (row.PurchasePrice is { } purchasePrice && (!HasScale(purchasePrice, 4) || purchasePrice < 0))
        {
            rejections.Add(Reject(NativeResources.Variants, line, row.ExternalId, "FIELD_INVALID", "purchasePrice"));
        }
    }

    private static void ValidateOpening(NativeOpeningRow row, long line, List<NativeRejection> rejections)
    {
        ValidateExternalId(row.ExternalId, line, NativeResources.Openings, rejections);

        if (!DateOnly.TryParseExact(row.EntryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "entryDate"));
        }

        if (row.Currency is null || row.Currency.Length != 3 || !row.Currency.All(char.IsAsciiLetterUpper))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "currency"));
        }

        ValidateOptionalText(row.Memo, "memo", 500, line, NativeResources.Openings, rejections);
        ValidateOptionalText(row.InventoryAccountCode, "inventoryAccountCode", 32, line, NativeResources.Openings, rejections);
        ValidateOptionalText(row.ReceivableAccountCode, "receivableAccountCode", 32, line, NativeResources.Openings, rejections);
        ValidateOptionalText(row.PayableAccountCode, "payableAccountCode", 32, line, NativeResources.Openings, rejections);

        if (row.Lines.Count == 0)
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "lines"));
        }

        var lineNo = 0;

        foreach (var openingLine in row.Lines)
        {
            lineNo++;

            if (string.IsNullOrWhiteSpace(openingLine.AccountCode) || openingLine.AccountCode.Length > 32)
            {
                rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", $"lines[{lineNo}].accountCode"));
            }

            var debit = openingLine.Debit;
            var credit = openingLine.Credit;

            if (!HasScale(debit, 4) || !HasScale(credit, 4)
                || debit < 0 || credit < 0
                || (debit > 0) == (credit > 0))
            {
                rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", $"lines[{lineNo}]"));
            }

            ValidateOptionalText(openingLine.Description, $"lines[{lineNo}].description", 500, line, NativeResources.Openings, rejections);
        }

        if (row.Lines.Sum(entry => entry.Debit) != row.Lines.Sum(entry => entry.Credit))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "OPENING_UNBALANCED", "lines"));
        }

        if (row.Stock.Count > 0 && string.IsNullOrWhiteSpace(row.InventoryAccountCode))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "inventoryAccountCode"));
        }

        if (row.Receivables.Count > 0 && string.IsNullOrWhiteSpace(row.ReceivableAccountCode))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "receivableAccountCode"));
        }

        if (row.Payables.Count > 0 && string.IsNullOrWhiteSpace(row.PayableAccountCode))
        {
            rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", "payableAccountCode"));
        }

        var stockNo = 0;
        var stockKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stock in row.Stock)
        {
            stockNo++;

            if (!IsSafeExternalId(stock.VariantExternalId)
                || !HasScale(stock.Quantity, 6) || stock.Quantity <= 0
                || !HasScale(stock.UnitCost, 4) || stock.UnitCost <= 0
                || string.IsNullOrWhiteSpace(stock.LocationCode) || stock.LocationCode.Length > 32)
            {
                rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", $"stock[{stockNo}]"));
                continue;
            }

            if (!stockKeys.Add($"{stock.VariantExternalId}\u0000{stock.LocationCode}"))
            {
                rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", $"stock[{stockNo}]"));
            }
        }

        var partyNo = 0;

        foreach (var party in row.Receivables.Concat(row.Payables))
        {
            partyNo++;

            if (!IsSafeExternalId(party.PartyExternalId)
                || !HasScale(party.Amount, 4) || party.Amount <= 0)
            {
                rejections.Add(Reject(NativeResources.Openings, line, row.ExternalId, "FIELD_INVALID", $"party[{partyNo}]"));
            }

            ValidateOptionalText(party.Reference, $"party[{partyNo}].reference", 200, line, NativeResources.Openings, rejections);
        }
    }

    private static void ValidatePackageNaturalKeys(
        IReadOnlyList<NativeCustomerRow> customers,
        IReadOnlyList<NativeSupplierRow> suppliers,
        IReadOnlyList<NativeCategoryRow> categories,
        IReadOnlyList<NativeProductRow> products,
        IReadOnlyList<NativeVariantRow> variants,
        List<NativeRejection> rejections)
    {
        AddNaturalKeyDuplicates(
            NativeResources.Customers,
            customers.Where(row => !string.IsNullOrWhiteSpace(row.Phone)).Select(row => NormalizePhone(row.Phone!)),
            rejections);

        AddNaturalKeyDuplicates(
            NativeResources.Suppliers,
            suppliers.Select(row => row.Name.Trim().ToUpperInvariant()),
            rejections);

        AddNaturalKeyDuplicates(
            NativeResources.Categories,
            categories.Select(row => row.Name.Trim().ToUpperInvariant()),
            rejections);

        AddNaturalKeyDuplicates(
            NativeResources.Products,
            products.Where(row => !string.IsNullOrWhiteSpace(row.Sku)).Select(row => row.Sku!.Trim().ToUpperInvariant()),
            rejections);

        AddNaturalKeyDuplicates(
            NativeResources.Variants,
            variants.Where(row => !string.IsNullOrWhiteSpace(row.Barcode)).Select(row => row.Barcode!.Trim().ToUpperInvariant()),
            rejections);
    }

    private static void AddNaturalKeyDuplicates(
        string resource,
        IEnumerable<string> keys,
        List<NativeRejection> rejections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (!seen.Add(key))
            {
                rejections.Add(Reject(resource, 0, null, "NATURAL_KEY_DUPLICATE", null));
                return;
            }
        }
    }

    private static void ValidateExternalId(
        string? value,
        long line,
        string resource,
        List<NativeRejection> rejections,
        string field = "externalId",
        string? externalId = null)
    {
        if (!IsSafeExternalId(value))
        {
            rejections.Add(Reject(resource, line, externalId, "FIELD_INVALID", field));
        }
    }

    private static void ValidateOptionalExternalId(
        string? value,
        string field,
        long line,
        string resource,
        List<NativeRejection> rejections)
    {
        if (value is not null && !IsSafeExternalId(value))
        {
            rejections.Add(Reject(resource, line, null, "FIELD_INVALID", field));
        }
    }

    private static void ValidateText(
        string? value,
        string field,
        int maxLength,
        long line,
        string resource,
        List<NativeRejection> rejections,
        bool allowNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || value != value.Trim()
            || value.Any(character => char.IsControl(character) && !(allowNewlines && (character is '\r' or '\n'))))
        {
            rejections.Add(Reject(resource, line, null, "FIELD_INVALID", field));
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string field,
        int maxLength,
        long line,
        string resource,
        List<NativeRejection> rejections,
        bool allowNewlines = false)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > maxLength
            || value != value.Trim()
            || value.Any(character => char.IsControl(character) && !(allowNewlines && (character is '\r' or '\n'))))
        {
            rejections.Add(Reject(resource, line, null, "FIELD_INVALID", field));
        }
    }

    public static bool IsSafeExternalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim())
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
    }

    public static string NormalizePhone(string phone) =>
        new(phone.Where(char.IsAsciiDigit).ToArray());

    private static bool HasScale(decimal value, int scale)
    {
        var factor = 1m;

        for (var index = 0; index < scale; index++)
        {
            factor *= 10m;
        }

        return decimal.Truncate(value * factor) == value * factor;
    }

    private static bool TryResolveResourcePath(
        string root,
        string relative,
        out string fullPath,
        out string reason)
    {
        fullPath = string.Empty;
        reason = "RESOURCE_PATH_UNSAFE";

        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || Path.IsPathRooted(relative))
        {
            return false;
        }

        if (relative.Contains('\\') || relative.StartsWith('/') || relative.EndsWith('/'))
        {
            return false;
        }

        var segments = relative.Split('/');

        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':')
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var relativeBack = Path.GetRelativePath(root, candidate);

        if (Path.IsPathRooted(relativeBack)
            || relativeBack == ".."
            || relativeBack.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || string.Equals(relativeBack, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var current = root;

        foreach (var segment in relativeBack.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            if (File.Exists(current) && IsLink(new FileInfo(current)))
            {
                return false;
            }

            if (Directory.Exists(current) && HasReparsePoint(current))
            {
                return false;
            }
        }

        fullPath = candidate;
        reason = string.Empty;
        return true;
    }

    private static bool CheckUndeclaredFiles(
        string root,
        HashSet<string> declaredFullPaths,
        List<NativeRejection> rejections)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);

            if (string.Equals(fullPath, Path.Combine(root, "manifest.json"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!declaredFullPaths.Contains(fullPath))
            {
                rejections.Add(Reject(string.Empty, 0, null, "RESOURCE_UNDECLARED", null));
                return false;
            }
        }

        return true;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsLink(FileInfo file)
    {
        try
        {
            return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool IsHexSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(character => character is not ('_' or '-' or ' ')).ToArray())
            .ToLowerInvariant();

        if (normalized is "pan" or "pin" or "token" or "secret" or "cvv" or "cvc")
        {
            return true;
        }

        return SensitiveTokens.Any(token => token.Length > 3 && normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string SafeFieldName(string name) =>
        name.Length <= 64 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '[' or ']')
            ? name
            : "field";

    private static string? SafeJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.Length <= 200 && path.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '$' or '.' or '[' or ']' or '_')
            ? path
            : null;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();

        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && char.IsAsciiDigit(name[3]));
    }

    private static IReadOnlySet<string> Fields(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static NativeRejection Reject(
        string resource,
        long line,
        string? externalId,
        string reason,
        string? field) =>
        new(resource, line, IsSafeExternalId(externalId) ? externalId : null, reason, field);

    private static NativePackageValidationResult Invalid(
        string root,
        IReadOnlyList<NativeRejection> rejections)
    {
        var report = new NativeValidationReport(root, null, null, null, null, [], rejections);
        return new NativePackageValidationResult(report, null);
    }
}

public sealed record NativePackageValidationResult(
    NativeValidationReport Report,
    ValidatedPackage? Package)
{
    public bool IsValid => Package is not null && Report.IsValid;
}

public sealed record NativeReverificationResult(
    bool Ok,
    IReadOnlyList<NativeRejection> Rejections);
