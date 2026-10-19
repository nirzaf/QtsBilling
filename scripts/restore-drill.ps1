# QtsBilling PostgreSQL restore drill (QB-14).
#
# Backs up the live database, restores it into a disposable database and reconciles the two: row counts
# for the authoritative tables plus the journal debit/credit totals must match exactly. The disposable
# database is dropped afterwards (pass -Keep to inspect it). Exit code 0 = drill passed.
#
# Example:
#   powershell -File scripts/restore-drill.ps1

[CmdletBinding()]
param(
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$User = "qtsbilling",
    [string]$DrillDatabase = "qtsbilling_restore_drill",
    [string]$OutDir = "backups",
    [switch]$Keep
)

$ErrorActionPreference = "Stop"

$reconcileSql = @"
SELECT 'businesses', COUNT(*) FROM businesses
UNION ALL SELECT 'customers', COUNT(*) FROM customers
UNION ALL SELECT 'products', COUNT(*) FROM products
UNION ALL SELECT 'product_variants', COUNT(*) FROM product_variants
UNION ALL SELECT 'sales', COUNT(*) FROM sales
UNION ALL SELECT 'sale_lines', COUNT(*) FROM sale_lines
UNION ALL SELECT 'payments', COUNT(*) FROM payments
UNION ALL SELECT 'journal_entries', COUNT(*) FROM journal_entries
UNION ALL SELECT 'journal_lines', COUNT(*) FROM journal_lines
UNION ALL SELECT 'journal_posted_debit_total', COALESCE(SUM(l.debit_amount), 0)::numeric(28,4) FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id WHERE e.status = 'Posted'
UNION ALL SELECT 'journal_posted_credit_total', COALESCE(SUM(l.credit_amount), 0)::numeric(28,4) FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id WHERE e.status = 'Posted'
UNION ALL SELECT 'invoices', COUNT(*) FROM invoices
UNION ALL SELECT 'credit_notes', COUNT(*) FROM credit_notes
UNION ALL SELECT 'purchase_orders', COUNT(*) FROM purchase_orders
UNION ALL SELECT 'purchase_receipts', COUNT(*) FROM purchase_receipts
UNION ALL SELECT 'expenses', COUNT(*) FROM expenses
UNION ALL SELECT 'audit_events', COUNT(*) FROM audit_events
UNION ALL SELECT 'identity_users', COUNT(*) FROM users
ORDER BY 1
"@

function Query-Reconciliation([string]$TargetDatabase) {
    $raw = docker exec $Container psql --username $User --dbname $TargetDatabase --tuples-only --no-align --field-separator "|" --command $reconcileSql
    $exit = $LASTEXITCODE

    if ($exit -ne 0) {
        throw "Reconciliation query failed against '$TargetDatabase' (exit $exit)."
    }

    $map = @{}

    foreach ($line in $raw) {
        $trimmed = "$line".Trim()

        if ($trimmed.Length -eq 0) { continue }

        $parts = $trimmed -split "\|"

        if ($parts.Count -eq 2) {
            $map[$parts[0]] = $parts[1]
        }
    }

    if (-not $map.ContainsKey("journal_posted_debit_total") -or $map.Count -lt 10) {
        throw "Reconciliation against '$TargetDatabase' produced an incomplete result ($($map.Count) values); refusing to call the drill a pass."
    }

    return $map
}

$backupScript = Join-Path $PSScriptRoot "backup-postgres.ps1"
$restoreScript = Join-Path $PSScriptRoot "restore-postgres.ps1"

Write-Output "== QB-14 restore drill: $Database -> $DrillDatabase =="
Write-Output "---- step 1: backup the live database"

$backupOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $backupScript -Container $Container -Database $Database -User $User -OutDir $OutDir 2>&1
$backupOutput | ForEach-Object { "  $_" }

$backupLine = $backupOutput | Where-Object { $_ -match "^backup file" } | Select-Object -First 1

if (-not $backupLine) {
    throw "Backup step did not report a file path."
}

$backupFile = ($backupLine -split ":", 2)[1].Trim()
Write-Output "---- step 2: restore into the disposable database"

& powershell -NoProfile -ExecutionPolicy Bypass -File $restoreScript -BackupFile $backupFile -TargetDatabase $DrillDatabase -Container $Container -User $User -Force 2>&1 | ForEach-Object { "  $_" }

Write-Output "---- step 3: reconcile source against restore"

$source = Query-Reconciliation $Database
$target = Query-Reconciliation $DrillDatabase

$failures = 0

foreach ($key in ($source.Keys | Sort-Object)) {
    $sourceValue = $source[$key]
    $targetValue = $target[$key]
    $ok = $sourceValue -eq $targetValue

    if (-not $ok) { $failures++ }

    Write-Output ("  {0,-20} source={1,-14} restored={2,-14} {3}" -f $key, $sourceValue, $targetValue, $(if ($ok) { "match" } else { "MISMATCH" }))
}

Write-Output "---- step 4: cleanup"

if ($Keep) {
    Write-Output "  keeping '$DrillDatabase' for inspection (-Keep)"
}
else {
    docker exec $Container dropdb --username $User --force $DrillDatabase | ForEach-Object { "  $_" }
    Write-Output "  dropped '$DrillDatabase'"
}

if ($failures -gt 0) {
    Write-Output "== drill FAILED: $failures mismatched values =="
    exit 1
}

Write-Output "== drill PASSED: backup restored into a disposable database and reconciled =="
exit 0
