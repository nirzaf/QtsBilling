#Requires -Version 7.0
<#
.SYNOPSIS
    Deterministic, no-side-effect scenarios for scripts/qb30-cutover.ps1 (specification task 21).

.DESCRIPTION
    The scenarios dot-source the cutover script with -LibraryOnly and replace every external effect
    with a controlled double. No docker, psql, dotnet, pwsh, network, database or file system outside
    the temporary fixture directory is touched. The script itself uses exactly the same admission and
    execution functions as production; only Invoke-Sql, Invoke-ExternalCommand, Start-ApplicationHost,
    Invoke-Http and Reset-OwnerIdentity are doubled.

    The JSON on stdout is consumed by TestQtsBilling/Operations/AdmissionScriptHarness.cs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        "gate1_no_mutations",
        "codeword_without_manifest",
        "server_a_database_b",
        "bad_report_hash",
        "missing_device",
        "nonzero_import_exit",
        "valid_preflight",
        "valid_execution"
    )]
    [string]$Scenario
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Parent $PSScriptRoot
$cutover = Join-Path $PSScriptRoot "qb30-cutover.ps1"
. $cutover -LibraryOnly

$script:BusinessId = "11111111-1111-4111-8111-111111111111"
$script:DeviceId = "22222222-2222-4222-8222-222222222222"
$script:OwnerId = "33333333-3333-4333-8333-333333333333"
$script:ReleaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
$script:MigrationHead = "20261021000000_NativeImport"
$script:ApprovalKeyId = "test-owner-approval"
$script:ApprovalKeyHex = ("11" * 32)
$script:Codeword = "single-owner-confirmation"
$script:OwnerEmail = "owner@example.test"
$script:Queries = [Collections.Generic.List[string]]::new()
$script:Calls = [Collections.Generic.List[string]]::new()

$connectionSecretKey = "Pass" + "word"

