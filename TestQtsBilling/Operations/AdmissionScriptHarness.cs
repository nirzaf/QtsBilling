using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestQtsBilling.Operations;

/// <summary>
/// Runs scripts/Test-ProductionAdmission.ps1 in PowerShell 7. The script replaces every external
/// effect with a controlled double, so these tests never touch PostgreSQL, Docker, the network or a
/// real cutover target. A missing PowerShell 7 is a test failure, never a skip (specification task 21).
/// </summary>
public static class AdmissionScriptHarness
{
    public static AdmissionScenarioResult Run(string scenario)
    {
        var repoRoot = FindRepositoryRoot();
        var script = Path.Combine(repoRoot, "scripts", "Test-ProductionAdmission.ps1");

        if (!File.Exists(script))
        {
            throw new FileNotFoundException("The Task 21 admission harness is missing.", script);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Scenario");
        startInfo.ArgumentList.Add(scenario);

        using var process = StartOrFail(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            TryKill(process);
            throw new TimeoutException($"The Task 21 harness scenario '{scenario}' did not finish.");
        }

        Task.WaitAll(stdout, stderr);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Task 21 harness scenario '{scenario}' exited {process.ExitCode}.{Environment.NewLine}{stderr.Result}");
        }

        var jsonLine = stdout.Result
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));

        if (jsonLine is null)
        {
            throw new InvalidOperationException(
                $"The Task 21 harness scenario '{scenario}' produced no JSON result.{Environment.NewLine}{stdout.Result}{Environment.NewLine}{stderr.Result}");
        }

        var envelope = JsonSerializer.Deserialize<Envelope>(
            jsonLine,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (envelope is null)
        {
            throw new InvalidOperationException($"The Task 21 harness scenario '{scenario}' returned invalid JSON.");
        }

        return new AdmissionScenarioResult(
            envelope.Scenario,
            envelope.ExitCode,
            envelope.Mutations,
            envelope.Gates,
            stdout.Result);
    }

    private static Process StartOrFail(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell 7 did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "PowerShell 7 (pwsh) is required to run the Task 21 admission harness; it is not installed or not on PATH.",
                exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the timeout and the kill.
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "qb30-cutover.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The QtsBilling repository root could not be located from the test output.");
    }

    private sealed class Envelope
    {
        [JsonPropertyName("scenario")]
        public string Scenario { get; init; } = string.Empty;

        [JsonPropertyName("exitCode")]
        public int ExitCode { get; init; }

        [JsonPropertyName("mutations")]
        public List<string> Mutations { get; init; } = [];

        [JsonPropertyName("gates")]
        public List<AdmissionGateResult> Gates { get; init; } = [];
    }
}

public sealed record AdmissionScenarioResult(
    string Scenario,
    int ExitCode,
    IReadOnlyList<string> Mutations,
    IReadOnlyList<AdmissionGateResult> Gates,
    string Raw);

public sealed record AdmissionGateResult(
    [property: JsonPropertyName("gate")] string Gate,
    [property: JsonPropertyName("passed")] bool Passed);
