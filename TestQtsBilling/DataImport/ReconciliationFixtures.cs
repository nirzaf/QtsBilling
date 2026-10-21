using System.Security.Cryptography;
using System.Text;
using QtsBilling.DataImport;

namespace TestQtsBilling.DataImport;

/// <summary>
/// Deterministic task-20 reports. Every fixture is signed with test-only keys; no fixture is ever
/// allowed to become production trust and no fixture touches a database or a source directory.
/// </summary>
internal static class ReconciliationTestData
{
    public const string ReportKeyId = "test-reconciliation-report";
    public const string ApprovalKeyId = "test-owner-approval";
    public const string UnknownKeyId = "test-unknown-key";

    public static readonly byte[] ReportKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("qts-reconciliation-tests-only"));

    public static readonly byte[] ApprovalKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("qts-reconciliation-owner-tests-only"));

    public static readonly Guid BusinessId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid BatchId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static readonly Guid CustomerTargetId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    public static Dictionary<string, byte[]> ReportTrust() =>
        new(StringComparer.Ordinal) { [ReportKeyId] = ReportKey };

    public static Dictionary<string, byte[]> ApprovalTrust() =>
        new(StringComparer.Ordinal) { [ApprovalKeyId] = ApprovalKey };

    public static ReconciliationReportBody CleanBody(DateTimeOffset? generatedAt = null) => new()
    {
        ReportSchema = ReconciliationReportBody.CurrentSchema,
        ToolSha256 = Hash("tool"),
        ManifestSha256 = Hash("manifest"),
        TargetSchema = "postgresql",
        SchemaHead = "20261021000000_NativeImport",
        BatchId = BatchId,
        TargetBusinessId = BusinessId,
        GeneratedAtUtc = generatedAt ?? DateTimeOffset.Parse("2026-10-21T12:00:00+00:00"),
        ImportCommitted = true,
        Resources =
        [
            new ReconciliationResourceComparison
            {
                Resource = NativeResources.Customers,
                SourceCount = 1,
                AcceptedCount = 1,
                QuarantinedCount = 0,
                PersistedCount = 1,
                UnexpectedCount = 0,
                SourceSha256 = Hash("source-customers"),
                ExpectedIdentitySha256 = Hash("accepted-identity"),
                ActualIdentitySha256 = Hash("accepted-identity"),
                ExpectedBusinessId = BusinessId,
                Targets =
                [
                    new ReconciliationTargetReference
                    {
                        ExternalId = "C-1",
                        TargetId = CustomerTargetId,
                        BusinessId = BusinessId,
                        Exists = true,
                    },
                ],
            },
        ],
        Aggregates =
        [
            new ReconciliationAggregateComparison
            {
                Name = "V-1@MAIN",
                Kind = "inventory",
                LocationId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
                ExpectedBusinessId = BusinessId,
                ActualBusinessId = BusinessId,
                ExpectedQuantity = 2m,
                ActualQuantity = 2m,
                ExpectedValue = 20m,
                ActualValue = 20m,
            },
        ],
        Parties =
        [
            new ReconciliationPartyComparison
            {
                Kind = "receivable",
                ExternalId = "C-1",
                PartyId = CustomerTargetId,
                BusinessId = BusinessId,
                ExpectedAmount = 12.5m,
                ActualAmount = 12.5m,
            },
        ],
        Ledger =
        [
            new ReconciliationLedgerComparison
            {
                AccountCode = "1200",
                AccountId = Guid.Parse("55555555-5555-4555-8555-555555555555"),
                BusinessId = BusinessId,
                ExpectedDebit = 32.5m,
                ExpectedCredit = 32.5m,
                ActualDebit = 32.5m,
                ActualCredit = 32.5m,
            },
        ],
        Controls =
        [
            new ReconciliationControlComparison
            {
                Name = "receivable:1200",
                BusinessId = BusinessId,
                DetailAmount = 12.5m,
                LedgerAmount = 12.5m,
            },
        ],
        Exceptions = [],
    };

    public static ReconciliationReportDocument Signed(
        ReconciliationReportBody? body = null,
        string keyId = ReportKeyId,
        byte[]? key = null) =>
        ReconciliationSigner.Sign(body ?? CleanBody(), keyId, key ?? ReportKey);

    public static ReconciliationReportBody WithException(
        this ReconciliationReportBody body,
        string exceptionId,
        bool financial = false) =>
        body with
        {
            Exceptions =
            [
                .. body.Exceptions,
                new ReconciliationException
                {
                    ExceptionId = exceptionId,
                    Scope = "test",
                    Reason = $"Approved non-financial exception {exceptionId}.",
                    Financial = financial,
                    ExternalId = "C-1",
                },
            ],
        };

    public static ReconciliationApprovalSetDocument Approvals(
        ReconciliationReportDocument report,
        params string[] exceptionIds) =>
        Approvals(report, ApprovalKeyId, ApprovalKey, exceptionIds);

    public static ReconciliationApprovalSetDocument Approvals(
        ReconciliationReportDocument report,
        string keyId,
        byte[] key,
        params string[] exceptionIds) =>
        ReconciliationSigner.SignApprovals(
            new ReconciliationApprovalSetBody
            {
                ReportSha256 = report.BodySha256,
                Approvals = exceptionIds.Select(id => new ReconciliationExceptionApproval
                {
                    ExceptionId = id,
                    Reason = "The owner reviewed the non-financial exception.",
                    Approver = "owner@example.test",
                    ApprovedAtUtc = DateTimeOffset.Parse("2026-10-21T12:05:00+00:00"),
                }).ToList(),
            },
            keyId,
            key);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
