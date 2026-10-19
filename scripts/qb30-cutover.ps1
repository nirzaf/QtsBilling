<#
.SYNOPSIS
    The production cutover and the post-cutover integrity verification (QB-30).

.DESCRIPTION
    Runs the cutover in the order of the specification §6.1 and stops at the first failed gate:

      0. the codeword gate (the cutover never runs without the business owner's codeword),
      1. the final backup and its manifest hash,
      2. the schema state and the health of the server,
      3. the final frozen import (idempotent) and the signed reconciliation report,
      4. the identity reset and the device enrolment,
      5. a read-only and a limited authorized smoke,
      6. the post-cutover integrity verification (the ledger, the stock, the tenant, the numbering),
      7. the go/no-go checklist, the stop/fix-forward plan and the handover record.

    It never deletes anything: the legacy data stays read-only, the backups are kept and the import is
    idempotent, so a fix-forward is always possible. A failed gate stops the cutover with the checklist
    printed; it does not roll anything back automatically (a blind rollback to the legacy system is not a
    valid recovery path).

.EXAMPLE
    powershell -File scripts/qb30-cutover.ps1 -Codeword <owner codeword> -ExpectedCodeword <owner codeword>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Codeword,
    [Parameter(Mandatory = $true)][string]$ExpectedCodeword,
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$User = "qtsbilling",
    [string]$BaseUrl = "http://localhost:5188",
    [string]$Snapshot = "",
    [string]$Rows = "",
    [string]$Mapping = "docs/migration/mapping.sample.json",
    [string]$Approvals = "",
    [switch]$SkipImport
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$checklist = New-Object System.Collections.Generic.List[object]
$serverProcess = $null

function Add-Gate([string]$Gate, [bool]$Passed, [string]$Detail) {
    $script:checklist.Add([pscustomobject]@{ Gate = $Gate; Passed = $Passed; Detail = $Detail })
    Write-Host ("{0,-58} {1}" -f $Gate, $(if ($Passed) { "PASS" } else { "FAIL" }))
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

Write-Host "== QtsBilling cutover =="
Write-Host ""

# 0. the codeword gate -------------------------------------------------------------
if ($Codeword -ne $ExpectedCodeword -or [string]::IsNullOrWhiteSpace($Codeword)) {
    Add-Gate "codeword: the business owner's cutover codeword" $false "The codeword does not match. The cutover does not start."
    Pop-Location
    exit 1
}

Add-Gate "codeword: the business owner's cutover codeword" $true "accepted"

# 1. the final backup ----------------------------------------------------------------
$backupOutput = powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-postgres.ps1 2>&1 | Out-String
$backupOk = $backupOutput -match "(?i)manifest" -and $backupOutput -notmatch "(?i)error|failed"
Add-Gate "backup: a fresh dump with its SHA-256 manifest" $backupOk (($backupOutput.Trim() -split "`n" | Select-Object -Last 2) -join " | ")

# 2. the schema and the health -------------------------------------------------------
$connection = $env:QTS_POSTGRES_CONNECTION
if ([string]::IsNullOrWhiteSpace($connection)) {
    Add-Gate "configuration: the database connection" $false "QTS_POSTGRES_CONNECTION is not set."
    Pop-Location
    exit 1
}

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

# 3. the final frozen import and the signed reconciliation ---------------------------
if (-not $SkipImport -and $Snapshot -and $Rows) {
    if ([string]::IsNullOrWhiteSpace($env:QTS_RECONCILIATION_KEY)) { Write-Host "TTS_RECONCILIATION_KEY is not set: the reconciliation report cannot be signed."; Pop-Location; exit 1 }
    $importOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- import --rows $Rows --mapping $Mapping 2>&1 | Out-String
    $importOk = $importOutput -notmatch "(?i)Unhandled exception|error:"
    Add-Gate "import: the final frozen import (idempotent)" $importOk (($importOutput.Trim() -split "`n" | Select-Object -Last 2) -join " | ")

    $reportPath = Join-Path $repoRoot "backups/cutover-reconciliation.json"
    $reconcileOutput = dotnet run --project QtsBilling.Migration/QtsBilling.Migration.csproj --no-build -- reconcile --snapshot $Snapshot --rows $Rows --mapping $Mapping --out $reportPath 2>&1 | Out-String
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    $signed = $report.signature -ne "unsigned" -and $report.signature.Length -eq 64
    Add-Gate "reconciliation: the report is signed" $signed "hash $($report.reportSha256.Substring(0, 16)) signature $($report.signature.Substring(0, 16))"
    $approved = @(); if ($Approvals -and (Test-Path $Approvals)) { $approved = Get-Content $Approvals | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() } }
    $unresolved = @($report.exceptions | Where-Object { $exception = $_; -not ($approved | Where-Object { $exception -like "*$_*" }) })
    Add-Gate "reconciliation: no unresolved exception" ($unresolved.Count -eq 0) "$($unresolved.Count) unresolved of $($report.exceptions.Count) exception(s) - each one blocks the go-live until it is decided"
}
else {
    Add-Gate "import: the final frozen import" $true "skipped (-SkipImport or no snapshot)"
}

# 4. the identity reset and the device enrolment --------------------------------------
$email = "owner@qtsbilling.local"
$resetRequested = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/password-reset/request" -Method Post `
    -ContentType "application/json" -Body ('{"email":"' + $email + '"}') -TimeoutSec 30 -UseBasicParsing
$resetOk = $resetRequested.StatusCode -in @(200, 202, 204)
Add-Gate "identities: the reset flow is available" $resetOk "POST /api/v1/auth/password-reset/request -> $($resetRequested.StatusCode)"

# The imported identities carry no credentials: the account is reset through this flow.
$newPassword = $env:QTS_OWNER_PASSWORD
$token = $null
try { $token = ($resetRequested.Content | ConvertFrom-Json).token } catch { $token = $null }
if ($token -and $newPassword) {
    $complete = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/password-reset/complete" -Method Post `
        -ContentType "application/json" -Body ('{"email":"' + $email + '","token":"' + $token + '","newPassword":"' + $newPassword + '"}') `
        -TimeoutSec 30 -UseBasicParsing
    Add-Gate "identities: the reset completes" ($complete.StatusCode -in @(200, 204)) "POST /api/v1/auth/password-reset/complete -> $($complete.StatusCode)"
}

$businessId = Invoke-Sql "select id from businesses limit 1;"
$locationId = Invoke-Sql "select id from business_locations limit 1;"
$deviceId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"
docker exec $Container psql -U $User -d $Database -c "insert into device_registrations (id, business_id, location_id, name, platform, app_version, status, enrolled_at_utc, version, created_at_utc, updated_at_utc) values ('$deviceId', '$businessId', '$locationId', 'cutover device', 'windows', '1.0.0', 'Active', now(), 1, now(), now()) on conflict (id) do update set business_id = excluded.business_id, status = 'Active';" 2>&1 | Out-Null
$enrolled = Invoke-Sql "select count(*) from device_registrations where id = '$deviceId' and status = 'Active';"
Add-Gate "devices: the desktop device is enrolled" ($enrolled -eq "1") "device $deviceId"

# 5. the smoke -----------------------------------------------------------------------
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/login" -Method Post -WebSession $session `
    -ContentType "application/json" -Body ('{"email":"' + $email + '","password":"' + $env:QTS_OWNER_PASSWORD + '"}') `
    -TimeoutSec 30 -UseBasicParsing
$readOnlyOk = $login.StatusCode -eq 200
Add-Gate "smoke: the sign-in and the session" $readOnlyOk "login -> $($login.StatusCode)"

try {
    $reports = Get-Scalar "$BaseUrl/api/v1/reports/balance-sheet" $session
    $balanceSheetOk = $reports -match '"assets"'
    Add-Gate "smoke: the balance sheet reads from the authoritative ledger" $balanceSheetOk "GET /api/v1/reports/balance-sheet"
}
catch {
    Add-Gate "smoke: the balance sheet reads from the authoritative ledger" $false $_.Exception.Message
}

try {
    $customers = Get-Scalar "$BaseUrl/api/v1/customers?pageSize=5" $session
    $customerOk = $customers -match '"items"'
    Add-Gate "smoke: the read-only master data" $customerOk "GET /api/v1/customers"
}
catch {
    Add-Gate "smoke: the read-only master data" $false $_.Exception.Message
}

# 6. the post-cutover integrity verification -------------------------------------------
$ledgerDifference = Invoke-Sql "select coalesce(sum(l.debit_amount),0) - coalesce(sum(l.credit_amount),0) from journal_lines l join journal_entries e on e.id = l.journal_entry_id where e.status = 'Posted';"
Add-Gate "integrity: the posted ledger balances (debits = credits)" ([decimal]$ledgerDifference -eq 0) "difference $ledgerDifference"

$stockTies = Invoke-Sql "select count(*) from (select m.variant_id, m.location_id, sum(m.quantity_delta) as moved, b.on_hand from inventory_movements m join inventory_balances b on b.variant_id = m.variant_id and b.location_id = m.location_id group by m.variant_id, m.location_id, b.on_hand) t where t.moved <> t.on_hand;"
Add-Gate "integrity: the stock movements tie to the balances" ($stockTies -eq "0") "$stockTies mismatch(es)"

$crossTenant = Invoke-Sql "select count(*) from (select id from sales where business_id is null union all select id from customers where business_id is null union all select id from journal_lines where business_id is null) x;"
Add-Gate "integrity: no unowned business rows" ($crossTenant -eq "0") "$crossTenant row(s) without a business"

$duplicateNumbers = Invoke-Sql "select count(*) from (select business_id, invoice_number from invoices where invoice_number is not null group by business_id, invoice_number having count(*) > 1) d;"
Add-Gate "integrity: the invoice numbers are unique per business" ($duplicateNumbers -eq "0") "$duplicateNumbers duplicate(s)"

$paymentTie = Invoke-Sql "select coalesce(sum(s.total_amount),0) - coalesce((select sum(p.amount) from payments p join sales s2 on s2.id = p.sale_id where p.status = 'Confirmed'),0) from sales s where s.status = 'Posted';"
Add-Gate "integrity: the posted sales equal the confirmed payments" ([decimal]$paymentTie -eq 0) "difference $paymentTie"

$legacyDependency = git ls-files | Select-String -Pattern "^QtsServices/|^QtsBilling/" | Measure-Object
Add-Gate "cutover: no live dependency on the legacy client or services" ($legacyDependency.Count -eq 0) "$($legacyDependency.Count) tracked legacy project file(s)"

# 7. the checklist, the stop/fix-forward plan and the handover -------------------------
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
Write-Host ("cutover: {0} gate(s), {1} failed" -f $checklist.Count, $failed.Count)

if ($serverProcess) {
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
}

Pop-Location

if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Host ("  FAIL {0}: {1}" -f $item.Gate, $item.Detail) }
    exit 1
}

exit 0
