using System.Diagnostics;
using System.Text;

namespace TestQtsBilling.Ui;

/// <summary>
/// Nothing the operator loads may depend on a public network (specification task 10, requirements R01/R11).
/// The self-hosted profile runs on a machine with no internet access, so the shipped shells, styles and
/// scripts are audited as source here, and the same audit runs again against the packaged candidate at
/// Task 45 with egress denied.
/// </summary>
/// <remarks>
/// Documentation and licence links inside comments are deliberately not failures: nothing fetches them.
/// A remote font, icon, script, image, stylesheet or <c>@import</c> is a failure.
/// </remarks>
public sealed class NoExternalAssetsTests
{
    [Theory]
    [InlineData(@"QtsWebApp/QtsWebApp/Components/App.razor")]
    [InlineData(@"QtsBilling.Maui/wwwroot/index.html")]
    [InlineData(@"QtsWebApp/QtsWebApp/wwwroot/app.css")]
    [InlineData(@"QtsBilling.Maui/wwwroot/app.css")]
    public void T10_01_a_shipped_asset_loads_no_public_resource(string relativePath)
    {
        var path = Path.Combine(InvoiceEditorTests.RepositoryRoot(), relativePath);
        var text = File.ReadAllText(path);

        var offenders = RemoteLoads(text);

        Assert.True(
            offenders.Count == 0,
            $"{relativePath} would make the engine fetch: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void T10_01_the_web_shell_does_not_return_to_a_hosted_webfont()
    {
        var shell = File.ReadAllText(Path.Combine(InvoiceEditorTests.RepositoryRoot(), @"QtsWebApp/QtsWebApp/Components/App.razor"));

        // The defect this task closed: the shell fetched Roboto from Google on every cold start.
        Assert.DoesNotContain("fonts.googleapis.com", shell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.gstatic.com", shell, StringComparison.OrdinalIgnoreCase);

        var appCss = File.ReadAllText(Path.Combine(InvoiceEditorTests.RepositoryRoot(), @"QtsWebApp/QtsWebApp/wwwroot/app.css"));
        Assert.Contains("system-ui", appCss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T10_01_the_desktop_shell_still_allows_accessible_zoom()
    {
        var index = File.ReadAllText(Path.Combine(InvoiceEditorTests.RepositoryRoot(), @"QtsBilling.Maui/wwwroot/index.html"));

        // Zoom is an accessibility requirement, not a preference: an operator must read money at 200%+.
        Assert.DoesNotContain("user-scalable=no", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximum-scale", index, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T10_02_the_asset_audit_fails_on_a_remote_import_and_names_the_exact_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qts-asset-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var badHtml = Path.Combine(directory, "bad-shell.html");
        File.WriteAllText(badHtml, """
            <html><head>
              <link href="https://fonts.example.test/css?family=Roboto" rel="stylesheet" />
              <script src="https://cdn.example.test/tracker.js"></script>
              <style>@import url("https://evil.example.test/track.css");</style>
            </head><body>ok</body></html>
            """);

        var (exitCode, output) = RunAssetAudit(badHtml);

        try
        {
            Assert.NotEqual(0, exitCode);
            Assert.Contains("bad-shell.html", output, StringComparison.Ordinal);
            Assert.Contains("remote stylesheet link", output, StringComparison.Ordinal);
            Assert.Contains("remote script", output, StringComparison.Ordinal);
            Assert.Contains("remote CSS import", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void T10_02_the_asset_audit_passes_the_shipped_runtime_and_reports_what_it_scanned()
    {
        var (exitCode, output) = RunAssetAudit();

        Assert.True(exitCode == 0, output);
        Assert.Contains("audit PASSED", output, StringComparison.Ordinal);
        Assert.Matches("scanned [1-9][0-9]* shipped runtime asset", output);
    }

    /// <summary>Runs scripts/verify-runtime-assets.ps1; a missing PowerShell 7 is a blocked run, not a pass.</summary>
    private static (int ExitCode, string Output) RunAssetAudit(params string[] extraTargets)
    {
        var script = Path.Combine(InvoiceEditorTests.RepositoryRoot(), "scripts", "verify-runtime-assets.ps1");
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";

        foreach (var target in extraTargets)
        {
            arguments += $" -Path \"{target}\"";
        }

        var startInfo = new ProcessStartInfo("pwsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? audit;
        try
        {
            audit = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"BLOCKED: the runtime asset audit needs PowerShell 7 and could not start: {exception.Message}",
                exception);
        }

        if (audit is null)
        {
            throw new InvalidOperationException("BLOCKED: the runtime asset audit process did not start.");
        }

        using (audit)
        {
            var stdout = audit.StandardOutput.ReadToEnd();
            var stderr = audit.StandardError.ReadToEnd();
            audit.WaitForExit();

            var combined = new StringBuilder();
            combined.Append(stdout);
            if (stderr.Length > 0)
            {
                combined.AppendLine().Append(stderr);
            }

            return (audit.ExitCode, combined.ToString());
        }
    }

    /// <summary>The attributes and rules that make an engine reach the network, ignoring comments.</summary>
    private static List<string> RemoteLoads(string text)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, @"(?s)<!--.*?-->", " ");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"(?s)/\*.*?\*/", " ");

        (string Label, string Regex)[] patterns =
        [
            ("remote stylesheet link", @"<link[^>]+href\s*=\s*[""']?(https?:|//)"),
            ("remote script", @"<script[^>]+src\s*=\s*[""']?(https?:|//)"),
            ("remote image", @"<img[^>]+src\s*=\s*[""']?(https?:)"),
            ("remote CSS import", @"@import\s+(?:url\(\s*)?[""']?(https?:|//)"),
            ("remote url() load", @"url\(\s*[""']?(https?:|//)"),
        ];

        return [.. patterns
            .Where(entry => System.Text.RegularExpressions.Regex.IsMatch(stripped, entry.Regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(entry => entry.Label)];
    }
}
