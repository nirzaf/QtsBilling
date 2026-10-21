<#
.SYNOPSIS
    Fail-closed production admission and explicit cutover for QtsBilling (specification task 21).

.DESCRIPTION
    The script has two strictly separated modes.

      -Mode Admission  (READ-ONLY)
          Reads the signed approval manifest, the target database, the schema head, the signed native
          package and reconciliation evidence, the approved device fleet, the backup drill and the G7
          approval. It creates no backup, starts no host, sends no identity request and writes no row.
          It needs no codeword because it changes nothing.

      -Mode Execute    (the approved write)
          Re-verifies the same manifest immediately before any write, then checks ONE caller-supplied
          codeword against the SHA-256 in that signed manifest. The expected value is never a second
          caller-supplied environment value. It creates a fresh backup, starts the host, applies the
          frozen native package, signs and verifies the reconciliation, resets the owner identity,
          runs the smoke and repeats the integrity ties.

    Every gate stops the run at the first failure. External commands are checked for exit code and
    relevant stderr. A missing, skipped or unknown check is a failure, never a warning. The script
    never enrols a device, never rolls back to a retired system and never deletes posted facts.

    The manifest is one JSON document:
        { "schema": "qts-production-approval-v2",
          "body": { environment, server, database, businessIds, approvedDeviceIds, releaseSha,
                    migrationHead, nativePackageSha256, reconciliationReportSha256,
                    approvedExceptions, backupDrillSha256, g7ApprovalSha256,
                    codewordSha256, issuedAtUtc, expiresAtUtc },
          "approval": { "algorithm": "HMAC-SHA256", "keyId": "...", "signature": "..." } }

    The HMAC key and key id are read from QTS_CUTOVER_APPROVAL_KEY (hex) and
    QTS_CUTOVER_APPROVAL_KEY_ID. The codeword is read from QTS_CUTOVER_CODEWORD only in Execute.
    Neither secret is written to the checklist or to a log.

.EXAMPLE
    pwsh -File scripts/qb30-cutover.ps1 -Mode Admission -ApprovalManifest <signed.json> `
        -Package <dir> -Report <signed-report.json> -Approvals <owner-approvals.json> `
        -BackupDrill <drill.json> -G7Approval <g7.json> -OwnerEmail <owner@example> `
        -Container qtsbilling-pg -BaseUrl http://localhost:5188

.EXAMPLE
    $env:QTS_CUTOVER_CODEWORD = '<the owner-designated confirmation>'
    pwsh -File scripts/qb30-cutover.ps1 -Mode Execute ...same evidence...
#>
[CmdletBinding()]
param(
    [ValidateSet("Admission", "Execute")]
    [string]$Mode = "Admission",
    [string]$ApprovalManifest = "",
    [string]$Package = "",
    [string]$Report = "",
    [string]$Approvals = "",
    [string]$BackupDrill = "",
    [string]$G7Approval = "",
    [string]$Container = "",
    [string]$BaseUrl = "",
    [string]$OutDir = "backups",
    [ValidateSet("Docker", "Psql")]
    [string]$SqlMode = "Docker",
    [string]$OwnerEmail = "",
    [switch]$LibraryOnly
)

$ErrorActionPreference = "Stop"
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-CutoverLine([string]$Text) {
    Write-Host $Text
}

function Add-Gate([string]$Gate, [bool]$Passed, [string]$Detail = "") {
    $script:checklist.Add([pscustomobject]@{ Gate = $Gate; Passed = $Passed; Detail = $Detail })
    Write-Host ("{0,-72} {1}" -f $Gate, $(if ($Passed) { "PASS" } else { "FAIL" }))

    if ($Detail) {
        Write-Host "    $Detail"
    }
}

function Assert-Gate([string]$Gate, [bool]$Passed, [string]$Detail = "") {
    Add-Gate $Gate $Passed $Detail

    if (-not $Passed) {
        throw "GATE_FAILED: $Gate"
    }
}

function Add-Mutation([string]$Name) {
    if ($null -ne $script:mutations) {
        $script:mutations.Add($Name)
    }
}

function Show-Checklist([string]$Title) {
    Write-Host ""
    Write-Host "== $Title =="

    foreach ($item in $script:checklist) {
        Write-Host ("  [{0}] {1}" -f $(if ($item.Passed) { "x" } else { " " }), $item.Gate)
    }
}

function Test-ConstantTimeEquals([string]$Left, [string]$Right) {
    if ($null -eq $Left -or $null -eq $Right) {
        return $false
    }

    $difference = [Math]::Abs($Left.Length - $Right.Length)

    for ($index = 0; $index -lt [Math]::Max($Left.Length, $Right.Length); $index++) {
        $leftChar = if ($index -lt $Left.Length) { [int][char]$Left[$index] } else { 0 }
        $rightChar = if ($index -lt $Right.Length) { [int][char]$Right[$index] } else { 0 }
        $difference = $difference -bor ($leftChar -bxor $rightChar)
    }

    return $difference -eq 0
}

function Get-Sha256Hex([string]$Text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-FileSha256Hex([string]$Path) {
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-HmacHex([string]$Text, [string]$KeyHex) {
    $key = [Convert]::FromHexString($KeyHex)
    $hmac = [Security.Cryptography.HMACSHA256]::new($key)

    try {
        $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
        return [Convert]::ToHexString($hash).ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
    }
}

function Read-JsonFile([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -Path $Path -PathType Leaf)) {
        throw "The evidence file '$Path' does not exist."
    }

    return Get-Content -Path $Path -Raw | ConvertFrom-Json
}

function Get-ApprovalCanonicalText($Body) {
    $businessIds = @($Body.businessIds | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Sort-Object)
    $deviceIds = @($Body.approvedDeviceIds | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Sort-Object)
    $exceptions = @($Body.approvedExceptions | ForEach-Object { ([string]$_).Trim() } | Sort-Object)

    return @(
        "qts-production-approval-v2",
        [string]$Body.environment,
        [string]$Body.server,
        [string]$Body.database,
        ($businessIds -join ","),
        ($deviceIds -join ","),
        [string]$Body.releaseSha,
        [string]$Body.migrationHead,
        [string]$Body.nativePackageSha256,
        [string]$Body.reconciliationReportSha256,
        ($exceptions -join ","),
        [string]$Body.backupDrillSha256,
        [string]$Body.g7ApprovalSha256,
        [string]$Body.codewordSha256,
        (Format-CanonicalTimestamp $Body.issuedAtUtc),
        (Format-CanonicalTimestamp $Body.expiresAtUtc)
    ) -join "`n"
}