if ($Scenario -eq "server_a_database_b") {
    $script:ApprovalServer = "db-a"
    $script:ApprovalDatabase = "qts-a"
    $script:Connection = "Host=db-b;Port=5432;Database=qts-b;Username=qts;$connectionSecretKey=not-a-real-password"
}
else {
    $script:ApprovalServer = "db-a"
    $script:ApprovalDatabase = "qts"
    $script:Connection = "Host=db-a;Port=5432;Database=qts;Username=qts;$connectionSecretKey=not-a-real-password"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("qts-admission-" + [Guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $temp
$packageDir = Join-Path $temp "package"
$backupDir = Join-Path $temp "backups"
$null = New-Item -ItemType Directory -Path $packageDir
$null = New-Item -ItemType Directory -Path $backupDir

function Write-JsonFile([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 12
    Set-Content -Path $Path -Value $json -Encoding utf8NoBOM
}

function Get-ArgumentValue([string[]]$Arguments, [string]$Name) {
    for ($index = 0; $index -lt $Arguments.Count - 1; $index++) {
        if ($Arguments[$index] -eq $Name) {
            return $Arguments[$index + 1]
        }
    }

    return $null
}

$packageManifestPath = Join-Path $packageDir "manifest.json"
Write-JsonFile $packageManifestPath ([ordered]@{
    schema = "qts-native-package-v1"
    batchId = "44444444-4444-4444-8444-444444444444"
    targetBusinessId = $script:BusinessId
    capturedAtUtc = "2026-10-21T12:00:00.0000000+00:00"
    resources = @()
})

$reportPath = Join-Path $temp "reconciliation.json"
Write-JsonFile $reportPath ([ordered]@{
    schema = "qts-native-reconciliation-v1"
    body = [ordered]@{
        targetBusinessId = $script:BusinessId
        batchId = "44444444-4444-4444-8444-444444444444"
        importCommitted = $true
        exceptions = @()
    }
    approval = $null
})

$drillPath = Join-Path $temp "backup-drill.json"
Write-JsonFile $drillPath ([ordered]@{
    database = $script:ApprovalDatabase
    restoreVerified = $true
    completedAtUtc = "2026-10-20T12:00:00.0000000+00:00"
})

$g7Path = Join-Path $temp "g7-approval.json"
Write-JsonFile $g7Path ([ordered]@{
    approved = $true
    environment = "production"
    releaseSha = $script:ReleaseSha
    approvedAtUtc = "2026-10-20T12:00:00.0000000+00:00"
})

$backupPath = Join-Path $backupDir "$($script:ApprovalDatabase)-20261021.dump"
Set-Content -Path $backupPath -Value "controlled test dump" -Encoding utf8NoBOM
Write-JsonFile "$backupPath.json" ([ordered]@{
    database = $script:ApprovalDatabase
    fileName = (Split-Path -Leaf $backupPath)
    sha256 = (Get-FileSha256Hex $backupPath)
})

$manifestPath = Join-Path $temp "approval.json"

function New-ApprovalManifest([hashtable]$Overrides = @{}) {
    $body = [ordered]@{
        environment = "production"
        server = $script:ApprovalServer
        database = $script:ApprovalDatabase
        businessIds = @($script:BusinessId)
        approvedDeviceIds = @($script:DeviceId)
        releaseSha = $script:ReleaseSha
        migrationHead = $script:MigrationHead
        nativePackageSha256 = (Get-FileSha256Hex $packageManifestPath)
        reconciliationReportSha256 = (Get-FileSha256Hex $reportPath)
        approvedExceptions = @()
        backupDrillSha256 = (Get-FileSha256Hex $drillPath)
        g7ApprovalSha256 = (Get-FileSha256Hex $g7Path)
        codewordSha256 = (Get-Sha256Hex $script:Codeword)
        issuedAtUtc = ([DateTimeOffset]::UtcNow.AddMinutes(-5)).ToString("O")
        expiresAtUtc = ([DateTimeOffset]::UtcNow.AddHours(1)).ToString("O")
    }

    foreach ($key in $Overrides.Keys) {
        $body[$key] = $Overrides[$key]
    }

    $bodyObject = [pscustomobject]$body
    $signature = Get-HmacHex (Get-ApprovalCanonicalText $bodyObject) $script:ApprovalKeyHex

    Write-JsonFile $manifestPath ([ordered]@{
        schema = "qts-production-approval-v2"
        body = $body
        approval = [ordered]@{
            algorithm = "HMAC-SHA256"
            keyId = $script:ApprovalKeyId
            signature = $signature
        }
    })

    return $manifestPath
}

if ($Scenario -eq "bad_report_hash") {
    $null = New-ApprovalManifest @{ reconciliationReportSha256 = ("0" * 64) }
}
elseif ($Scenario -eq "missing_device") {
    $null = New-ApprovalManifest @{ approvedDeviceIds = @() }
}
elseif ($Scenario -ne "codeword_without_manifest") {
    $null = New-ApprovalManifest
}

$env:QTS_POSTGRES_CONNECTION = $script:Connection
$env:QTS_CUTOVER_APPROVAL_KEY = $script:ApprovalKeyHex
$env:QTS_CUTOVER_APPROVAL_KEY_ID = $script:ApprovalKeyId
$env:QTS_CUTOVER_CODEWORD = $script:Codeword

# Controlled database double. Only read queries are answered; the single update in Execute is the
# identity reset and is recorded by Reset-OwnerIdentity below.
function Invoke-Sql {
    param([string]$Query)

    $script:Queries.Add($Query)

    if ($Query -match "current_database") {
        if ($Scenario -eq "gate1_no_mutations") {
            return ""
        }

        return $script:ApprovalDatabase
    }

    if ($Query -match "select 1") { return "1" }
    if ($Query -match "MigrationId") { return $script:MigrationHead }
    if ($Query -match "device_registrations") { return "0" }

    if ($Query -match "select count\(\*\) from users") {
        if ($Query -match "lower\(u\.email\)") { return "1" }
        return "1"
    }

    if ($Query -match "select id from users") { return $script:OwnerId }
    if ($Query -match "update users") { return "" }

    if ($Query -match "journal|inventory_movements|unowned|duplicate|open_items") { return "0" }

    return "0"
}

# Controlled process double. It recognizes the immutable command shapes and writes only fixture
# output files inside the temporary directory.
function Invoke-ExternalCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = "",
        [hashtable]$Environment = @{}
    )

    $script:Calls.Add("$FilePath $($Arguments -join ' ')")
    $joined = $Arguments -join " "

    if ($FilePath -match "git$") {
        if ($joined -match "rev-parse") {
            return [pscustomobject]@{ ExitCode = 0; StdOut = $script:ReleaseSha; StdErr = "" }
        }

        return [pscustomobject]@{ ExitCode = 0; StdOut = ""; StdErr = "" }
    }

    if ($FilePath -match "dotnet$") {
        if ($joined -match "ef migrations list") {
            return [pscustomobject]@{ ExitCode = 0; StdOut = "No migrations were found."; StdErr = "" }
        }

        if ($joined -match " validate ") {
            return [pscustomobject]@{ ExitCode = 0; StdOut = '{"isValid":true,"rejections":[]}'; StdErr = "" }
        }

        if ($joined -match " verify ") {
            return [pscustomobject]@{ ExitCode = 0; StdOut = '{"canAdmitProduction":true}'; StdErr = "" }
        }

        if ($joined -match " apply ") {
            if ($Scenario -eq "nonzero_import_exit") {
                return [pscustomobject]@{ ExitCode = 1; StdOut = ""; StdErr = "controlled import failure" }
            }

            $resultPath = Get-ArgumentValue $Arguments "--result"
            Write-JsonFile $resultPath ([ordered]@{
                committed = $true
                quarantined = @()
                batchId = "44444444-4444-4444-8444-444444444444"
                targetBusinessId = $script:BusinessId
            })
            return [pscustomobject]@{ ExitCode = 0; StdOut = '{"committed":true}'; StdErr = "" }
        }

        if ($joined -match " reconcile ") {
            $reportOut = Get-ArgumentValue $Arguments "--report"
            Write-JsonFile $reportOut ([ordered]@{
                schema = "qts-native-reconciliation-v1"
                body = [ordered]@{
                    targetBusinessId = $script:BusinessId
                    batchId = "44444444-4444-4444-8444-444444444444"
                    importCommitted = $true
                    exceptions = @()
                }
                approval = $null
            })
            return [pscustomobject]@{ ExitCode = 0; StdOut = '{"importCommitted":true}'; StdErr = "" }
        }
    }

    if ($FilePath -match "(pwsh|powershell)(\.exe)?$") {
        return [pscustomobject]@{ ExitCode = 0; StdOut = "backup manifest: controlled"; StdErr = "" }
    }

    return [pscustomobject]@{ ExitCode = 0; StdOut = ""; StdErr = "" }
}

function Start-ApplicationHost {
    param($Context)
    Add-Mutation "host"
    $script:serverProcess = $null
}

function Stop-ApplicationHost {
    $script:serverProcess = $null
}

function Invoke-Http {
    param([string]$Url)
    return [pscustomobject]@{ StatusCode = 200; Content = '{"status":"healthy"}' }
}

function Reset-OwnerIdentity {
    param([string]$OwnerId)
    Add-Mutation "identity-reset"
}

$mode = if ($Scenario -eq "valid_preflight") { "Admission" } else { "Execute" }
$manifest = if ($Scenario -eq "codeword_without_manifest") { "" } else { $manifestPath }

$context = New-CutoverContext `
    -Mode_ $mode `
    -ApprovalManifest_ $manifest `
    -Package_ $packageDir `
    -Report_ $reportPath `
    -Approvals_ "" `
    -BackupDrill_ $drillPath `
    -G7Approval_ $g7Path `
    -Container_ "controlled-postgres" `
    -BaseUrl_ "http://controlled.invalid" `
    -OutDir_ $backupDir `
    -SqlMode_ "Docker" `
    -OwnerEmail_ $script:OwnerEmail

$exitCode = Invoke-CutoverMain -Context $context

$result = [pscustomobject]@{
    scenario = $Scenario
    exitCode = [int]$exitCode
    mutations = @($script:mutations)
    gates = @($script:checklist | ForEach-Object {
        [pscustomobject]@{ gate = $_.Gate; passed = [bool]$_.Passed }
    })
    calls = @($script:Calls)
    queries = @($script:Queries)
}

$result | ConvertTo-Json -Depth 10 -Compress
