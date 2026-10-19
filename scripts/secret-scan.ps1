# QtsBilling secret/configuration static scan (QB-14).
#
# Scans every git-tracked file for credential-shaped material: connection strings with real passwords,
# private keys, static-IV AES usage, and hardcoded API keys/tokens. Documented development fixtures are
# allowlisted; anything else fails the scan (exit code 1) so it can be reviewed before it is committed.
#
# Example:
#   powershell -File scripts/secret-scan.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $binaryExtensions = @(".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".zip", ".dump", ".dll", ".exe", ".woff", ".woff2", ".ttf", ".snap")
    $patterns = @(
        @{ Name = "connection-string password"; Regex = "(?i)(?<![A-Za-z0-9_])(password|pwd)\s*=\s*(?!(string\.Empty|String\.Empty|configuration\b|Environment\b|_))[^;""'\s]{6,}" },
        @{ Name = "hardcoded password literal"; Regex = "(?i)[A-Za-z0-9_]*(password|pwd)\s*[:=]\s*[""'][^""']{8,}[""']" },
        @{ Name = "private key block"; Regex = "-----BEGIN [A-Z ]*PRIVATE KEY-----" },
        @{ Name = "static-IV AES usage"; Regex = "(?i)\bAes\.Create\(|\bCipherMode\.(CBC|ECB)\b" },
        @{ Name = "hardcoded api key/secret"; Regex = "(?i)(api[_-]?key|secret[_-]?key|client[_-]?secret)\s*[:=]\s*[""'][A-Za-z0-9_\-]{12,}[""']" },
        @{ Name = "hardcoded token"; Regex = "(?i)\b(token|secret)\s*[:=]\s*[""'][A-Za-z0-9_\-+/=]{24,}[""']" }
    )
    $allowlist = @(
        @{ Name = "dev database password (documented fixture)"; Regex = "(?i)password\s*=\s*qtsbilling\b" },
        @{ Name = "dev account fixture (documented)"; Regex = "QtsBilling!Dev2026" },
        @{ Name = "reset-ticket placeholder (docs)"; Regex = "(?i)token.{0,40}(header|response|ticket)" }
    )

    $files = git ls-files
    $findings = @()
    $scanned = 0

    foreach ($file in $files) {
        $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()

        if ($binaryExtensions -contains $extension) { continue }
        if (-not (Test-Path $file)) { continue }

        $scanned++
        $lineNumber = 0

        foreach ($line in (Get-Content -Path $file -ErrorAction SilentlyContinue)) {
            $lineNumber++

            foreach ($pattern in $patterns) {
                if ($line -match $pattern.Regex) {
                    $allowed = $false

                    foreach ($rule in $allowlist) {
                        if ($line -match $rule.Regex) { $allowed = $true; break }
                    }

                    $findings += [pscustomobject]@{
                        File    = $file
                        Line    = $lineNumber
                        Pattern = $pattern.Name
                        Allowed = $allowed
                        Text    = ($line.Trim().Substring(0, [Math]::Min(120, $line.Trim().Length)))
                    }
                }
            }
        }
    }

    Write-Output "scanned $scanned tracked files"

    $disallowed = @($findings | Where-Object { -not $_.Allowed })
    $allowed = @($findings | Where-Object { $_.Allowed })

    Write-Output "allowlisted development fixtures : $($allowed.Count)"

    foreach ($finding in $allowed) {
        Write-Output ("  allow | {0}:{1} | {2}" -f $finding.File, $finding.Line, $finding.Pattern)
    }

    if ($disallowed.Count -eq 0) {
        Write-Output "== secret scan PASSED: no unallowlisted credential material found =="
        exit 0
    }

    Write-Output "== secret scan FAILED: $($disallowed.Count) finding(s) need review =="

    foreach ($finding in $disallowed) {
        Write-Output ("  FAIL | {0}:{1} | {2} | {3}" -f $finding.File, $finding.Line, $finding.Pattern, $finding.Text)
    }

    exit 1
}
finally {
    Pop-Location
}