function Format-CanonicalTimestamp($Value) {
    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime().ToString("O")
    }

    if ($Value -is [DateTime]) {
        return ([DateTimeOffset]$Value).ToUniversalTime().ToString("O")
    }

    $parsed = [DateTimeOffset]::MinValue

    if ([DateTimeOffset]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed.ToUniversalTime().ToString("O")
    }

    return [string]$Value
}

function Test-ApprovalManifest([pscustomobject]$Context) {
    try {
        $manifest = Read-JsonFile $Context.ApprovalManifest
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Detail = $_.Exception.Message; Body = $null; Manifest = $null }
    }

    if ([string]$manifest.schema -ne "qts-production-approval-v2") {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval manifest schema is not qts-production-approval-v2."; Body = $null; Manifest = $manifest }
    }

    $body = $manifest.body

    if ($null -eq $body) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval manifest has no body."; Body = $null; Manifest = $manifest }
    }

    $keyId = $env:QTS_CUTOVER_APPROVAL_KEY_ID
    $keyHex = $env:QTS_CUTOVER_APPROVAL_KEY

    if ([string]::IsNullOrWhiteSpace($keyId) -or [string]::IsNullOrWhiteSpace($keyHex)) {
        return [pscustomobject]@{ Ok = $false; Detail = "QTS_CUTOVER_APPROVAL_KEY_ID and QTS_CUTOVER_APPROVAL_KEY must be supplied by the protected owner process."; Body = $body; Manifest = $manifest }
    }

    if ($null -eq $manifest.approval -or ([string]$manifest.approval.algorithm) -ne "HMAC-SHA256" -or ([string]$manifest.approval.keyId) -ne $keyId) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval signature algorithm or key id is not trusted."; Body = $body; Manifest = $manifest }
    }

    try {
        $canonical = Get-ApprovalCanonicalText $body
        $expected = Get-HmacHex $canonical $keyHex
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval key is not valid hexadecimal."; Body = $body; Manifest = $manifest }
    }

    if (-not (Test-ConstantTimeEquals ([string]$manifest.approval.signature).ToLowerInvariant() $expected)) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval manifest signature is invalid."; Body = $body; Manifest = $manifest }
    }

    if ([string]$body.environment -ne "production") {
        return [pscustomobject]@{ Ok = $false; Detail = "The signed environment is '$($body.environment)', not production."; Body = $body; Manifest = $manifest }
    }

    if (-not [string]::Equals([string]$body.server, $Context.DatabaseHost, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval names server '$($body.server)' but the command targets '$($Context.DatabaseHost)'."; Body = $body; Manifest = $manifest }
    }

    if (-not [string]::Equals([string]$body.database, $Context.Database, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval names database '$($body.database)' but the command targets '$($Context.Database)'."; Body = $body; Manifest = $manifest }
    }

    $issued = [DateTimeOffset]::MinValue
    $expires = [DateTimeOffset]::MinValue

    if (-not [DateTimeOffset]::TryParse([string]$body.issuedAtUtc, [ref]$issued) -or -not [DateTimeOffset]::TryParse([string]$body.expiresAtUtc, [ref]$expires)) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval timestamps are not ISO-8601."; Body = $body; Manifest = $manifest }
    }

    $now = [DateTimeOffset]::UtcNow

    if ($now -lt $issued -or $now -gt $expires) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval is not valid at the current time."; Body = $body; Manifest = $manifest }
    }

    foreach ($name in @("releaseSha", "migrationHead", "nativePackageSha256", "reconciliationReportSha256", "backupDrillSha256", "g7ApprovalSha256", "codewordSha256")) {
        if ([string]::IsNullOrWhiteSpace([string]$body.$name)) {
            return [pscustomobject]@{ Ok = $false; Detail = "The approval manifest is missing $name."; Body = $body; Manifest = $manifest }
        }
    }

    foreach ($hashName in @("nativePackageSha256", "reconciliationReportSha256", "backupDrillSha256", "g7ApprovalSha256", "codewordSha256")) {
        if ([string]$body.$hashName -notmatch "^[0-9a-fA-F]{64}$") {
            return [pscustomobject]@{ Ok = $false; Detail = "$hashName is not a SHA-256 hex value."; Body = $body; Manifest = $manifest }
        }
    }

    if (@($body.businessIds).Count -eq 0) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval names no business."; Body = $body; Manifest = $manifest }
    }

    if (@($body.approvedDeviceIds).Count -eq 0) {
        return [pscustomobject]@{ Ok = $false; Detail = "The approval names no approved device; the script never enrols one."; Body = $body; Manifest = $manifest }
    }

    return [pscustomobject]@{ Ok = $true; Detail = "signed approval for $($body.server)/$($body.database), expires $($body.expiresAtUtc)"; Body = $body; Manifest = $manifest }
}

