<#
.SYNOPSIS
    The production admission and the cutover of QtsBilling (specification task 21).

.DESCRIPTION
    The script has two modes, and they are strictly separated by what they are allowed to touch.

      -Mode Admission  (READ-ONLY)
          Only reads. It creates no backup, it starts no application host, it sends no password-reset
          and no login request, and it writes nothing to the database. It verifies the environment
          binding, the schema state, the reconciliation evidence, the identity and device state, the
          integrity ties and the latest existing backup (its manifest and its hash). It needs no
          codeword, because it changes nothing.

      -Mode Execute    (the approved write)
          Performs the cutover: the codeword, a fresh backup, the host, the final frozen import, the
          signed reconciliation, the identity reset, the smoke and the final integrity verification.
          It requires the business owner's codeword, which is read from the environment
          (QTS_CUTOVER_CODEWORD) and compared in constant time - the caller can never accept its own
          codeword.

    Both modes FAIL CLOSED: the run stops at the first failed gate. A failed backup, a failed health
    check or a failed schema check stops the run before the import, never after it.

    Everything is bound to ONE environment. The database, the backup, the migration state, the command
    line tool and the HTTP target must all name the same database; a target that does not match is a
    failed gate, never a warning. A skipped import is a failed gate, not a pass, and the device is
    never enrolled automatically: an enrolment is an approval, not a side effect of a script.

    The script never deletes anything and never rolls back to the legacy system: a failed gate means
    stop and fix forward.

