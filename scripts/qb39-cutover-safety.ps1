# Proves the safety properties of the cutover script (specification task 21).
#
#   T1  the read-only admission writes NOTHING: no backup file, no application host, no
#       password-reset or login request (proved by the audit trail of the database).
#   T2  the admission fails closed when the environment does not match.
#   T3  the approved execution stops BEFORE the import when a gate fails.
#   T4  the approved execution reaches the import only after the backup, the health and the
#       schema gates have passed.
#
# The script needs a reachable PostgreSQL (QTS_POSTGRES_CONNECTION) and the container that serves it.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$temp = Join-Path $env:TEMP ("qts-cutover-safety-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$script = "scripts/qb30-cutover.ps1"
$failures = 0

function Check([string]$Name, [bool]$Passed, [string]$Detail) {
    if ($Passed) {
        Write-Host ("  PASS  {0}" -f $Name)
    }
    else {
        Write-Host ("  FAIL  {0} - {1}" -f $Name, $Detail)
        $script:failures++
    }
}

function Run-Cutover([string[]]$Arguments, [string]$OutDir) {
    # The child writes to a file, not to this pipeline: a background host that inherits the pipeline
    # handles would keep it open and the call would never return.
    $log = Join-Path $OutDir ("cutover-run-" + [guid]::NewGuid().ToString("N") + ".log")
    $process = Start-Process -FilePath "powershell" `
        -ArgumentList (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script) + $Arguments) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" `
        -WindowStyle Hidden -PassThru -Wait

    $output = ""
    if (Test-Path $log) { $output = Get-Content $log -Raw }
    if (Test-Path "$log.err") { $output += (Get-Content "$log.err" -Raw) }

    return [pscustomobject]@{ Output = $output; ExitCode = $process.ExitCode }
}

$connection = $env:QTS_POSTGRES_CONNECTION

if ([string]::IsNullOrWhiteSpace($connection)) {
    Write-Host "QTS_POSTGRES_CONNECTION is not set; the safety script needs a reachable PostgreSQL."
    Pop-Location
    exit 1
}

$Database = ([regex]::Match($connection, "(?i)(?:^|;)\s*Database\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$User = ([regex]::Match($connection, "(?i)(?:^|;)\s*Username\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$Container = "qtsbilling-pg"

function Invoke-Sql([string]$query) {
    $result = docker exec $Container psql -U $User -d $Database -t -A -c $query 2>&1
    return ($result | Where-Object { $_ -and $_ -notmatch "^\s*$" } | Select-Object -First 1)
}

Write-Host "== the cutover safety =="
Write-Host ""

# ---------------------------------------------------------------- T1: the admission writes nothing
$outDir = Join-Path $temp "admission"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$dumpsBefore = @(Get-ChildItem -Path $outDir -Filter "*.dump" -ErrorAction SilentlyContinue).Count
$auditBefore = Invoke-Sql "select count(*) from audit_events;"

$admission = Run-Cutover @("-Mode", "Admission", "-OutDir", $outDir, "-ApprovedDevices", "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", "-ReconciliationExceptions", "Customer/64e000000000000000000001") $outDir

$dumpsAfter = @(Get-ChildItem -Path $outDir -Filter "*.dump" -ErrorAction SilentlyContinue).Count
$auditAfter = Invoke-Sql "select count(*) from audit_events;"
$hostLog = Test-Path (Join-Path $outDir "cutover-server.log")

Check "T1a the admission creates no backup" ($dumpsAfter -eq $dumpsBefore) "$dumpsBefore dumps before, $dumpsAfter after"
Check "T1b the admission starts no application host" (-not $hostLog) "the host log was created"
Check "T1c the admission sends no password-reset or login request" ($auditAfter -eq $auditBefore) "the audit trail grew from $auditBefore to $auditAfter"

# ---------------------------------------------------------------- T2: the admission fails closed
$env:QTS_POSTGRES_CONNECTION = "Host=localhost;Port=5433;Database=not_the_target_database;Username=$User;Password=qtsbilling"
$wrong = Run-Cutover @("-Mode", "Admission", "-OutDir", $outDir) $outDir
$env:QTS_POSTGRES_CONNECTION = $connection

Check "T2a a mismatched environment stops the run" ($wrong.ExitCode -ne 0) "exit code $($wrong.ExitCode)"
Check "T2b the environment gate is the one that failed" ($wrong.Output -match "environment: .*FAIL") "the output does not show a failed environment gate"

# ---------------------------------------------------------------- T3: the execution stops before the import
$env:QTS_POSTGRES_CONNECTION = "Host=localhost;Port=5433;Database=not_the_target_database;Username=$User;Password=qtsbilling"
$env:QTS_CUTOVER_CODEWORD = "the-owner-codeword"
$env:QTS_CUTOVER_PRESENTED_CODEWORD = "the-owner-codeword"

$stopped = Run-Cutover @("-Mode", "Execute", "-OutDir", $outDir, "-Snapshot", "x", "-Rows", "y") $outDir

Check "T3a a failed gate stops the execution" ($stopped.ExitCode -ne 0) "exit code $($stopped.ExitCode)"
Check "T3b the import is never reached" (-not ($stopped.Output -match "import: the final frozen import \(idempotent\)")) "the import gate ran"

$env:QTS_POSTGRES_CONNECTION = $connection

# ---------------------------------------------------------------- T4: the order of the gates
$env:QTS_RECONCILIATION_KEY = "safety-script-key"

$executeArguments = @("-Mode", "Execute")
if ($env:QTS_CUTOVER_DEVICES) { $executeArguments += @("-ApprovedDevices", $env:QTS_CUTOVER_DEVICES) }
if ($env:QTS_CUTOVER_APPROVED_EXCEPTIONS) { $executeArguments += @("-ReconciliationExceptions", $env:QTS_CUTOVER_APPROVED_EXCEPTIONS) }
if ($env:QTS_CUTOVER_SNAPSHOT) { $executeArguments += @("-Snapshot", $env:QTS_CUTOVER_SNAPSHOT) }
if ($env:QTS_CUTOVER_ROWS) { $executeArguments += @("-Rows", $env:QTS_CUTOVER_ROWS) }

$executed = Run-Cutover $executeArguments "backups"

$backupIndex = $executed.Output.IndexOf("backup: a fresh dump")
$healthIndex = $executed.Output.IndexOf("server: the health endpoint")
$schemaIndex = $executed.Output.IndexOf("schema: no pending PostgreSQL migration")
$importIndex = $executed.Output.IndexOf("import: the final frozen import (idempotent)")

Check "T4a the execution reached the import" ($importIndex -ge 0) "the import gate did not run (the run may have failed earlier: $($executed.ExitCode))"
Check "T4b the backup comes before the import" ($backupIndex -ge 0 -and $backupIndex -lt $importIndex) "backup at $backupIndex, import at $importIndex"
Check "T4c the health check comes before the import" ($healthIndex -ge 0 -and $healthIndex -lt $importIndex) "health at $healthIndex, import at $importIndex"
Check "T4d the schema check comes before the import" ($schemaIndex -ge 0 -and $schemaIndex -lt $importIndex) "schema at $schemaIndex, import at $importIndex"

Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("safety: {0} failure(s)" -f $failures)

Pop-Location

if ($failures -gt 0) { exit 1 }

exit 0