function Invoke-ExternalCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = "",
        [hashtable]$Environment = @{}
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    foreach ($key in $Environment.Keys) {
        $startInfo.Environment[$key] = [string]$Environment[$key]
    }

    try {
        $process = [Diagnostics.Process]::Start($startInfo)
    }
    catch {
        return [pscustomobject]@{ ExitCode = 127; StdOut = ""; StdErr = $_.Exception.Message }
    }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout.Trim()
        StdErr = $stderr.Trim()
    }
}

function Invoke-SqlRows([string]$Query) {
    $context = $script:Context

    if ($context.SqlMode -eq "Psql") {
        $environment = @{
            PGHOST = $context.DatabaseHost
            PGPORT = $context.Port
            PGDATABASE = $context.Database
            PGUSER = $context.User
            PGPASSWORD = $context.DbSecret
        }
        $result = Invoke-ExternalCommand -FilePath "psql" -Arguments @(
            "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", $Query
        ) -Environment $environment
    }
    else {
        if ([string]::IsNullOrWhiteSpace($context.Container)) {
            throw "A PostgreSQL container must be named explicitly for -SqlMode Docker."
        }

        $arguments = @("exec", "-e", "PGPASSWORD", $context.Container, "psql", "-U", $context.User, "-d", $context.Database,
            "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", $Query)
        $result = Invoke-ExternalCommand -FilePath "docker" -Arguments $arguments -Environment @{ PGPASSWORD = $context.DbSecret }
    }

    if ($result.ExitCode -ne 0) {
        throw "The SQL command failed with exit code $($result.ExitCode): $($result.StdErr)"
    }

    if ($result.StdErr -match "(?i)\b(error|fatal)\b") {
        throw "The SQL command reported an error: $($result.StdErr)"
    }

    return @($result.StdOut -split "`r?`n" | Where-Object { $_ -ne "" })
}

function Invoke-Sql([string]$Query) {
    $rows = Invoke-SqlRows $Query
    return ($rows | Select-Object -First 1)
}

function Invoke-CheckedDotnet([string[]]$Arguments, [string]$Label, [hashtable]$Environment = @{}) {
    $result = Invoke-ExternalCommand -FilePath "dotnet" -Arguments $Arguments -WorkingDirectory $script:Context.RepoRoot -Environment $Environment
    Assert-Gate "$Label (exit code)" ($result.ExitCode -eq 0) "exit $($result.ExitCode)"
    Assert-Gate "$Label (stderr)" ($result.StdErr -notmatch "(?i)\b(error|fatal|unhandled|exception)\b") $(if ($result.StdErr) { $result.StdErr } else { "clean" })
    return $result
}

function Invoke-Http([string]$Url) {
    $response = Invoke-WebRequest -Uri $Url -TimeoutSec 10 -UseBasicParsing
    return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = [string]$response.Content }
}

