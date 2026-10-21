using System.Security.Cryptography;
using QtsBilling.DataImport;
using Xunit;

namespace TestQtsBilling.DataImport;

/// <summary>
/// Task 20: a report is exact per resource/identity/aggregate/party/account/control, is signed over
/// one canonical body and timestamp, and only independently approved non-financial exceptions can
/// ever be waived. No test mutates a package, a source or a target.
/// </summary>
public sealed class ReconciliationTests
{
    private static readonly Dictionary<string, byte[]> ReportTrust = ReconciliationTestData.ReportTrust();
    private static readonly Dictionary<string, byte[]> ApprovalTrust = ReconciliationTestData.ApprovalTrust();

    [Fact]
    public void T20_01_body_timestamp_exception_and_approval_tampering_is_refused()
    {
        var signed = ReconciliationTestData.Signed();
        var original = signed.Body;

        var timestampTampered = signed with
        {
            Body = original with { GeneratedAtUtc = original.GeneratedAtUtc.AddSeconds(1) },
        };
        var timestampResult = ReconciliationVerifier.Verify(timestampTampered, ReportTrust);
        Assert.False(timestampResult.Authenticated);
        Assert.Equal("REPORT_DIGEST_MISMATCH", timestampResult.Failure);

        var aggregateTampered = signed with
        {
            Body = original with
            {
                Aggregates = [original.Aggregates[0] with { ActualValue = 19.99m }],
            },
        };
        var aggregateResult = ReconciliationVerifier.Verify(aggregateTampered, ReportTrust);
        Assert.False(aggregateResult.Authenticated);
        Assert.Equal("REPORT_DIGEST_MISMATCH", aggregateResult.Failure);

        var targetTampered = signed with
        {
            Body = original with
            {
                Resources =
                [
                    original.Resources[0] with
                    {
                        Targets =
                        [
                            original.Resources[0].Targets[0] with
                            {
                                TargetId = Guid.Parse("99999999-9999-4999-8999-999999999999"),
                            },
                        ],
                    },
                ],
            },
        };
        var targetResult = ReconciliationVerifier.Verify(targetTampered, ReportTrust);
        Assert.False(targetResult.Authenticated);
        Assert.Equal("REPORT_DIGEST_MISMATCH", targetResult.Failure);

        var bodyWithException = original.WithException("A");
        var reportWithException = ReconciliationTestData.Signed(bodyWithException);
        var approvals = ReconciliationTestData.Approvals(reportWithException, "A");
        var changedApproval = approvals with
        {
            Body = approvals.Body with
            {
                Approvals =
                [
                    approvals.Body.Approvals[0] with { Approver = "someone-else@example.test" },
                ],
            },
        };
        var approvalResult = ReconciliationVerifier.Verify(
            reportWithException,
            ReportTrust,
            changedApproval,
            ApprovalTrust);
        Assert.True(approvalResult.Authenticated);
        Assert.False(approvalResult.Passed);
        Assert.False(approvalResult.CanAdmitProduction);
        Assert.Contains("APPROVAL_DIGEST_MISMATCH", approvalResult.Findings);
    }