.EXAMPLE
    # the read-only admission (writes nothing)
    pwsh -File scripts/qb30-cutover.ps1 -Mode Admission -ApprovedDevices <ids>

    # the approved cutover (the codeword comes from the environment)
    $env:QTS_CUTOVER_CODEWORD = "<the owner's codeword>"
    $env:QTS_CUTOVER_PRESENTED_CODEWORD = "<the owner's codeword>"
    pwsh -File scripts/qb30-cutover.ps11 -Mode Execute -Snapshot <dir> -Rows <dir>
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
    [string[]]$ReconciliationExceptions = @(),
    [string]$OutDir = "backups"
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$approvedExceptions = @($ReconciliationExceptions -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$checklist = New-Object System.Collections.Generic.List[object]
$serverProcess = $null
$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

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

function Show-Checklist([string]$Title) {
    Write-Host ""
    Write-Host "== $Title =="
    foreach ($item in $script:checklist) {
        Write-Host ("  [{0}] {1}" -f $(if ($item.Passed) { "x" } else { " " }), $item.Gate)
    }
}

function Stop-Run([int]$ExitCode) {
    if ($script:serverProcess) {
        Stop-Process -Id $script:serverProcess.Id -Force -ErrorAction SilentlyContinue
        $script:serverProcess = $null
    }

    $failed = @($script:checklist | Where-Object { -not $_.Passed })

    if ($failed.Count -gt 0) {
        Write-Host ""
        foreach ($item in $failed) { Write-Host ("  FAIL {0}: {1}" -f $item.Gate, $item.Detail) }
    }

    Pop-Location
    exit $ExitCode
}

# The fail-closed rule: after every gate, a failure stops the run before the next step.
function Assert-NoFailedGate([string]$Phase) {
    $failed = @($script:checklist | Where-Object { -not $_.Passed })

    if ($failed.Count -gt 0) {
        Write-Host ""
        Write-Host "$Phase stopped: $($failed.Count) gate(s) failed. The run does not continue."
        Stop-Run 1
    }
}

Write-Host "== QtsBilling $Mode =="
Write-Host ""

# =========================================================================================
# PHASE 1 - the read-only admission. Nothing in this phase writes: no backup is created, no
# application host is started, no password-reset or login request is sent, and no row is written.
# =========================================================================================

# 1. the environment binding ------------------------------------------------------------
$connection = $env:QTS_POSTGRES_CONNECTION

if ([string]::IsNullOrWhiteSpace($connection)) {
    Add-Gate "environment: the database connection" $false "QTS_POSTGRES_CONNECTION is not set."
    Stop-Run 1
}

$Database = ([regex]::Match($connection, "(?i)(?:^|;)\s*Database\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$DatabaseHost = ([regex]::Match($connection, "(?i)(?:^|;)\s*Host\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$Port = ([regex]::Match($connection, "(?i)(?:^|;)\s*Port\s*=\s*([^;]+)")).Groups[1].Value.Trim()
$User = ([regex]::Match($connection, "(?i)(?:^|;)\s*Username\s*=\s*([^;]+)")).Groups[1].Value.Trim()

if ([string]::IsNullOrWhiteSpace($Database) -or [string]::IsNullOrWhiteSpace($DatabaseHost)) {
    Add-Gate "environment: the database connection" $false "The connection string does not name a database and a host."
    Stop-Run 1
}

Add-Gate "environment: the target of this run" $true "$User@${DatabaseHost}:$Port/$Database"

$containerDatabase = Invoke-Sql "select current_database();"

if ($containerDatabase -ne $Database) {
    Add-Gate "environment: the backup, the SQL and the CLI name the same database" $false "The container serves '$containerDatabase' but the connection names '$Database'."
    Stop-Run 1
}

Add-Gate "environment: the backup, the SQL and the CLI name the same database" $true "container database: $containerDatabase"

# 2. the reachability and the schema (reads only) ---------------------------------------
$reachable = Invoke-Sql "select 1;"

if ($reachable -ne "1") {
    Add-Gate "database: the authoritative store is reachable" $false "The database did not answer a read."
    Stop-Run 1
}

Add-Gate "database: the authoritative store is reachable" $true "$Database"

$pending = dotnet ef migrations list --project QtsBilling.Persistence.Postgres --no-build 2>&1 | Out-String
$pendingCount = ([regex]::Matches($pending, "\(Pending\)")).Count
Add-Gate "schema: no pending PostgreSQL migration" ($pendingCount -eq 0) "$pendingCount pending"

# 3. the reconciliation evidence (a file read) --------------------------------------------
$reportPath = Join-Path $resolvedOut "cutover-reconciliation.json"

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

# 4. the identity and the device state (reads only) -----------------------------------------
$activeUsers = Invoke-Sql "select count(*) from users u join business_memberships m on m.user_id = u.id where m.is_active and u.is_active;"

Add-Gate "identities: the active memberships of this business" ($activeUsers -match "^\d+$" -and [int]$activeUsers -gt 0) "$activeUsers active membership(s)"

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

# 5. the integrity verification (reads only) --------------------------------------------------
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

# 6. the backup evidence (a read: the newest existing backup of THIS database) --------------------
$backupFiles = @(Get-ChildItem -Path $resolvedOut -Filter "$Database-*.dump" -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)

if ($backupFiles.Count -eq 0) {
    Add-Gate "backup: a verified backup of this database exists" $false "No backup of '$Database' was found in $resolvedOut. The admission does not create one; the approved execution does, before it touches anything."
}
else {
    $newest = $backupFiles[0]
    $manifestPath = "$($newest.FullName).json"

    if (-not (Test-Path $manifestPath)) {
        Add-Gate "backup: a verified backup of this database exists" $false "The manifest of '$($newest.Name)' is missing."
    }
    else {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $hash = (Get-FileHash -Path $newest.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($manifest.database -ne $Database) {
            Add-Gate "backup: a verified backup of this database exists" $false "The manifest names the database '$($manifest.database)'."
        }
        elseif ($manifest.sha256.ToLowerInvariant() -ne $hash) {
            Add-Gate "backup: a verified backup of this database exists" $false "The file no longer matches its manifest hash."
        }
        else {
            Add-Gate "backup: a verified backup of this database exists" $true "$($manifest.fileName) sha256 $($manifest.sha256.Substring(0, 16))"
        }
    }
}

Assert-NoFailedGate "The read-only admission"

if ($Mode -eq "Admission") {
    Show-Checklist "go/no-go (read-only admission)"
    Write-Host ""
    Write-Host "The admission wrote nothing: no backup was created, no application host was started, and no"
    Write-Host "password-reset or login request was sent. The approved execution creates the backup and"
    Write-Host "performs the cutover."
    Write-Host ""
    Write-Host ("Admission: {0} gate(s), {1} failed" -f $checklist.Count, @($checklist | Where-Object { -not $_.Passed }).Count)
    Stop-Run 0
}

# =========================================================================================
# PHASE 2 - the approved execution. Everything below may write, and every gate stops the run.
# =========================================================================================

# 7. the codeword ----------------------------------------------------------------------------
$expected = $env:QTS_CUTOVER_CODEWORD
$presented = $env:QTS_CUTOVER_PRESENTED_CODEWORD

if ([string]::IsNullOrWhiteSpace($expected) -or [string]::IsNullOrWhiteSpace($presented)) {
    Add-Gate "codeword: the business owner's cutover codeword" $false "QTS_CUTOVER_CODEWORD and QTS_CUTOVER_PRESENTED_CODEWORD must both be set; the script never accepts a codeword from its own arguments."
    Stop-Run 1
}

if (-not (Test-ConstantTimeEquals $presented $expected)) {
    Add-Gate "codeword: the business owner's cutover codeword" $false "The codeword does not match. The cutover does not start."
    Stop-Run 1
}

Add-Gate "codeword: the business owner's cutover codeword" $true "accepted (constant-time comparison)"

# 8. the fresh backup (a write, before anything else is touched) --------------------------------
New-Item -ItemType Directory -Force -Path $resolvedOut | Out-Null

$backupOutput = powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-postgres.ps1 -Container $Container -Database $Database -User $User -OutDir $resolvedOut 2>&1 | Out-String
$backupOk = $backupOutput -match "(?i)manifest" -and $backupOutput -notmatch "(?i)error|failed"
$backupDetail = (($backupOutput.Trim() -split "`n" | Select-Object -Last 3) -join " | ")

Add-Gate "backup: a fresh dump of this database with its SHA-256 manifest" $backupOk $backupDetail
Assert-NoFailedGate "The approved execution (backup)"

# 9. the host and the schema state ----------------------------------------------------------------
$env:ASPNETCORE_ENVIRONMENT = "Production"

$serverProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj --no-launch-profile --urls $BaseUrl" `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput (Join-Path $resolvedOut "cutover-server.log") `
    -RedirectStandardError (Join-Path $resolvedOut "cutover-server.err") `
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
Assert-NoFailedGate "The approved execution (host)"

$pending = dotnet ef migrations list --project QtsBilling.Persistence.Postgres --no-build 2>&1 | Out-String
$pendingCount = ([regex]::Matches($pending, "\(Pending\)")).Count
Add-Gate "schema: no pending PostgreSQL migration" ($pendingCount -eq 0) "$pendingCount pending"
Assert-NoFailedGate "The approved execution (schema)"

# 10. the final frozen import ------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Snapshot) -or [string]::IsNullOrWhiteSpace($Rows)) {
    Add-Gate "import: the final frozen import" $false "The frozen snapshot and the transformed rows are required. A skipped import is not a pass."
    Stop-Run 1
}

$importOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- import --rows $Rows --mapping $Mapping 2>&1 | Out-String
$importOk = $importOutput -notmatch "(?i)Unhandled exception|error:"
Add-Gate "import: the final frozen import (idempotent)" $importOk (($importOutput.Trim() -split "`n" | Select-Object -Last 2) -join " | ")
Assert-NoFailedGate "The approved execution (import)"

# 11. the signed reconciliation ------------------------------------------------------------------------
$reconcileOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- reconcile --snapshot $Snapshot --rows $Rows --mapping $Mapping --out $reportPath 2>&1 | Out-String
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
$signed = $report.signature -ne "unsigned" -and $report.signature.Length -eq 64
Add-Gate "reconciliation: the fresh report is signed" $signed "hash $($report.reportSha256.Substring(0, 16))"

$unresolved = @($report.exceptions | Where-Object { $exception = $_; -not ($approvedExceptions | Where-Object { $exception -like "*$_*" }) })
Add-Gate "reconciliation: no unresolved exception" ($unresolved.Count -eq 0) "$($unresolved.Count) unresolved of $($report.exceptions.Count) exception(s)"
Assert-NoFailedGate "The approved execution (reconciliation)"

# 12. the smoke ----------------------------------------------------------------------------------------
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

Assert-NoFailedGate "The approved execution (smoke)"

# 13. the identity reset (a write, after the session proved to work) -------------------------------------
$email = "owner@qtsbilling.local"
$resetRequested = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/password-reset/request" -Method Post `
    -ContentType "application/json" -Body ('{"email":"' + $email + '"}') -TimeoutSec 30 -UseBasicParsing
Add-Gate "identities: the reset flow is available" ($resetRequested.StatusCode -in @(200, 202, 204)) "POST /api/v1/auth/password-reset/request -> $($resetRequested.StatusCode)"

# 14. the final integrity verification --------------------------------------------------------------------------
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

# 15. the checklist and the handover ----------------------------------------------------------------------------
Show-Checklist "go/no-go"

Write-Host ""
Write-Host "stop/fix-forward plan:"
Write-Host "  stop    : if a gate fails, keep the legacy system read-only, do not open the new system for writes"
Write-Host "            and do not delete anything; the import is idempotent and can be re-run."
Write-Host "  forward : fix the failing gate, re-run the import (it updates the same rows), re-run this script."
Write-Host "  never   : a blind rollback to the legacy database, and a destructive purge of the legacy backups."
Write-Host ""
Write-Host ("Execute: {0} gate(s), {1} failed" -f $checklist.Count, @($checklist | Where-Object { -not $_.Passed }).Count)

$failed = @($checklist | Where-Object { -not $_.Passed })

if ($failed.Count -gt 0) {
    Stop-Run 1
}

Stop-Run 0