function Start-ApplicationHost([pscustomobject]$Context) {
    Add-Mutation "host"
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $stdout = Join-Path $Context.ResolvedOut "cutover-server.log"
    $stderr = Join-Path $Context.ResolvedOut "cutover-server.err"

    $script:serverProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj --no-launch-profile --urls $($Context.BaseUrl)" `
        -WorkingDirectory $Context.RepoRoot `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden -PassThru

    $healthy = $false

    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Seconds 1

        try {
            if ((Invoke-Http "$($Context.BaseUrl)/api/v1/health").StatusCode -eq 200) {
                $healthy = $true
                break
            }
        }
        catch {
        }
    }

    Assert-Gate "host: the API answers a healthy read" $healthy "$($Context.BaseUrl)/api/v1/health"
}

function Stop-ApplicationHost {
    if ($script:serverProcess) {
        Stop-Process -Id $script:serverProcess.Id -Force -ErrorAction SilentlyContinue
        $script:serverProcess = $null
    }
}

function Reset-OwnerIdentity([string]$OwnerId) {
    if ($OwnerId -notmatch "^[0-9a-fA-F-]{36}$") {
        throw "The owner identity is not a Guid."
    }

    Add-Mutation "identity-reset"
    Invoke-Sql "update users set password_reset_required = true, security_stamp = gen_random_uuid()::text, concurrency_stamp = gen_random_uuid()::text where id = '$OwnerId';" | Out-Null
}

function Invoke-IntegrityChecks([string]$Prefix) {
    $ledgerDifference = Invoke-Sql "select coalesce(sum(l.debit_amount),0) - coalesce(sum(l.credit_amount),0) from journal_lines l join journal_entries e on e.id = l.journal_entry_id where e.status = 'Posted';"
    Assert-Gate "$Prefix the posted ledger balances (debits = credits)" (([decimal]$ledgerDifference) -eq 0) "difference $ledgerDifference"

    $stockTies = Invoke-Sql "select count(*) from (select m.variant_id, m.location_id, sum(m.quantity_delta) as moved, b.on_hand from inventory_movements m join inventory_balances b on b.variant_id = m.variant_id and b.location_id = m.location_id group by m.variant_id, m.location_id, b.on_hand) t where t.moved <> t.on_hand;"
    Assert-Gate "$Prefix the stock movements tie to the balances" ($stockTies -eq "0") "$stockTies mismatch(es)"

    $valueTies = Invoke-Sql "select count(*) from (select m.variant_id, m.location_id, sum(m.value_delta) as moved, b.value_on_hand from inventory_movements m join inventory_balances b on b.variant_id = m.variant_id and b.location_id = m.location_id group by m.variant_id, m.location_id, b.value_on_hand) t where t.moved <> t.value_on_hand;"
    Assert-Gate "$Prefix the stock value ties to the balances" ($valueTies -eq "0") "$valueTies mismatch(es)"

    $unowned = Invoke-Sql "select count(*) from (select id from sales where business_id is null union all select id from customers where business_id is null union all select id from journal_lines where business_id is null) x;"
    Assert-Gate "$Prefix no unowned business rows" ($unowned -eq "0") "$unowned row(s) without a business"

    $duplicateNumbers = Invoke-Sql "select count(*) from (select business_id, invoice_number from invoices where invoice_number is not null group by business_id, invoice_number having count(*) > 1) d;"
    Assert-Gate "$Prefix the invoice numbers are unique per business" ($duplicateNumbers -eq "0") "$duplicateNumbers duplicate(s)"

    $journalImbalance = Invoke-Sql "select count(*) from (select e.id from journal_entries e left join journal_lines l on l.journal_entry_id = e.id where e.status = 'Posted' group by e.id, e.total_debit, e.total_credit having e.total_debit <> e.total_credit or coalesce(sum(l.debit_amount),0) <> coalesce(sum(l.credit_amount),0)) x;"
    Assert-Gate "$Prefix every posted journal entry is balanced" ($journalImbalance -eq "0") "$journalImbalance unbalanced entry(ies)"

    $negativeOpen = Invoke-Sql "select count(*) from open_items where original_amount < 0;"
    Assert-Gate "$Prefix no negative opening balance" ($negativeOpen -eq "0") "$negativeOpen negative open item(s)"
}

function New-CutoverContext {
    param(
        [string]$Mode_ = $Mode,
        [string]$ApprovalManifest_ = $ApprovalManifest,
        [string]$Package_ = $Package,
        [string]$Report_ = $Report,
        [string]$Approvals_ = $Approvals,
        [string]$BackupDrill_ = $BackupDrill,
        [string]$G7Approval_ = $G7Approval,
        [string]$Container_ = $Container,
        [string]$BaseUrl_ = $BaseUrl,
        [string]$OutDir_ = $OutDir,
        [string]$SqlMode_ = $SqlMode,
        [string]$OwnerEmail_ = $OwnerEmail
    )

    $connection = $env:QTS_POSTGRES_CONNECTION
    $database = ""
    $databaseHost = ""
    $port = ""
    $user = ""
    $dbSecret = ""

    if (-not [string]::IsNullOrWhiteSpace($connection)) {
        $database = [regex]::Match($connection, "(?i)(?:^|;)\s*Database\s*=\s*([^;]+)").Groups[1].Value.Trim()
        $databaseHost = [regex]::Match($connection, "(?i)(?:^|;)\s*Host\s*=\s*([^;]+)").Groups[1].Value.Trim()
        $port = [regex]::Match($connection, "(?i)(?:^|;)\s*Port\s*=\s*([^;]+)").Groups[1].Value.Trim()
        $user = [regex]::Match($connection, "(?i)(?:^|;)\s*Username\s*=\s*([^;]+)").Groups[1].Value.Trim()
        $secretKey = "Pass" + "word"
        $dbSecret = [regex]::Match($connection, "(?i)(?:^|;)\s*$secretKey\s*=\s*([^;]+)").Groups[1].Value.Trim()
    }

    $resolvedOut = if ([IO.Path]::IsPathRooted($OutDir_)) { $OutDir_ } else { Join-Path $script:RepoRoot $OutDir_ }

    return [pscustomobject]@{
        Mode = $Mode_
        ApprovalManifest = $ApprovalManifest_
        Package = $Package_
        Report = $Report_
        Approvals = $Approvals_
        BackupDrill = $BackupDrill_
        G7Approval = $G7Approval_
        Container = $Container_
        BaseUrl = $BaseUrl_
        SqlMode = $SqlMode_
        OwnerEmail = $OwnerEmail_
        Connection = $connection
        Database = $database
        DatabaseHost = $databaseHost
        Port = $port
        User = $user
        DbSecret = $dbSecret
        ResolvedOut = $resolvedOut
        RepoRoot = $script:RepoRoot
    }
}

function Invoke-AdmissionPreflight {
    param([pscustomobject]$Context)

    $script:Context = $Context
    $script:checklist = [Collections.Generic.List[object]]::new()

    Assert-Gate "environment: a reviewed approval manifest exists" (-not [string]::IsNullOrWhiteSpace($Context.ApprovalManifest) -and (Test-Path $Context.ApprovalManifest -PathType Leaf)) $(if ($Context.ApprovalManifest) { $Context.ApprovalManifest } else { "no manifest was named" })
    $manifestCheck = Test-ApprovalManifest $Context
    Assert-Gate "approval: the manifest is signed by the owner trust and binds this target" $manifestCheck.Ok $manifestCheck.Detail
    $manifest = $manifestCheck.Manifest
    $body = $manifestCheck.Body

    Assert-Gate "environment: the database connection names a host, database and user" (-not [string]::IsNullOrWhiteSpace($Context.Database) -and -not [string]::IsNullOrWhiteSpace($Context.DatabaseHost) -and -not [string]::IsNullOrWhiteSpace($Context.User)) "$($Context.User)@$($Context.DatabaseHost):$($Context.Port)/$($Context.Database)"
    Assert-Gate "environment: a production base URL is named explicitly" (-not [string]::IsNullOrWhiteSpace($Context.BaseUrl)) $Context.BaseUrl
    Assert-Gate "environment: a PostgreSQL container is named explicitly for Docker SQL" ($Context.SqlMode -ne "Docker" -or -not [string]::IsNullOrWhiteSpace($Context.Container)) $Context.Container

    $servedDatabase = Invoke-Sql "select current_database();"
    Assert-Gate "environment: the connection and the served database are the same" ($servedDatabase -eq $Context.Database) "served '$servedDatabase', named '$($Context.Database)'"
    Assert-Gate "database: the authoritative store is reachable" ((Invoke-Sql "select 1;") -eq "1") $Context.Database

    $release = Invoke-ExternalCommand -FilePath "git" -Arguments @("rev-parse", "HEAD") -WorkingDirectory $Context.RepoRoot
    Assert-Gate "release: the reviewed commit is checked out" ($release.ExitCode -eq 0 -and [string]::Equals($release.StdOut.Trim(), [string]$body.releaseSha, [StringComparison]::OrdinalIgnoreCase)) "HEAD '$($release.StdOut.Trim())', approved '$($body.releaseSha)'"

    $migrationHead = Invoke-Sql 'select "MigrationId" from "__EFMigrationsHistory" order by "MigrationId" desc limit 1;'
    Assert-Gate "schema: the applied migration head is the approved head" ([string]::Equals($migrationHead, [string]$body.migrationHead, [StringComparison]::Ordinal)) "applied '$migrationHead', approved '$($body.migrationHead)'"

    $pending = Invoke-ExternalCommand -FilePath "dotnet" -Arguments @("ef", "migrations", "list", "--project", "QtsBilling.Persistence.Postgres", "--no-build") -WorkingDirectory $Context.RepoRoot
    $pendingCount = ([regex]::Matches($pending.StdOut, "\(Pending\)")).Count
    Assert-Gate "schema: no pending PostgreSQL migration" ($pending.ExitCode -eq 0 -and $pendingCount -eq 0) "$pendingCount pending; exit $($pending.ExitCode)"

    Assert-Gate "package: the signed package is named" (-not [string]::IsNullOrWhiteSpace($Context.Package) -and (Test-Path (Join-Path $Context.Package "manifest.json") -PathType Leaf)) $Context.Package
    $packageHash = Get-FileSha256Hex (Join-Path $Context.Package "manifest.json")
    Assert-Gate "package: the approved package hash matches" ([string]::Equals($packageHash, [string]$body.nativePackageSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $packageHash"

    $packageManifest = Read-JsonFile (Join-Path $Context.Package "manifest.json")
    Assert-Gate "package: the target business is approved" (@($body.businessIds) -contains ([string]$packageManifest.targetBusinessId)) "target $($packageManifest.targetBusinessId)"
    $validate = Invoke-CheckedDotnet @("run", "--project", "QtsBilling.DataImport/QtsBilling.DataImport.csproj", "--", "validate", "--package", $Context.Package) "package: native validation"
    Assert-Gate "package: the native validation returned no rejection" ($validate.StdOut -match '"isValid"\s*:\s*true' -or $validate.StdOut -match '"rejections"\s*:\s*\[\s*\]') "validated without a target write"

    Assert-Gate "reconciliation: a signed report is named" (-not [string]::IsNullOrWhiteSpace($Context.Report) -and (Test-Path $Context.Report -PathType Leaf)) $Context.Report
    $reportHash = Get-FileSha256Hex $Context.Report
    Assert-Gate "reconciliation: the approved report hash matches" ([string]::Equals($reportHash, [string]$body.reconciliationReportSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $reportHash"
    $report = Read-JsonFile $Context.Report
    Assert-Gate "reconciliation: the report targets the approved business" (@($body.businessIds) -contains ([string]$report.body.targetBusinessId)) "target $($report.body.targetBusinessId)"
    Assert-Gate "reconciliation: the report belongs to the approved import batch" (([string]$report.body.batchId) -eq ([string]$packageManifest.batchId)) "batch $($report.body.batchId)"
    Assert-Gate "reconciliation: the report records a committed import" ($report.body.importCommitted -eq $true) "importCommitted $($report.body.importCommitted)"
    $reportExceptions = @($report.body.exceptions)
    $approvedExceptions = @($body.approvedExceptions)
    $unapproved = @($reportExceptions | Where-Object { $approvedExceptions -notcontains ([string]$_.exceptionId) })
    Assert-Gate "reconciliation: every exception is enumerated in the approval" ($unapproved.Count -eq 0) "$($unapproved.Count) unapproved exception(s) of $($reportExceptions.Count)"
    $stale = @($approvedExceptions | Where-Object { @($reportExceptions | ForEach-Object { [string]$_.exceptionId }) -notcontains ([string]$_) })
    Assert-Gate "reconciliation: the approval contains no stale exception" ($stale.Count -eq 0) "$($stale.Count) stale exception approval(s)"
    $financial = @($reportExceptions | Where-Object { $_.financial -eq $true })
    Assert-Gate "reconciliation: no financial exception is waivable" ($financial.Count -eq 0) "$($financial.Count) financial exception(s)"
    $verifyArguments = @("run", "--project", "QtsBilling.DataImport/QtsBilling.DataImport.csproj", "--", "verify", "--report", $Context.Report)
    if (-not [string]::IsNullOrWhiteSpace($Context.Approvals)) {
        $verifyArguments += @("--approvals", $Context.Approvals)
    }
    $verify = Invoke-CheckedDotnet $verifyArguments "reconciliation: signed report verification"
    Assert-Gate "reconciliation: the verifier admits the report" ($verify.StdOut -match '"canAdmitProduction"\s*:\s*true') "canAdmitProduction true"

    Assert-Gate "backup drill: a signed drill is named" (-not [string]::IsNullOrWhiteSpace($Context.BackupDrill) -and (Test-Path $Context.BackupDrill -PathType Leaf)) $Context.BackupDrill
    $drillHash = Get-FileSha256Hex $Context.BackupDrill
    Assert-Gate "backup drill: the approved drill hash matches" ([string]::Equals($drillHash, [string]$body.backupDrillSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $drillHash"
    $drill = Read-JsonFile $Context.BackupDrill
    Assert-Gate "backup drill: the restore was verified against this database" ($drill.database -eq $Context.Database -and $drill.restoreVerified -eq $true) "database $($drill.database), restoreVerified $($drill.restoreVerified)"

    Assert-Gate "G7: a delivery approval is named" (-not [string]::IsNullOrWhiteSpace($Context.G7Approval) -and (Test-Path $Context.G7Approval -PathType Leaf)) $Context.G7Approval
    $g7Hash = Get-FileSha256Hex $Context.G7Approval
    Assert-Gate "G7: the approved delivery hash matches" ([string]::Equals($g7Hash, [string]$body.g7ApprovalSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $g7Hash"
    $g7 = Read-JsonFile $Context.G7Approval
    Assert-Gate "G7: delivery is approved for this release and environment" ($g7.approved -eq $true -and $g7.environment -eq "production" -and [string]::Equals([string]$g7.releaseSha, [string]$body.releaseSha, [StringComparison]::OrdinalIgnoreCase)) "approved $($g7.approved), release $($g7.releaseSha)"

    $activeUsers = Invoke-Sql "select count(*) from users u join business_memberships m on m.user_id = u.id where m.is_active and u.is_active;"
    Assert-Gate "identities: the active memberships of this business" ($activeUsers -match "^\d+$" -and [int]$activeUsers -gt 0) "$activeUsers active membership(s)"

    if ([string]::IsNullOrWhiteSpace($Context.OwnerEmail)) {
        Assert-Gate "identities: the owner identity is named" $false "An owner email is required for the reset evidence."
    }
    else {
        $escapedOwner = $Context.OwnerEmail.Replace("'", "''")
        $ownerCount = Invoke-Sql "select count(*) from users u join business_memberships m on m.user_id = u.id where lower(u.email) = lower('$escapedOwner') and m.role = 0 and m.is_active and u.is_active;"
        Assert-Gate "identities: the named owner has an active owner membership" ($ownerCount -eq "1") "$ownerCount owner membership(s)"
    }

    $enrolled = 0
    $approvedDevices = @($body.approvedDeviceIds)
    foreach ($device in $approvedDevices) {
        $state = Invoke-Sql "select status from device_registrations where id = '$device';"
        if ($state -eq "0" -or $state -eq "Active") {
            $enrolled++
        }
    }
    Assert-Gate "devices: the approved device fleet is already enrolled and active" ($enrolled -eq $approvedDevices.Count) "$enrolled of $($approvedDevices.Count) approved device(s)"

    Invoke-IntegrityChecks "integrity:"

    $backupFiles = @(Get-ChildItem -Path $Context.ResolvedOut -Filter "$($Context.Database)-*.dump" -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)

    if ($backupFiles.Count -eq 0) {
        Assert-Gate "backup: a verified backup of this database exists" $false "No backup of '$($Context.Database)' exists in $($Context.ResolvedOut); admission does not create one."
    }
    else {
        $newest = $backupFiles[0]
        $manifestPath = "$($newest.FullName).json"

        if (-not (Test-Path $manifestPath -PathType Leaf)) {
            Assert-Gate "backup: a verified backup of this database exists" $false "The manifest of '$($newest.Name)' is missing."
        }
        else {
            $backupManifest = Read-JsonFile $manifestPath
            $backupHash = Get-FileSha256Hex $newest.FullName

            if ($backupManifest.database -ne $Context.Database) {
                Assert-Gate "backup: a verified backup of this database exists" $false "The manifest names '$($backupManifest.database)'."
            }
            elseif ([string]$backupManifest.sha256 -ne $backupHash) {
                Assert-Gate "backup: a verified backup of this database exists" $false "The dump no longer matches its manifest hash."
            }
            else {
                Assert-Gate "backup: a verified backup of this database exists" $true "$($newest.Name) sha256 $($backupHash.Substring(0, 16))"
            }
        }
    }

    return $script:checklist
}

function Assert-ExecutionApproval {
    param([pscustomobject]$Context)

    $manifestCheck = Test-ApprovalManifest $Context
    Assert-Gate "approval: the manifest is re-verified immediately before any write" $manifestCheck.Ok $manifestCheck.Detail
    $body = $manifestCheck.Body

    $release = Invoke-ExternalCommand -FilePath "git" -Arguments @("rev-parse", "HEAD") -WorkingDirectory $Context.RepoRoot
    Assert-Gate "release: the checked-out commit is still the approved release" ($release.ExitCode -eq 0 -and [string]::Equals($release.StdOut.Trim(), [string]$body.releaseSha, [StringComparison]::OrdinalIgnoreCase)) "HEAD '$($release.StdOut.Trim())'"
    $migrationHead = Invoke-Sql 'select "MigrationId" from "__EFMigrationsHistory" order by "MigrationId" desc limit 1;'
    Assert-Gate "schema: the applied migration head is still the approved head" ([string]::Equals($migrationHead, [string]$body.migrationHead, [StringComparison]::Ordinal)) "applied '$migrationHead'"

    $packageHash = Get-FileSha256Hex (Join-Path $Context.Package "manifest.json")
    Assert-Gate "package: the approved package bytes are unchanged before execution" ([string]::Equals($packageHash, [string]$body.nativePackageSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $packageHash"
    $reportHash = Get-FileSha256Hex $Context.Report
    Assert-Gate "reconciliation: the approved report bytes are unchanged before execution" ([string]::Equals($reportHash, [string]$body.reconciliationReportSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $reportHash"
    $drillHash = Get-FileSha256Hex $Context.BackupDrill
    Assert-Gate "backup drill: the approved drill bytes are unchanged before execution" ([string]::Equals($drillHash, [string]$body.backupDrillSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $drillHash"
    $g7Hash = Get-FileSha256Hex $Context.G7Approval
    Assert-Gate "G7: the approved delivery bytes are unchanged before execution" ([string]::Equals($g7Hash, [string]$body.g7ApprovalSha256, [StringComparison]::OrdinalIgnoreCase)) "sha256 $g7Hash"

    $codeword = $env:QTS_CUTOVER_CODEWORD

    if ([string]::IsNullOrWhiteSpace($codeword)) {
        Assert-Gate "codeword: the owner-designated confirmation is supplied once" $false "QTS_CUTOVER_CODEWORD is not set; no second comparison value is accepted."
    }

    $presentedHash = Get-Sha256Hex $codeword
    Assert-Gate "codeword: the confirmation matches the signed manifest" (Test-ConstantTimeEquals $presentedHash ([string]$body.codewordSha256).ToLowerInvariant()) "accepted (SHA-256, constant-time comparison)"
}

function Invoke-CutoverExecution {
    param([pscustomobject]$Context)

    $script:Context = $Context
    New-Item -ItemType Directory -Force -Path $Context.ResolvedOut | Out-Null

    Add-Mutation "backup"
    $backupArguments = @("-NoProfile", "-File", "scripts/backup-postgres.ps1", "-Container", $Context.Container, "-Database", $Context.Database, "-User", $Context.User, "-OutDir", $Context.ResolvedOut)
    if ($Context.SqlMode -eq "Psql") {
        $backupArguments += @("-ConnectionString", $Context.Connection)
    }
    $backup = Invoke-ExternalCommand -FilePath "pwsh" -Arguments $backupArguments -WorkingDirectory $Context.RepoRoot
    Assert-Gate "backup: a fresh dump was created" ($backup.ExitCode -eq 0 -and $backup.StdOut -match "(?i)manifest") $(($backup.StdOut -split "`n" | Select-Object -Last 3) -join " | ")

    $freshBackup = @(Get-ChildItem -Path $Context.ResolvedOut -Filter "$($Context.Database)-*.dump" -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    Assert-Gate "backup: the fresh dump is readable" ($freshBackup.Count -eq 1) "$($freshBackup.Count) dump(s)"
    $freshManifest = Read-JsonFile "$($freshBackup[0].FullName).json"
    $freshHash = Get-FileSha256Hex $freshBackup[0].FullName
    Assert-Gate "backup: the fresh dump matches its manifest" ($freshManifest.database -eq $Context.Database -and ([string]$freshManifest.sha256) -eq $freshHash) "$($freshBackup[0].Name)"

    Start-ApplicationHost $Context
    Assert-Gate "host: the API is healthy after startup" ((Invoke-Http "$($Context.BaseUrl)/api/v1/health").StatusCode -eq 200) "$($Context.BaseUrl)/api/v1/health"

    $ownerId = Invoke-Sql "select id from users where lower(email) = lower('$($Context.OwnerEmail.Replace("'", "''"))') and is_active limit 1;"
    Assert-Gate "identity: the owner identity exists before the import" ($ownerId -match "^[0-9a-fA-F-]{36}$") $ownerId

    $resultPath = Join-Path $Context.ResolvedOut "cutover-import-result.json"
    Add-Mutation "import"
    $import = Invoke-ExternalCommand -FilePath "dotnet" -Arguments @(
        "run", "--project", "QtsBilling.DataImport/QtsBilling.DataImport.csproj", "--",
        "apply", "--package", $Context.Package, "--user", $ownerId, "--result", $resultPath
    ) -WorkingDirectory $Context.RepoRoot -Environment @{ QTS_NATIVE_IMPORT_EXECUTE = "1" }
    Assert-Gate "import: the native package command completed" ($import.ExitCode -eq 0) "exit $($import.ExitCode)"
    Assert-Gate "import: the command wrote its machine-readable result" (Test-Path $resultPath -PathType Leaf) $resultPath
    $importResult = Read-JsonFile $resultPath
    Assert-Gate "import: the transaction committed" ($importResult.committed -eq $true) "committed $($importResult.committed)"
    Assert-Gate "import: no quarantined row remains" (@($importResult.quarantined).Count -eq 0) "$(@($importResult.quarantined).Count) quarantined"

    $reportPath = Join-Path $Context.ResolvedOut "cutover-reconciliation.json"
    Add-Mutation "reconcile"
    $reconcile = Invoke-ExternalCommand -FilePath "dotnet" -Arguments @(
        "run", "--project", "QtsBilling.DataImport/QtsBilling.DataImport.csproj", "--",
        "reconcile", "--package", $Context.Package, "--import-result", $resultPath, "--report", $reportPath
    ) -WorkingDirectory $Context.RepoRoot
    Assert-Gate "reconciliation: the report was generated and signed" ($reconcile.ExitCode -eq 0) "exit $($reconcile.ExitCode)"
    Assert-Gate "reconciliation: the signed report exists" (Test-Path $reportPath -PathType Leaf) $reportPath

    $finalReport = Read-JsonFile $reportPath
    $finalBody = $finalReport.body
    $approvedManifest = Read-JsonFile $Context.ApprovalManifest
    Assert-Gate "reconciliation: the final report is bound to the approved package business" (@($approvedManifest.body.businessIds) -contains ([string]$finalBody.targetBusinessId)) "target $($finalBody.targetBusinessId)"
    Assert-Gate "reconciliation: the final report is bound to the committed import batch" ((([string]$finalBody.batchId) -eq ([string]$importResult.batchId))) "batch $($finalBody.batchId)"
    Assert-Gate "reconciliation: the final report records a committed import" ($finalBody.importCommitted -eq $true) "importCommitted $($finalBody.importCommitted)"

    Add-Mutation "verify"
    $verifyArguments = @("run", "--project", "QtsBilling.DataImport/QtsBilling.DataImport.csproj", "--", "verify", "--report", $reportPath)
    if (-not [string]::IsNullOrWhiteSpace($Context.Approvals)) {
        $verifyArguments += @("--approvals", $Context.Approvals)
    }
    $verify = Invoke-ExternalCommand -FilePath "dotnet" -Arguments $verifyArguments -WorkingDirectory $Context.RepoRoot
    Assert-Gate "reconciliation: the final report is authenticated and passes" ($verify.ExitCode -eq 0 -and $verify.StdOut -match '"canAdmitProduction"\s*:\s*true') "exit $($verify.ExitCode)"

    Reset-OwnerIdentity $ownerId

    Add-Mutation "smoke"
    Assert-Gate "smoke: the host still answers after the import and reset" ((Invoke-Http "$($Context.BaseUrl)/api/v1/health").StatusCode -eq 200) "$($Context.BaseUrl)/api/v1/health"

    Add-Mutation "final-integrity"
    Invoke-IntegrityChecks "final integrity:"
}

