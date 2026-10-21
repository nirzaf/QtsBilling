using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using QtsBilling.DataImport;
using QtsBilling.Persistence.Postgres;

var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
var arguments = new List<string>();

for (var index = 0; index < args.Length; index++)
{
    if (args[index].StartsWith("--", StringComparison.Ordinal))
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"The option '{args[index]}' needs a value.");
            return 2;
        }

        var name = args[index][2..];

        if (!options.TryGetValue(name, out var values))
        {
            values = [];
            options[name] = values;
        }

        values.Add(args[++index]);
    }
    else
    {
        arguments.Add(args[index]);
    }
}

string? Option(string name) =>
    options.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;

IReadOnlyList<string> OptionList(string name) =>
    options.TryGetValue(name, out var values) ? values : [];

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

if (arguments.Count != 1
    || arguments[0] is not ("validate" or "preview" or "apply" or "reconcile" or "verify" or "approve"))
{
    Console.Error.WriteLine(
        "usage: QtsBilling.DataImport <validate|preview|apply|reconcile|verify|approve> [options]");
    Console.Error.WriteLine("  validate|preview|apply|reconcile --package <directory>");
    Console.Error.WriteLine("  apply --result <file> --user <guid>");
    Console.Error.WriteLine("  reconcile --report <file> [--import-result <file>]");
    Console.Error.WriteLine("  verify --report <file> [--approvals <file>]");
    Console.Error.WriteLine("  approve --report <file> --approvals <file> --approver <name> --reason <text> --exception <id[,id]>");
    Console.Error.WriteLine("  the package is native; no legacy snapshot or source database is read.");
    return 2;
}

var command = arguments[0];

if (command is "verify" or "approve")
{
    return await RunReportCommand(command);
}

if (!options.TryGetValue("package", out var packageValues)
    || packageValues.Count == 0
    || string.IsNullOrWhiteSpace(packageValues[^1]))
{
    Console.Error.WriteLine("--package <directory> is required.");
    return 2;
}

var packageRoot = packageValues[^1];

if (!TryReadKey(
        "QTS_NATIVE_IMPORT_KEY_ID",
        "QTS_NATIVE_IMPORT_HMAC_KEY",
        "native package approval",
        out var importKeyId,
        out var importKey,
        out var importKeyError))
{
    Console.Error.WriteLine(importKeyError);
    return 2;
}

var packageReader = new NativePackageReader();
var validation = await packageReader.ValidateAsync(
    packageRoot,
    new Dictionary<string, byte[]> { [importKeyId] = importKey });

if (!validation.IsValid)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(validation.Report, jsonOptions));
    return 1;
}

var package = validation.Package!;

if (command == "validate")
{
    Console.WriteLine(JsonSerializer.Serialize(validation.Report, jsonOptions));
    return 0;
}

if (command == "preview")
{
    Console.WriteLine(JsonSerializer.Serialize(packageReader.Preview(package), jsonOptions));
    return 0;
}

var connectionString = Environment.GetEnvironmentVariable("QTS_POSTGRES_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("QTS_POSTGRES_CONNECTION must name the target PostgreSQL database.");
    return 2;
}

var dbOptions = new DbContextOptionsBuilder<BillingDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new BillingDbContext(dbOptions);

if (command == "apply")
{
    if (!string.Equals(
            Environment.GetEnvironmentVariable("QTS_NATIVE_IMPORT_EXECUTE"),
            "1",
            StringComparison.Ordinal))
    {
        Console.Error.WriteLine("apply requires QTS_NATIVE_IMPORT_EXECUTE=1.");
        return 2;
    }

    Guid? userId = null;

    if (Option("user") is { } userText)
    {
        if (!Guid.TryParse(userText, out var parsedUser))
        {
            Console.Error.WriteLine("--user must be a Guid.");
            return 2;
        }

        userId = parsedUser;
    }

    var writer = new NativeImportWriter(packageReader);
    var result = await writer.ApplyAsync(
        db,
        package,
        new NativeImportOptions
        {
            TargetBusinessId = package.Manifest.TargetBusinessId,
            UserId = userId,
        });

    if (Option("result") is { } resultPath)
    {
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, jsonOptions));
    }

    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    return result.Committed ? 0 : 1;
}

