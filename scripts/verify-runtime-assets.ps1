# QtsBilling runtime asset audit (self-hosted SMB completion, specification task 10).
#
# The self-hosted profile must render on a machine with no internet access at all. This scan reads the
# shipped shells and styles only: it fails when a page or stylesheet would make the browser fetch a font,
# icon, script, image or stylesheet from a public network. Documentation and licence links inside comments
# or text are deliberately not failures, because nothing loads them. No network request is made by this
# script itself.
#
# Example:
#   pwsh -NoProfile -File scripts/verify-runtime-assets.ps1
#   pwsh -NoProfile -File scripts/verify-runtime-assets.ps1 -Path Some/File.html   # scan an extra target

[CmdletBinding()]
param(
    [string[]]$Path = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # The runtime surfaces a browser or WebView2 actually loads, plus the shared component styles. A named
    # target that disappears is itself a failure: it means the shipped shell moved, not that it is clean.
    $targets = @(
        "QtsWebApp/QtsWebApp/Components/App.razor",
        "QtsWebApp/QtsWebApp.Client/Routes.razor",
        "QtsBilling.Maui/wwwroot/index.html",
        "QtsBilling.Maui/Components/Routes.razor"
    )

    $rootLength = (Get-Location).Path.Length + 1

    foreach ($pattern in @("wwwroot/*.css", "wwwroot/**/*.css", "*.razor.css", "wwwroot/*.js")) {
        $found = Get-ChildItem -Path "QtsWebApp", "QtsBilling.Ui", "QtsBilling.Maui" -Recurse -File -Include $pattern -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch "\\(bin|obj)\\|\\_framework\\|\.br$" } |
            ForEach-Object { $_.FullName.Substring($rootLength).Replace("\", "/") }
        $targets += $found
    }

    $targets += $Path
    $scanTargets = @($targets | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique | Where-Object { Test-Path $_ })

    $missing = @($targets | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not (Test-Path $_) })
    if ($missing.Count -gt 0) {
        Write-Output "== runtime asset audit FAILED: a named target does not exist =="
        $missing | ForEach-Object { Write-Output "  MISSING | $_" }
        exit 1
    }

    # Only references the engine would fetch. A data: URI, an XML namespace inside inline SVG, or a link a
    # human reads in a comment is not a network dependency.
    $rules = @(
        @{ Name = "remote stylesheet link"; Regex = '(?i)<link[^>]+href\s*=\s*["'']?(https?:|//)' },
        @{ Name = "remote script"; Regex = '(?i)<script[^>]+src\s*=\s*["'']?(https?:|//)' },
        @{ Name = "remote image"; Regex = '(?i)<img[^>]+src\s*=\s*["'']?(https?:)' },
        @{ Name = "remote font or icon href"; Regex = '(?i)@font-face[^}]{0,200}src\s*:\s*(?:local\([^)]*\)\s*,\s*)?url\(\s*["'']?(https?:|//)' },
        @{ Name = "remote CSS import"; Regex = '(?i)@import\s+(?:url\(\s*)?["'']?(https?:|//)' },
        @{ Name = "remote url() load"; Regex = '(?i)url\(\s*["'']?(https?:|//)' },
        @{ Name = "prefetch or preconnect hint"; Regex = '(?i)<link[^>]+rel\s*=\s*["''](?:preconnect|dns-prefetch|preload|prefetch)["'']' }
    )

    $findings = @()
    $scanned = 0

    foreach ($file in $scanTargets) {
        $scanned++
        $lineNumber = 0

        foreach ($line in (Get-Content -LiteralPath $file -ErrorAction SilentlyContinue)) {
            $lineNumber++
            $probe = $line.TrimStart()

            # An HTML comment or a CSS comment documents; it never loads. Strip them before matching.
            $probe = [regex]::Replace($probe, '(?s)<!--.*?-->', ' ')
            $probe = [regex]::Replace($probe, '(?s)/\*.*?\*/', ' ')
            if ($probe.Trim().StartsWith("//") -or $probe.Trim().StartsWith("#")) { continue }

            foreach ($rule in $rules) {
                if ($probe -match $rule.Regex) {
                    $snippet = $probe.Trim()
                    if ($snippet.Length -gt 120) { $snippet = $snippet.Substring(0, 120) + "…" }

                    $findings += [pscustomobject]@{
                        File    = $file
                        Line    = $lineNumber
                        Rule    = $rule.Name
                        Snippet = $snippet
                    }
                }
            }
        }
    }

    Write-Output "scanned $scanned shipped runtime asset(s)"

    if ($findings.Count -eq 0) {
        Write-Output "== runtime asset audit PASSED: no page or stylesheet fetches a public resource =="
        exit 0
    }

    Write-Output "== runtime asset audit FAILED: $($findings.Count) remote loadable reference(s) =="

    foreach ($finding in $findings) {
        Write-Output ("  FAIL | {0}:{1} | {2} | {3}" -f $finding.File, $finding.Line, $finding.Rule, $finding.Snippet)
    }

    exit 1
}
finally {
    Pop-Location
}