    [Fact]
    public void T20_02_equal_counts_with_wrong_totals_or_tenant_are_refused()
    {
        var original = ReconciliationTestData.CleanBody();

        var wrongValue = original with
        {
            Aggregates = [original.Aggregates[0] with { ActualValue = 19.99m }],
        };
        var valueResult = ReconciliationVerifier.VerifyContent(wrongValue);
        Assert.False(valueResult.Passed);
        Assert.Contains(
            "AGGREGATE:inventory:V-1@MAIN:VALUE_MISMATCH",
            valueResult.Findings);

        var wrongQuantity = original with
        {
            Aggregates = [original.Aggregates[0] with { ActualQuantity = 1.999m }],
        };
        var quantityResult = ReconciliationVerifier.VerifyContent(wrongQuantity);
        Assert.False(quantityResult.Passed);
        Assert.Contains(
            "AGGREGATE:inventory:V-1@MAIN:QUANTITY_MISMATCH",
            quantityResult.Findings);

        var equalCountsWrongIdentity = original with
        {
            Resources =
            [
                original.Resources[0] with
                {
                    ActualIdentitySha256 = ReconciliationTestData.Hash("different-body"),
                },
            ],
        };
        var identityResult = ReconciliationVerifier.VerifyContent(equalCountsWrongIdentity);
        Assert.False(identityResult.Passed);
        Assert.Contains(
            "RESOURCE:customers:IDENTITY_MISMATCH",
            identityResult.Findings);

        var swappedTenant = original with
        {
            Resources =
            [
                original.Resources[0] with
                {
                    Targets =
                    [
                        original.Resources[0].Targets[0] with
                        {
                            BusinessId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                        },
                    ],
                },
            ],
        };
        var tenantResult = ReconciliationVerifier.VerifyContent(swappedTenant);
        Assert.False(tenantResult.Passed);
        Assert.Contains(
            "RESOURCE:customers:TARGET_TENANT_MISMATCH:C-1",
            tenantResult.Findings);

        var wrongParty = original with
        {
            Parties = [original.Parties[0] with { ActualAmount = 12.49m }],
        };
        var partyResult = ReconciliationVerifier.VerifyContent(wrongParty);
        Assert.False(partyResult.Passed);
        Assert.Contains("PARTY:receivable:C-1:AMOUNT_MISMATCH", partyResult.Findings);

        var wrongLedger = original with
        {
            Ledger = [original.Ledger[0] with { ActualCredit = 32.49m }],
        };
        var ledgerResult = ReconciliationVerifier.VerifyContent(wrongLedger);
        Assert.False(ledgerResult.Passed);
        Assert.Contains("LEDGER:1200:BALANCE_MISMATCH", ledgerResult.Findings);

        var wrongControl = original with
        {
            Controls = [original.Controls[0] with { LedgerAmount = 12.49m }],
        };
        var controlResult = ReconciliationVerifier.VerifyContent(wrongControl);
        Assert.False(controlResult.Passed);
        Assert.Contains(
            "CONTROL:receivable:1200:SUBLEDGER_MISMATCH",
            controlResult.Findings);
    }

    [Fact]
    public void T20_03_approval_of_a_does_not_cover_new_exception_b_or_financial_exceptions()
    {
        var bodyWithAAndB = ReconciliationTestData.CleanBody()
            .WithException("A")
            .WithException("B");
        var reportWithAAndB = ReconciliationTestData.Signed(bodyWithAAndB);
        var onlyAApproved = ReconciliationTestData.Approvals(reportWithAAndB, "A");

        var partial = ReconciliationVerifier.Verify(
            reportWithAAndB,
            ReportTrust,
            onlyAApproved,
            ApprovalTrust);
        Assert.True(partial.Authenticated);
        Assert.False(partial.Passed);
        Assert.False(partial.CanAdmitProduction);
        Assert.Contains("APPROVAL_MISSING:B", partial.Findings);

        var bothApproved = ReconciliationTestData.Approvals(reportWithAAndB, "A", "B");
        var complete = ReconciliationVerifier.Verify(
            reportWithAAndB,
            ReportTrust,
            bothApproved,
            ApprovalTrust);
        Assert.True(complete.Authenticated);
        Assert.True(complete.Passed);
        Assert.True(complete.CanAdmitProduction);
        Assert.Empty(complete.Findings);

        var financialReport = ReconciliationTestData.Signed(
            ReconciliationTestData.CleanBody().WithException("FIN", financial: true));
        var financialApproval = ReconciliationTestData.Approvals(financialReport, "FIN");
        var financial = ReconciliationVerifier.Verify(
            financialReport,
            ReportTrust,
            financialApproval,
            ApprovalTrust);
        Assert.True(financial.Authenticated);
        Assert.False(financial.Passed);
        Assert.False(financial.CanAdmitProduction);
        Assert.Contains("FINANCIAL_EXCEPTION", financial.Findings);
        Assert.Contains("APPROVAL_FINANCIAL_EXCEPTION:FIN", financial.Findings);

        // The approval is bound to the exact report digest. Adding B after A was approved on a
        // different body cannot inherit A's approval.
        var reportOnlyA = ReconciliationTestData.Signed(
            ReconciliationTestData.CleanBody().WithException("A"));
        var approvalsForOnlyA = ReconciliationTestData.Approvals(reportOnlyA, "A");
        var staleApproval = ReconciliationVerifier.Verify(
            reportWithAAndB,
            ReportTrust,
            approvalsForOnlyA,
            ApprovalTrust);
        Assert.True(staleApproval.Authenticated);
        Assert.False(staleApproval.Passed);
        Assert.Contains("APPROVAL_REPORT_MISMATCH", staleApproval.Findings);
    }