// reconcile
NativeImportResult? importResult = null;

if (Option("import-result") is { } importResultPath)
{
    if (!File.Exists(importResultPath))
    {
        Console.Error.WriteLine($"The import result '{importResultPath}' does not exist.");
        return 2;
    }

    importResult = JsonSerializer.Deserialize<NativeImportResult>(
        await File.ReadAllTextAsync(importResultPath),
        jsonOptions);

    if (importResult is null
        || importResult.BatchId != package.Manifest.BatchId
        || importResult.BusinessId != package.Manifest.TargetBusinessId)
    {
        Console.Error.WriteLine("The import result does not belong to this package and target business.");
        return 1;
    }
}

var reportPath = Option("report");

if (string.IsNullOrWhiteSpace(reportPath))
{
    Console.Error.WriteLine("--report <file> is required for reconcile.");
    return 2;
}

var reportBuilder = new NativeReconciliationBuilder();
var reportBody = await reportBuilder.BuildAsync(db, package, importResult);

ReconciliationReportDocument report;

if (TryReadKey(
        "QTS_RECONCILIATION_KEY_ID",
        "QTS_RECONCILIATION_HMAC_KEY",
        "reconciliation",
        out var reportKeyId,
        out var reportKey,
        out _))
{
    report = ReconciliationSigner.Sign(reportBody, reportKeyId, reportKey);
}
else
{
    var unsignedBytes = reportBody.CanonicalBytes();
    report = new ReconciliationReportDocument(
        reportBody,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unsignedBytes)).ToLowerInvariant(),
        null);
    Console.Error.WriteLine(
        "The report is unsigned because QTS_RECONCILIATION_KEY_ID/QTS_RECONCILIATION_HMAC_KEY are not set. "
        + "An unsigned report is preview-only and cannot admit production.");
}

await File.WriteAllTextAsync(reportPath, ReconciliationReportCodec.Serialize(report));
Console.WriteLine(JsonSerializer.Serialize(reportBody, jsonOptions));
return reportBody.ImportCommitted ? 0 : 1;

