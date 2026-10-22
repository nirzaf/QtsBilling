using Xunit;

namespace TestQtsBilling.Operations;

/// <summary>
/// Task 21 acceptance: a read-only admission is proved read-only, a failed gate stops before every
/// subsequent effect, a caller-supplied codeword without a signed manifest cannot authorize anything, and a valid
/// manifest authorizes exactly one target and one execution order.
/// </summary>
public sealed class ProductionAdmissionTests
{
    [Fact]
    public void T21_01_a_failed_first_gate_stops_before_any_mutation()
    {
        var result = AdmissionScriptHarness.Run("gate1_no_mutations");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Mutations);
        Assert.Contains(
            result.Gates,
            gate => !gate.Passed && gate.Gate.Contains("connection and the served database", StringComparison.Ordinal));
    }

    [Fact]
    public void T21_02_a_caller_codeword_without_a_signed_manifest_is_refused()
    {
        var result = AdmissionScriptHarness.Run("codeword_without_manifest");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Mutations);
        Assert.Contains(
            result.Gates,
            gate => !gate.Passed && gate.Gate.Contains("reviewed approval manifest", StringComparison.Ordinal));
    }

    [Fact]
    public void T21_03_an_approval_for_server_a_cannot_execute_database_b()
    {
        var result = AdmissionScriptHarness.Run("server_a_database_b");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Mutations);
        Assert.Contains(
            result.Gates,
            gate => !gate.Passed && gate.Gate.Contains("manifest is signed", StringComparison.Ordinal));
        Assert.Contains("db-a", result.Raw, StringComparison.Ordinal);
        Assert.Contains("db-b", result.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void T21_04_a_nonzero_import_exit_stops_before_reconciliation_reset_or_smoke()
    {
        var result = AdmissionScriptHarness.Run("nonzero_import_exit");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["backup", "host", "import"], result.Mutations);
        Assert.DoesNotContain("reconcile", result.Mutations);
        Assert.DoesNotContain("verify", result.Mutations);
        Assert.DoesNotContain("identity-reset", result.Mutations);
        Assert.DoesNotContain("smoke", result.Mutations);
        Assert.Contains(
            result.Gates,
            gate => !gate.Passed && gate.Gate.Contains("native package command completed", StringComparison.Ordinal));
    }

    [Fact]
    public void T21_05_a_valid_admission_is_read_only_and_has_no_production_authority()
    {
        var result = AdmissionScriptHarness.Run("valid_preflight");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Mutations);
        Assert.NotEmpty(result.Gates);
        Assert.All(result.Gates, gate => Assert.True(gate.Passed, gate.Gate));
        Assert.Contains(
            "does not imply owner production authorization",
            result.Raw,
            StringComparison.Ordinal);
    }

    [Fact]
    public void T21_06_a_valid_execution_uses_the_approved_evidence_and_ordered_steps()
    {
        var result = AdmissionScriptHarness.Run("valid_execution");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["backup", "host", "import", "reconcile", "verify", "identity-reset", "smoke", "final-integrity"],
            result.Mutations);
        Assert.All(result.Gates, gate => Assert.True(gate.Passed, gate.Gate));
    }

    [Theory]
    [InlineData("bad_report_hash")]
    [InlineData("missing_device")]
    public void T21_07_a_tampered_or_incomplete_manifest_is_refused_without_mutation(string scenario)
    {
        var result = AdmissionScriptHarness.Run(scenario);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Mutations);
        Assert.Contains(result.Gates, gate => !gate.Passed);
    }
}
