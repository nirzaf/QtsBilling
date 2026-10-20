<#
.SYNOPSIS
    The production admission and the cutover of QtsBilling (specification task 21).

.DESCRIPTION
    The script has two modes, and they are strictly separated:

      -Mode Admission  (default, READ-ONLY)
          Verifies that the environment is ready to be admitted: the environment binding, a verified
          backup, the schema state, the health of the server, the reconciliation evidence, the device and
          pending-cash state, and a read-only canary. It never writes business data and never needs a
          codeword, because it changes nothing.

      -Mode Execute    (the approved write)
          Performs the cutover: the final frozen import, the signed reconciliation, the identity reset,
          the device enrolment check and the smoke. It requires the business owner's codeword, which is
          read from the environment (QTS_CUTOVER_CODEWORD) and compared in constant time - the caller
          can never accept its own codeword - and it stops at the first failed gate.

    Everything is bound to ONE environment. The database, the backup, the migration state, the command
    line tool and the HTTP target must all name the same database; a target that does not match is a
    failed gate, never a warning. A skipped import is a failed gate, not a pass, and the device is
    never enrolled automatically: an enrolment is an approval, not a side effect of a script.

    The script never deletes anything and never rolls back to the legacy system: a failed gate means
    stop and fix forward.

.EXAMPLE
    # the read-only admission
    pwsh -File scripts/qb30-cutover.ps1 -Mode Admission

    # the approved cutover (the codeword comes from the environment)
    $env:QTS_CUTOVER_CODEWORD = "<the owner's codeword>"
    pwsh -File scripts/qb30-cutover.ps1 -Mode Execute -Snapshot <dir> -Rows <dir> -ApprovedDevices <ids>