    [Fact]
    public void T20_04_unsigned_preview_unknown_key_and_wrong_key_are_not_admissible()
    {
        var body = ReconciliationTestData.CleanBody();
        var digest = Convert.ToHexString(SHA256.HashData(body.CanonicalBytes())).ToLowerInvariant();
        var unsigned = new ReconciliationReportDocument(body, digest, null);
        var unsignedResult = ReconciliationVerifier.Verify(unsigned, ReportTrust);
        Assert.False(unsignedResult.Authenticated);
        Assert.False(unsignedResult.Passed);
        Assert.False(unsignedResult.CanAdmitProduction);
        Assert.Equal("UNSIGNED_PREVIEW", unsignedResult.Failure);

        var unknownKey = ReconciliationTestData.Signed(
            keyId: ReconciliationTestData.UnknownKeyId);
        var unknownResult = ReconciliationVerifier.Verify(unknownKey, ReportTrust);
        Assert.False(unknownResult.Authenticated);
        Assert.False(unknownResult.CanAdmitProduction);
        Assert.Equal("SIGNATURE_KEY_UNKNOWN", unknownResult.Failure);

        var wrongMaterial = ReconciliationTestData.Signed(
            key: SHA256.HashData("not-the-trusted-key"u8));
        var wrongResult = ReconciliationVerifier.Verify(wrongMaterial, ReportTrust);
        Assert.False(wrongResult.Authenticated);
        Assert.False(wrongResult.CanAdmitProduction);
        Assert.Equal("SIGNATURE_INVALID", wrongResult.Failure);

        var exceptionReport = ReconciliationTestData.Signed(
            ReconciliationTestData.CleanBody().WithException("A"));
        var unsignedApproval = ReconciliationTestData.Approvals(exceptionReport, "A") with
        {
            Approval = null,
        };
        var unsignedApprovalResult = ReconciliationVerifier.Verify(
            exceptionReport,
            ReportTrust,
            unsignedApproval,
            ApprovalTrust);
        Assert.True(unsignedApprovalResult.Authenticated);
        Assert.False(unsignedApprovalResult.Passed);
        Assert.False(unsignedApprovalResult.CanAdmitProduction);
        Assert.Contains("APPROVALS_UNSIGNED", unsignedApprovalResult.Findings);
    }

    [Fact]
    public void T20_05_exact_authenticated_results_round_trip_without_changing_the_body()
    {
        var signed = ReconciliationTestData.Signed();
        var json = ReconciliationReportCodec.Serialize(signed);
        var roundTrip = ReconciliationReportCodec.Deserialize(json);

        Assert.Equal(signed.Body.CanonicalBytes(), roundTrip.Body.CanonicalBytes());
        Assert.Equal(signed.Body.GeneratedAtUtc, roundTrip.Body.GeneratedAtUtc);
        Assert.Equal(signed.BodySha256, roundTrip.BodySha256);
        Assert.Equal(json, ReconciliationReportCodec.Serialize(roundTrip));

        var first = ReconciliationVerifier.Verify(roundTrip, ReportTrust);
        var second = ReconciliationVerifier.Verify(roundTrip, ReportTrust);

        Assert.True(first.Authenticated);
        Assert.True(first.Passed);
        Assert.True(first.CanAdmitProduction);
        Assert.Empty(first.Findings);
        Assert.Equal(first.Authenticated, second.Authenticated);
        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.CanAdmitProduction, second.CanAdmitProduction);
        Assert.Equal(first.Failure, second.Failure);
        Assert.Equal(first.Findings, second.Findings);
        Assert.Empty(roundTrip.Body.Exceptions);
        Assert.Equal(TimeSpan.Zero, roundTrip.Body.GeneratedAtUtc.Offset);
    }
}