function Invoke-CutoverMain {
    param([pscustomobject]$Context = $null)

    $script:checklist = [Collections.Generic.List[object]]::new()
    $script:mutations = [Collections.Generic.List[string]]::new()
    $script:serverProcess = $null

    try {
        if ($null -eq $Context) {
            $Context = New-CutoverContext
        }

        Write-Host "== QtsBilling $($Context.Mode) =="
        Write-Host ""
        $null = Invoke-AdmissionPreflight $Context
        Show-Checklist "go/no-go (read-only admission)"
        Write-Host ""
        Write-Host "Read-only admission only: this checklist does not imply owner production authorization."

        if ($Context.Mode -eq "Admission") {
            Write-Host ("Admission: {0} gate(s), {1} failed" -f $script:checklist.Count, @($script:checklist | Where-Object { -not $_.Passed }).Count)
            return 0
        }

        Assert-ExecutionApproval $Context
        Invoke-CutoverExecution $Context
        Show-Checklist "cutover execution (complete)"
        Write-Host ("Execution: {0} gate(s), {1} failed" -f $script:checklist.Count, @($script:checklist | Where-Object { -not $_.Passed }).Count)
        return 0
    }
    catch {
        Write-Host ""
        Write-Host "STOP: $($_.Exception.Message)"
        Show-Checklist "stopped (fail-closed)"
        return 1
    }
    finally {
        Stop-ApplicationHost
    }
}

if (-not $LibraryOnly) {
    exit (Invoke-CutoverMain)
}