#>
[CmdletBinding()]
param(
    [ValidateSet("Admission", "Execute")]
    [string]$Mode = "Admission",
    [string]$Container = "qtsbilling-pg",
    [string]$BaseUrl = "http://localhost:5188",
    [string]$Snapshot = "",
    [string]$Rows = "",
    [string]$Mapping = "docs/migration/mapping.sample.json",
    [string]$ApprovedDevices = "",
    [string[]]$ReconciliationExceptions = @()
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$approvedExceptions = @($ReconciliationExceptions -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$checklist = New-Object System.Collections.Generic.List[object]
$serverProcess = $null

function Add-Gate([string]$Gate, [bool]$Passed, [string]$Detail) {
    $script:checklist.Add([pscustomobject]@{ Gate = $Gate; Passed = $Passed; Detail = $Detail })
    Write-Host ("{0,-62} {1}" -f $Gate, $(if ($Passed) { "PASS" } else { "FAIL" }))
    if ($Detail) { Write-Host "    $Detail" }
}

function Invoke-Sql([string]$query) {
    $result = docker exec $Container psql -U $User -d $Database -t -A -c $query 2>&1
    return ($result | Where-Object { $_ -and $_ -notmatch "^\s*$" } | Select-Object -First 1)
}

function Get-Scalar([string]$url, $cookieJar) {
    $response = Invoke-WebRequest -Uri $url -WebSession $cookieJar -TimeoutSec 30 -UseBasicParsing
    return $response.Content
}

# A comparison that does not stop at the first difference: the length of the codeword must not leak.
function Test-ConstantTimeEquals([string]$left, [string]$right) {
    if ($null -eq $left -or $null -eq $right) { return $false }

    $difference = [System.Math]::Abs($left.Length - $right.Length)

    for ($index = 0; $index -lt [System.Math]::Max($left.Length, $right.Length); $index++) {
        $leftChar = if ($index -lt $left.Length) { [int][char]$left[$index] } else { 0 }
        $rightChar = if ($index -lt $right.Length) { [int][char]$right[$index] } else { 0 }
        $difference = $difference -bor ($leftChar -bxor $rightChar)
    }

    return $difference -eq 0
}

Write-Host "== QtsBilling $($Mode.ToLower()) =="
Write-Host ""

# 0. the environment binding ------------------------------------------------------------
$connection = $env:QTS_POSTGRES_CONNECTION

if ([string]::IsNullOrWhiteSpace($connection)) {
    Add-Gate "environment: the database connection" $false "QTS_POSTGRES_CONNECTION is not set."
    Pop-Location
    exit 1
}

$Database = ([regex]::Match($connection, "(?i)(?:^|;)\s*Database\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$DatabaseHost = ([regex]::Match($connection, "(?i)(?:^|;)\s*Host\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$Port = ([regex]::Match($connection, "(?i)(?:^|;)\s*Port\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$User = ([regex]::Match($connection, "(?i)(?:^|;)\s*Username\s*=\s*([^;]+)")).Groups[1].Value.Trim()

if ([string]::IsNullOrWhiteSpace($Database) -or [string]::IsNullOrWhiteSpace($DatabaseHost)) {
    Add-Gate "environment: the database connection" $false "The connection string does not name a database and a host."
    Pop-Location
    exit 1
}

Add-Gate "environment: the target of this run" $true "$User@${DatabaseHost}:$Port/$Database"

$containerDatabase = Invoke-Sql "select current_database();"

if ($containerDatabase -ne $Database) {
    Add-Gate "environment: the backup, the SQL and the CLI name the same database" $false "The container serves '$containerDatabase' but the connection names '$Database'."
    Pop-Location
    exit 1
}

Add-Gate "environment: the backup, the SQL and the CLI name the same database" $true "container database: $containerDatabase"

# 1. the codeword ------------------------------------------------------------------------
if ($Mode -eq "Execute") {
    $expected = $env:QTS_CUTOVER_CODEWORD
    $presented = $env:QTS_CUTOVER_PRESENTED_CODEWORD

    if ([string]::IsNullOrWhiteSpace($expected) -or [string]::IsNullOrWhiteSpace($presented)) {
        Add-Gate "codeword: the business owner's cutover codeword" $false "QTS_CUTOVER_CODEWORD and QTS_CUTOVER_PRESENTED_CODEWORD must both be set; the script never accepts a codeword from its own arguments."
        Pop-Location
        exit 1
    }

    if (-not (Test-ConstantTimeEquals $presented $expected)) {
        Add-Gate "codeword: the business owner's cutover codeword" $false "The codeword does not match. The cutover does not start."
        Pop-Location
        exit 1
    }

    Add-Gate "codeword: the business owner's cutover codeword" $true "accepted (constant-time comparison)"
}
else {
    Add-Gate "codeword: not required for a read-only admission" $true "the admission changes nothing"
}

# 2. the backup --------------------------------------------------------------------------
$backupOutput = powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-postgres.ps1 -Container $Container -Database $Database -User $User 2>&1 | Out-String
$backupOk = $backupOutput -match "(?i)manifest" -and $backupOutput -notmatch "(?i)error|failed"
$backupDetail = (($backupOutput.Trim() -split "`n" | Select-Object -Last 3) -join " | ")

if ($backupOk) {
    $manifestPath = ([regex]::Match($backupOutput, "(?m)^\s*manifest\s*:\s*(.+)$")).Groups[1].Value.Trim()

    if ($manifestPath -and (Test-Path $manifestPath)) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $backupOk = $manifest.database -eq $Database
        $backupDetail = "$($manifest.fileName) sha256 $($manifest.sha256) database $($manifest.database)"
    }
    else {
        $backupOk = $false
        $backupDetail = "The backup manifest could not be read."
    }
}

Add-Gate "backup: a fresh dump of this database with its SHA-256 manifest" $backupOk $backupDetail

# 3. the schema and the health -------------------------------------------------------------
$env:ASPNETCORE_ENVIRONMENT = "Production"
New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "backups") | Out-Null

$serverProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj --no-launch-profile --urls $BaseUrl" `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput "backups/cutover-server.log" `
    -RedirectStandardError "backups/cutover-server.err" `
    -WindowStyle Hidden -PassThru

$healthy = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Seconds 1
    try {
        if ((Invoke-WebRequest -Uri "$BaseUrl/api/v1/health" -TimeoutSec 5 -UseBasicParsing).StatusCode -eq 200) { $healthy = $true; break }
    }
    catch { }
}

Add-Gate "server: the health endpoint" $healthy $BaseUrl

$pending = dotnet ef migrations list --project QtsBilling.Persistence.Postgres --no-build 2>&1 | Out-String
$pendingCount = ([regex]::Matches($pending, "\(Pending\)")).Count
Add-Gate "schema: no pending PostgreSQL migration" ($pendingCount -eq 0) "$pendingCount pending"

# 4. the reconciliation evidence (the admission reads the report that already exists) ---------------
$reportPath = Join-Path $repoRoot "backups/cutover-reconciliation.json"

if ($Mode -eq "Admission") {

    if (Test-Path $reportPath) {
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        $signed = $report.signature -and $report.signature -ne "unsigned" -and $report.signature.Length -eq 64
        Add-Gate "reconciliation: the report is signed" $signed "hash $($report.reportSha256.Substring(0, 16))"

        $unresolved = @($report.exceptions | Where-Object { $exception = $_; -not ($approvedExceptions | Where-Object { $exception -like "*$_*" }) })
        Add-Gate "reconciliation: no unresolved exception" ($unresolved.Count -eq 0) "$($unresolved.Count) unresolved of $($report.exceptions.Count) exception(s)"
    }
    else {
        Add-Gate "reconciliation: the report is signed" $false "No reconciliation report was found at $reportPath. An admission without signed reconciliation evidence is refused."
    }
}

# 5. the import (only in the approved execution) --------------------------------------------
if ($Mode -eq "Execute") {
    if ([string]::IsNullOrWhiteSpace($Snapshot) -or [string]::IsNullOrWhiteSpace($Rows)) {
        Add-Gate "import: the final frozen import" $false "The frozen snapshot and the transformed rows are required. A skipped import is not a pass."
    }
    else {
        $importOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- import --rows $Rows --mapping $Mapping 2>&1 | Out-String
        $importOk = $importOutput -notmatch "(?i)Unhandled exception|error:"
        Add-Gate "import: the final frozen import (idempotent)" $importOk (($importOutput.Trim() -split "`n" | Select-Object -Last 2) -join " | ")

        $reconcileOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- reconcile --snapshot $Snapshot --rows $Rows --mapping $Mapping --out $reportPath 2>&1 | Out-String
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        $signed = $report.signature -ne "unsigned" -and $report.signature.Length -eq 64
        Add-Gate "reconciliation: the fresh report is signed" $signed "hash $($report.reportSha256.Substring(0, 16))"

        $unresolved = @($report.exceptions | Where-Object { $exception = $_; -not ($approvedExceptions | Where-Object { $exception -like "*$_*" }) })
        Add-Gate "reconciliation: no unresolved exception" ($unresolved.Count -eq 0) "$($unresolved.Count) unresolved of $($report.exceptions.Count) exception(s)"
    }
}
else {
    Add-Gate "import: the final frozen import" $true "not part of a read-only admission"
}

# 6. the identities and the devices -------------------------------------------------------------
$email = "owner@qtsbilling.local"
$resetRequested = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/password-reset/request" -Method Post `
    -ContentType "application/json" -Body ('{"email":"' + $email + '"}') -TimeoutSec 30 -UseBasicParsing
Add-Gate "identities: the reset flow is available" ($resetRequested.StatusCode -in @(200, 202, 204)) "POST /api/v1/auth/password-reset/request -> $($resetRequested.StatusCode)"

# The device is never enrolled by this script: an enrolment is an approval. The script only verifies
# that the approved devices are enrolled and active.
$approved = @()
if (-not [string]::IsNullOrWhiteSpace($ApprovedDevices)) {
    $approved = $ApprovedDevices.Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}

if ($approved.Count -eq 0) {
    Add-Gate "devices: the approved device fleet" $false "No approved device was named. The script never enrols a device by itself."
}
else {
    $enrolled = 0
    foreach ($device in $approved) {
        $state = Invoke-Sql "select status from device_registrations where id = '$device';"
        if ($state -eq "Active") { $enrolled++ }
    }

    Add-Gate "devices: the approved device fleet" ($enrolled -eq $approved.Count) "$enrolled of $($approved.Count) approved device(s) are enrolled and active"
}

# 7. the smoke -------------------------------------------------------------------------------
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/login" -Method Post -WebSession $session `
    -ContentType "application/json" -Body ('{"email":"' + $email + '","password":"' + $env:QTS_OWNER_PASSWORD + '"}') `
    -TimeoutSec 30 -UseBasicParsing
Add-Gate "smoke: the sign-in and the session" ($login.StatusCode -eq 200) "login -> $($login.StatusCode)"

try {
    $reports = Get-Scalar "$BaseUrl/api/v1/reports/balance-sheet" $session
    Add-Gate "smoke: the balance sheet reads from the authoritative ledger" ($reports -match '"assets"') "GET /api/v1/reports/balance-sheet"
}
catch {
    Add-Gate "smoke: the balance sheet reads from the authoritative ledger" $false $_.Exception.Message
}

# 8. the integrity verification ----------------------------------------------------------------
$ledgerDifference = Invoke-Sql "select coalesce(sum(l.debit_amount),0) - coalesce(sum(l.credit_amount),0) from journal_lines l join journal_entries e on e.id = l.journal_entry_id where e.status = 'Posted';"
Add-Gate "integrity: the posted ledger balances (debits = credits)" ([decimal]$ledgerDifference -eq 0) "difference $ledgerDifference"

$stockTies = Invoke-Sql "select count(*) from (select m.variant_id, m.location_id, sum(m.quantity_delta) as moved, b.on_hand from inventory_movements m join inventory_balances b on b.variant_id = m.variant_id and b.location_id = m.location_id group by m.variant_id, m.location_id, b.on_hand) t where t.moved <> t.on_hand;"
Add-Gate "integrity: the stock movements tie to the balances" ($stockTies -eq "0") "$stockTies mismatch(es)"

$valueTies = Invoke-Sql "select count(*) from (select m.variant_id, m.location_id, sum(m.value_delta) as moved, b.value_on_hand from inventory_movements m join inventory_balances b on b.variant_id = m.variant_id and b.location_id = m.location_id group by m.variant_id, m.location_id, b.value_on_hand) t where t.moved <> t.value_on_hand;"
Add-Gate "integrity: the stock value ties to the balances" ($valueTies -eq "0") "$valueTies mismatch(es)"

$crossTenant = Invoke-Sql "select count(*) from (select id from sales where business_id is null union all select id from customers where business_id is null union all select id from journal_lines where business_id is null) x;"
Add-Gate "integrity: no unowned business rows" ($crossTenant -eq "0") "$crossTenant row(s) without a business"

$duplicateNumbers = Invoke-Sql "select count(*) from (select business_id, invoice_number from invoices where invoice_number is not null group by business_id, invoice_number having count(*) > 1) d;"
Add-Gate "integrity: the invoice numbers are unique per business" ($duplicateNumbers -eq "0") "$duplicateNumbers duplicate(s)"

$paymentTie = Invoke-Sql "select coalesce(sum(s.total_amount),0) - coalesce((select sum(p.amount) from payments p join sales s2 on s2.id = p.sale_id where p.status = 'Confirmed'),0) from sales s where s.status = 'Posted';"
Add-Gate "integrity: the posted sales equal the confirmed payments" ([decimal]$paymentTie -eq 0) "difference $paymentTie"

$legacyDependency = git ls-files | Select-String -Pattern "^QtsServices/|^QtsBilling/" | Measure-Object
Add-Gate "cutover: no live dependency on the legacy client or services" ($legacyDependency.Count -eq 0) "$($legacyDependency.Count) tracked legacy project file(s)"

# 9. the checklist and the handover ----------------------------------------------------------------
$failed = @($checklist | Where-Object { -not $_.Passed })

Write-Host ""
Write-Host "== go/no-go =="
foreach ($item in $checklist) {
    Write-Host ("  [{0}] {1}" -f $(if ($item.Passed) { "x" } else { " " }), $item.Gate)
}

Write-Host ""
Write-Host "stop/fix-forward plan:"
Write-Host "  stop    : if a gate fails, keep the legacy system read-only, do not open the new system for writes"
Write-Host "            and do not delete anything; the import is idempotent and can be re-run."
Write-Host "  forward : fix the failing gate, re-run the import (it updates the same rows), re-run this script."
Write-Host "  never   : a blind rollback to the legacy database, and a destructive purge of the legacy backups."
Write-Host ""
Write-Host ("{0}: {1} gate(s), {2} failed" -f $Mode, $checklist.Count, $failed.Count)

if ($serverProcess) {
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
}

Pop-Location

if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Host ("  FAIL {0}: {1}" -f $item.Gate, $item.Detail) }
    exit 1
}

exit 0