async Task<int> RunReportCommand(string reportCommand)
{
    var path = Option("report");

    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.Error.WriteLine("--report <file> must name an existing reconciliation report.");
        return 2;
    }

    if (!TryReadKey(
            "QTS_RECONCILIATION_KEY_ID",
            "QTS_RECONCILIATION_HMAC_KEY",
            "reconciliation",
            out var trustedKeyId,
            out var trustedKey,
            out var reportKeyError))
    {
        Console.Error.WriteLine(reportKeyError);
        return 2;
    }

    ReconciliationReportDocument report;

    try
    {
        report = ReconciliationReportCodec.Deserialize(await File.ReadAllTextAsync(path));
    }
    catch (Exception exception) when (exception is JsonException or FormatException)
    {
        Console.Error.WriteLine("The reconciliation report is not valid JSON.");
        return 1;
    }

    ReconciliationApprovalSetDocument? approvals = null;

    if (Option("approvals") is { } inputApprovalsPath)
    {
        if (!File.Exists(inputApprovalsPath))
        {
            Console.Error.WriteLine($"The approvals file '{inputApprovalsPath}' does not exist.");
            return 2;
        }

        try
        {
            approvals = ReconciliationReportCodec.DeserializeApprovals(
                await File.ReadAllTextAsync(inputApprovalsPath));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            Console.Error.WriteLine("The approval set is not valid JSON.");
            return 1;
        }
    }

    if (reportCommand == "verify")
    {
        var approvalTrust = TryReadKey(
            "QTS_RECONCILIATION_APPROVAL_KEY_ID",
            "QTS_RECONCILIATION_APPROVAL_HMAC_KEY",
            "owner approval",
            out var approvalKeyId,
            out var approvalKey,
            out _)
            ? new Dictionary<string, byte[]> { [approvalKeyId] = approvalKey }
            : null;

        var verification = ReconciliationVerifier.Verify(
            report,
            new Dictionary<string, byte[]> { [trustedKeyId] = trustedKey },
            approvals,
            approvalTrust);

        Console.WriteLine(JsonSerializer.Serialize(verification, jsonOptions));
        return verification.CanAdmitProduction ? 0 : 1;
    }

    // approve: only an authenticated report can be approved; financial exceptions are non-waivable.
    var reportVerification = ReconciliationVerifier.Verify(
        report,
        new Dictionary<string, byte[]> { [trustedKeyId] = trustedKey },
        approvals: null,
        approvalTrust: null,
        productionAdmission: false);

    if (!reportVerification.Authenticated)
    {
        Console.Error.WriteLine(reportVerification.Failure ?? "The report is not authenticated.");
        return 1;
    }

    var requestedIds = OptionList("exception")
        .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    if (requestedIds.Count == 0)
    {
        Console.Error.WriteLine("--exception <id[,id]> is required for approve.");
        return 2;
    }

    var exceptionsById = report.Body.Exceptions.ToDictionary(
        exception => exception.ExceptionId,
        StringComparer.Ordinal);

    if (requestedIds.Any(id => !exceptionsById.ContainsKey(id)))
    {
        Console.Error.WriteLine("Every approved exception must exist in the signed report.");
        return 1;
    }

    if (requestedIds.Any(id => exceptionsById[id].Financial))
    {
        Console.Error.WriteLine("Financial exceptions cannot be approved away.");
        return 1;
    }

    var approver = Option("approver");
    var reason = Option("reason");

    if (string.IsNullOrWhiteSpace(approver) || string.IsNullOrWhiteSpace(reason))
    {
        Console.Error.WriteLine("--approver and --reason are required for approve.");
        return 2;
    }

    DateTimeOffset approvedAt = DateTimeOffset.UtcNow;

    if (Option("approved-at") is { } approvedAtText
        && !DateTimeOffset.TryParse(approvedAtText, out approvedAt))
    {
        Console.Error.WriteLine("--approved-at must be an ISO-8601 timestamp.");
        return 2;
    }

    if (!TryReadKey(
            "QTS_RECONCILIATION_APPROVAL_KEY_ID",
            "QTS_RECONCILIATION_APPROVAL_HMAC_KEY",
            "owner approval",
            out var ownerKeyId,
            out var ownerKey,
            out var ownerKeyError))
    {
        Console.Error.WriteLine(ownerKeyError);
        return 2;
    }

    var approvalBody = new ReconciliationApprovalSetBody
    {
        ReportSha256 = report.BodySha256,
        Approvals = requestedIds.Select(id => new ReconciliationExceptionApproval
        {
            ExceptionId = id,
            Reason = reason,
            Approver = approver,
            ApprovedAtUtc = approvedAt,
        }).ToList(),
    };
    var signedApprovals = ReconciliationSigner.SignApprovals(approvalBody, ownerKeyId, ownerKey);
    var outputApprovalsPath = Option("approvals");

    if (string.IsNullOrWhiteSpace(outputApprovalsPath))
    {
        Console.Error.WriteLine("--approvals <file> is required for approve.");
        return 2;
    }

    await File.WriteAllTextAsync(outputApprovalsPath, ReconciliationReportCodec.SerializeApprovals(signedApprovals));
    Console.WriteLine(JsonSerializer.Serialize(signedApprovals.Body, jsonOptions));
    return 0;
}

static bool TryReadKey(
    string keyIdVariable,
    string keyVariable,
    string label,
    out string keyId,
    out byte[] key,
    out string error)
{
    keyId = string.Empty;
    key = [];
    error = string.Empty;
    var keyIdText = Environment.GetEnvironmentVariable(keyIdVariable);
    var keyHex = Environment.GetEnvironmentVariable(keyVariable);

    if (string.IsNullOrWhiteSpace(keyIdText) || string.IsNullOrWhiteSpace(keyHex))
    {
        error = $"{keyIdVariable} and {keyVariable} must name the {label} trust.";
        return false;
    }

    try
    {
        key = Convert.FromHexString(keyHex);
    }
    catch (FormatException)
    {
        error = $"{keyVariable} is not hexadecimal.";
        return false;
    }

    if (key.Length < 16)
    {
        error = $"{keyVariable} is shorter than 16 bytes.";
        return false;
    }

    keyId = keyIdText;
    return true;
}
