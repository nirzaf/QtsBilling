using System.Security.Cryptography;
using System.Text.Json;

namespace QtsBilling.DataImport;

/// <summary>
/// Authenticates and evaluates a reconciliation report (specification task 20). A report is only
/// production-admissible when its canonical bytes match the signed digest/HMAC, its comparisons are
/// exact, and every exception is independently approved by exception identity for this exact digest.
/// </summary>
public static class ReconciliationVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ReconciliationVerification Verify(
        ReconciliationReportDocument document,
        IReadOnlyDictionary<string, byte[]> reportTrust,
        ReconciliationApprovalSetDocument? approvals = null,
        IReadOnlyDictionary<string, byte[]>? approvalTrust = null,
        bool productionAdmission = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reportTrust);

        if (document.Approval is null)
        {
            return ReconciliationVerification.Reject(
                "UNSIGNED_PREVIEW",
                "An unsigned preview is not authenticated and cannot admit production.");
        }

        var canonical = document.Body.CanonicalBytes();
        var digest = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();

        if (!string.Equals(digest, document.BodySha256, StringComparison.OrdinalIgnoreCase))
        {
            return ReconciliationVerification.Reject(
                "REPORT_DIGEST_MISMATCH",
                "The body bytes do not match the report digest.");
        }

        if (!VerifySignature(document.Approval, canonical, reportTrust, out var signatureFailure))
        {
            return ReconciliationVerification.Reject(signatureFailure);
        }

        var findings = new List<string>();
        EvaluateBody(document.Body, findings);
        var exceptionIds = document.Body.Exceptions
            .Select(exception => exception.ExceptionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        if (document.Body.Exceptions.Count > 0)
        {
            if (approvals is null)
            {
                findings.Add("EXCEPTIONS_UNAPPROVED");
            }
            else if (!VerifyApprovals(document, approvals, approvalTrust, findings))
            {
                // Findings are populated by VerifyApprovals.
            }
        }
        else if (approvals is not null && approvals.Body.Approvals.Count > 0)
        {
            findings.Add("UNEXPECTED_APPROVALS");
        }

        if (exceptionIds.Count != document.Body.Exceptions.Count)
        {
            findings.Add("EXCEPTION_IDENTITY_INVALID");
        }

        var financialException = document.Body.Exceptions.Any(exception => exception.Financial);

        if (financialException)
        {
            findings.Add("FINANCIAL_EXCEPTION");
        }

        var passed = findings.Count == 0;
        var canAdmit = passed
            && document.Body.ImportCommitted
            && (!productionAdmission || document.Body.Exceptions.All(exception => !exception.Financial));

        return new ReconciliationVerification(
            true,
            passed,
            canAdmit,
            passed ? null : findings[0],
            findings);
    }

    public static ReconciliationVerification VerifyContent(ReconciliationReportBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var findings = new List<string>();
        EvaluateBody(body, findings);

        if (body.Exceptions.Any(exception => exception.Financial))
        {
            findings.Add("FINANCIAL_EXCEPTION");
        }

        return new ReconciliationVerification(
            false,
            findings.Count == 0,
            false,
            findings.Count == 0 ? null : findings[0],
            findings);
    }

    private static void EvaluateBody(ReconciliationReportBody body, List<string> findings)
    {
        if (!string.Equals(body.ReportSchema, ReconciliationReportBody.CurrentSchema, StringComparison.Ordinal))
        {
            findings.Add("REPORT_SCHEMA_UNSUPPORTED");
        }

        if (!body.ImportCommitted)
        {
            findings.Add("IMPORT_NOT_COMMITTED");
        }

        if (body.TargetBusinessId == Guid.Empty)
        {
            findings.Add("TARGET_BUSINESS_MISSING");
        }

        if (string.IsNullOrWhiteSpace(body.ManifestSha256)
            || string.IsNullOrWhiteSpace(body.ToolSha256)
            || string.IsNullOrWhiteSpace(body.TargetSchema)
            || !IsSha256Hex(body.ManifestSha256)
            || !IsSha256Hex(body.ToolSha256))
        {
            findings.Add("REPORT_PROVENANCE_INCOMPLETE");
        }

        if (string.IsNullOrWhiteSpace(body.SchemaHead))
        {
            findings.Add("SCHEMA_HEAD_MISSING");
        }

        foreach (var resource in body.Resources)
        {
            var prefix = $"RESOURCE:{resource.Resource}";

            if (resource.SourceCount < 0
                || resource.AcceptedCount < 0
                || resource.QuarantinedCount < 0
                || resource.PersistedCount < 0
                || resource.UnexpectedCount < 0)
            {
                findings.Add($"{prefix}:NEGATIVE_COUNT");
                continue;
            }

            if (resource.AcceptedCount + resource.QuarantinedCount != resource.SourceCount)
            {
                findings.Add($"{prefix}:SOURCE_SPLIT_MISMATCH");
            }

            if (resource.PersistedCount != resource.AcceptedCount)
            {
                findings.Add($"{prefix}:PERSISTED_COUNT_MISMATCH");
            }

            if (resource.UnexpectedCount != 0)
            {
                findings.Add($"{prefix}:UNEXPECTED_ROWS");
            }

            if (!string.Equals(
                    resource.ExpectedIdentitySha256,
                    resource.ActualIdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                findings.Add($"{prefix}:IDENTITY_MISMATCH");
            }

            if (resource.Targets.Count != resource.AcceptedCount)
            {
                findings.Add($"{prefix}:TARGET_COUNT_MISMATCH");
            }

            if (resource.ExpectedBusinessId == Guid.Empty)
            {
                findings.Add($"{prefix}:EXPECTED_BUSINESS_MISSING");
            }
            else if (resource.ExpectedBusinessId != body.TargetBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }

            if (string.IsNullOrWhiteSpace(resource.SourceSha256)
                || !IsSha256Hex(resource.SourceSha256)
                || !IsSha256Hex(resource.ExpectedIdentitySha256)
                || !IsSha256Hex(resource.ActualIdentitySha256))
            {
                findings.Add($"{prefix}:PROVENANCE_INCOMPLETE");
            }

            foreach (var target in resource.Targets)
            {
                if (string.IsNullOrWhiteSpace(target.ExternalId) || target.TargetId == Guid.Empty)
                {
                    findings.Add($"{prefix}:TARGET_IDENTITY_INVALID");
                }

                if (!target.Exists)
                {
                    findings.Add($"{prefix}:TARGET_MISSING:{target.ExternalId}");
                }
                else if (target.BusinessId != resource.ExpectedBusinessId)
                {
                    findings.Add($"{prefix}:TARGET_TENANT_MISMATCH:{target.ExternalId}");
                }
            }
        }

        foreach (var aggregate in body.Aggregates)
        {
            var prefix = $"AGGREGATE:{aggregate.Kind}:{aggregate.Name}";

            if (aggregate.ExpectedBusinessId == Guid.Empty
                || aggregate.ActualBusinessId == Guid.Empty
                || aggregate.ExpectedBusinessId != aggregate.ActualBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }
            else if (aggregate.ExpectedBusinessId != body.TargetBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }

            if (aggregate.ExpectedQuantity != aggregate.ActualQuantity)
            {
                findings.Add($"{prefix}:QUANTITY_MISMATCH");
            }

            if (aggregate.ExpectedValue != aggregate.ActualValue)
            {
                findings.Add($"{prefix}:VALUE_MISMATCH");
            }
        }

        foreach (var party in body.Parties)
        {
            var prefix = $"PARTY:{party.Kind}:{party.ExternalId}";

            if (party.BusinessId == Guid.Empty || party.PartyId == Guid.Empty)
            {
                findings.Add($"{prefix}:IDENTITY_MISSING");
            }
            else if (party.BusinessId != body.TargetBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }

            if (party.ExpectedAmount != party.ActualAmount)
            {
                findings.Add($"{prefix}:AMOUNT_MISMATCH");
            }
        }

        foreach (var line in body.Ledger)
        {
            var prefix = $"LEDGER:{line.AccountCode}";

            if (line.BusinessId == Guid.Empty)
            {
                findings.Add($"{prefix}:TENANT_MISSING");
            }
            else if (line.BusinessId != body.TargetBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }

            if (line.ExpectedDebit != line.ActualDebit || line.ExpectedCredit != line.ActualCredit)
            {
                findings.Add($"{prefix}:BALANCE_MISMATCH");
            }
        }

        foreach (var control in body.Controls)
        {
            var prefix = $"CONTROL:{control.Name}";

            if (control.BusinessId == Guid.Empty)
            {
                findings.Add($"{prefix}:TENANT_MISSING");
            }
            else if (control.BusinessId != body.TargetBusinessId)
            {
                findings.Add($"{prefix}:TENANT_MISMATCH");
            }

            if (control.DetailAmount != control.LedgerAmount)
            {
                findings.Add($"{prefix}:SUBLEDGER_MISMATCH");
            }
        }

        foreach (var exception in body.Exceptions)
        {
            if (string.IsNullOrWhiteSpace(exception.ExceptionId)
                || string.IsNullOrWhiteSpace(exception.Scope)
                || string.IsNullOrWhiteSpace(exception.Reason))
            {
                findings.Add("EXCEPTION_IDENTITY_INVALID");
            }
        }
    }

    private static bool VerifyApprovals(
        ReconciliationReportDocument report,
        ReconciliationApprovalSetDocument approvals,
        IReadOnlyDictionary<string, byte[]>? approvalTrust,
        List<string> findings)
    {
        if (approvalTrust is null || approvalTrust.Count == 0)
        {
            findings.Add("APPROVAL_TRUST_MISSING");
            return false;
        }

        if (approvals.Approval is null)
        {
            findings.Add("APPROVALS_UNSIGNED");
            return false;
        }

        var canonical = approvals.Body.CanonicalBytes();
        var digest = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();

        if (!string.Equals(digest, approvals.BodySha256, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("APPROVAL_DIGEST_MISMATCH");
            return false;
        }

        if (!VerifySignature(approvals.Approval, canonical, approvalTrust, out var signatureFailure))
        {
            findings.Add(signatureFailure);
            return false;
        }

        if (!string.Equals(
                approvals.Body.ReportSha256,
                report.BodySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("APPROVAL_REPORT_MISMATCH");
            return false;
        }

        var reportExceptions = report.Body.Exceptions
            .ToDictionary(exception => exception.ExceptionId, StringComparer.Ordinal);
        var approved = approvals.Body.Approvals
            .GroupBy(approval => approval.ExceptionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var (exceptionId, group) in approved)
        {
            if (!reportExceptions.ContainsKey(exceptionId))
            {
                findings.Add($"APPROVAL_UNKNOWN_EXCEPTION:{exceptionId}");
            }

            if (group.Count != 1)
            {
                findings.Add($"APPROVAL_DUPLICATE:{exceptionId}");
                continue;
            }

            var approval = group[0];

            if (string.IsNullOrWhiteSpace(approval.Reason)
                || string.IsNullOrWhiteSpace(approval.Approver)
                || approval.ApprovedAtUtc == default)
            {
                findings.Add($"APPROVAL_IDENTITY_INVALID:{exceptionId}");
            }

            if (reportExceptions.TryGetValue(exceptionId, out var exception) && exception.Financial)
            {
                findings.Add($"APPROVAL_FINANCIAL_EXCEPTION:{exceptionId}");
            }
        }

        foreach (var exception in report.Body.Exceptions)
        {
            if (!approved.ContainsKey(exception.ExceptionId))
            {
                findings.Add($"APPROVAL_MISSING:{exception.ExceptionId}");
            }
        }

        return findings.Count == 0;
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool VerifySignature(
        NativeManifestApproval approval,
        byte[] canonical,
        IReadOnlyDictionary<string, byte[]> trust,
        out string failure)
    {
        if (!string.Equals(approval.Algorithm, NativeManifestApproval.HmacSha256, StringComparison.Ordinal))
        {
            failure = "SIGNATURE_ALGORITHM_UNSUPPORTED";
            return false;
        }

        if (string.IsNullOrWhiteSpace(approval.KeyId) || !trust.TryGetValue(approval.KeyId, out var key))
        {
            failure = "SIGNATURE_KEY_UNKNOWN";
            return false;
        }

        if (key.Length < 16)
        {
            failure = "SIGNATURE_KEY_INVALID";
            return false;
        }

        byte[] supplied;

        try
        {
            supplied = Convert.FromHexString(approval.Signature);
        }
        catch (FormatException)
        {
            failure = "SIGNATURE_INVALID";
            return false;
        }

        var expected = HMACSHA256.HashData(key, canonical);

        if (supplied.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            failure = "SIGNATURE_INVALID";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}

/// <summary>Serialization for the signed report and approval set. The body is written once.</summary>
public static class ReconciliationReportCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(ReconciliationReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var body = document.Body.CanonicalBytes();

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("body");

            using (var bodyDocument = JsonDocument.Parse(body))
            {
                bodyDocument.RootElement.WriteTo(writer);
            }

            writer.WriteString("bodySha256", document.BodySha256);

            if (document.Approval is null)
            {
                writer.WriteNull("approval");
            }
            else
            {
                writer.WritePropertyName("approval");
                JsonSerializer.Serialize(writer, document.Approval, JsonOptions);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ReconciliationReportDocument Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("body", out var bodyElement)
            || !root.TryGetProperty("bodySha256", out var digestElement))
        {
            throw new FormatException("The reconciliation report is missing its body or digest.");
        }

        var body = JsonSerializer.Deserialize<ReconciliationReportBody>(bodyElement.GetRawText(), JsonOptions)
            ?? throw new FormatException("The reconciliation report body is invalid.");

        NativeManifestApproval? approval = null;

        if (root.TryGetProperty("approval", out var approvalElement)
            && approvalElement.ValueKind != JsonValueKind.Null)
        {
            approval = JsonSerializer.Deserialize<NativeManifestApproval>(
                approvalElement.GetRawText(),
                JsonOptions);
        }

        return new ReconciliationReportDocument(
            body,
            digestElement.GetString() ?? string.Empty,
            approval);
    }

    public static string SerializeApprovals(ReconciliationApprovalSetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var body = document.Body.CanonicalBytes();

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("body");

            using (var bodyDocument = JsonDocument.Parse(body))
            {
                bodyDocument.RootElement.WriteTo(writer);
            }

            writer.WriteString("bodySha256", document.BodySha256);

            if (document.Approval is null)
            {
                writer.WriteNull("approval");
            }
            else
            {
                writer.WritePropertyName("approval");
                JsonSerializer.Serialize(writer, document.Approval, JsonOptions);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ReconciliationApprovalSetDocument DeserializeApprovals(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("body", out var bodyElement)
            || !root.TryGetProperty("bodySha256", out var digestElement))
        {
            throw new FormatException("The approval set is missing its body or digest.");
        }

        var body = JsonSerializer.Deserialize<ReconciliationApprovalSetBody>(
            bodyElement.GetRawText(),
            JsonOptions)
            ?? throw new FormatException("The approval set body is invalid.");

        NativeManifestApproval? approval = null;

        if (root.TryGetProperty("approval", out var approvalElement)
            && approvalElement.ValueKind != JsonValueKind.Null)
        {
            approval = JsonSerializer.Deserialize<NativeManifestApproval>(
                approvalElement.GetRawText(),
                JsonOptions);
        }

        return new ReconciliationApprovalSetDocument(
            body,
            digestElement.GetString() ?? string.Empty,
            approval);
    }
}
