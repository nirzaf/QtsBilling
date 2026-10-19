<#
.SYNOPSIS
    End-to-end rehearsal of the QtsBilling server and desktop client (QB-28).

.DESCRIPTION
    Runs the whole cutover rehearsal in the order an operator would:

      1. build the solution and run the test suites,
      2. the static secret scan,
      3. start the server and wait for the health endpoint,
      4. the idempotent sync-push smoke (one operation pushed five ways),
      5. the master-data change-feed smoke (commit order, cursor, resync decision),
      6. a real backup and a restore drill into a disposable database,
      7. stop the server again.

    Every step prints PASS/FAIL; the script exits non-zero when a step fails, so it can gate a
    release. It never deletes business data: the restore drill uses a separate drill database and
    drops it again.

.EXAMPLE
    powershell -File scripts/qb28-rehearsal.ps1
#>
[CmdletBinding()]
param(
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$User = "qtsbilling",
    [string]$BaseUrl = "http://localhost:5188",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$results = New-Object System.Collections.Generic.List[object]
$serverProcess = $null

function Add-Result([string]$Step, [bool]$Passed, [string]$Detail) {
    $script:results.Add([pscustomobject]@{ Step = $Step; Passed = $Passed; Detail = $Detail })
    Write-Host ("{0,-58} {1}" -f $Step, $(if ($Passed) { "PASS" } else { "FAIL" }))
    if ($Detail) { Write-Host "    $Detail" }
}

function Invoke-Step([string]$Step, [scriptblock]$Action) {
    try {
        $output = & $Action 2>&1 | Out-String
        $failed = $output -match "(?i)\bFAIL\b|error CS|Unhandled exception"
        $detail = (($output.Trim() -split "`n" | Select-Object -Last 2 | ForEach-Object { $_.Trim() }) -join " | ")
        Add-Result $Step (-not $failed) $detail
    }
    catch {
        Add-Result $Step $false $_.Exception.Message
    }
}

try {
    if (-not $SkipBuild) {
        Invoke-Step "build: solution" { dotnet build QtsBilling.sln -c Debug --nologo 2>&1 | Out-String }
        Invoke-Step "test: device store (SQLite)" { dotnet test QtsBilling.Persistence.Sqlite.Tests/QtsBilling.Persistence.Sqlite.Tests.csproj -c Debug --nologo 2>&1 | Out-String }
        Invoke-Step "test: domain and contracts" { dotnet test TestQtsBilling/TestQtsBilling.csproj -c Debug --nologo 2>&1 | Out-String }
    }

    Invoke-Step "scan: static secret scan" { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/secret-scan.ps1 2>&1 | Out-String }

    # 3. the server
    $connection = $env:QTS_POSTGRES_CONNECTION

    if ([string]::IsNullOrWhiteSpace($connection)) {
        Add-Result "server: configuration" $false "QTS_POSTGRES_CONNECTION is not set; the rehearsal never carries a credential itself."
        $connection = ""
    }
    else {
        Add-Result "server: configuration" $true "the connection string comes from the environment."
    }

    $env:ASPNETCORE_ENVIRONMENT = "Development"
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "backups") | Out-Null

    if ($connection) {
        $env:QTS_POSTGRES_CONNECTION = $connection
    }

    $serverProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project QtsWebApp/QtsWebApp/QtsWebApp.csproj --no-launch-profile --urls $BaseUrl" `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput "backups/rehearsal-server.log" `
        -RedirectStandardError "backups/rehearsal-server.err" `
        -WindowStyle Hidden -PassThru

    $healthy = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $code = (Invoke-WebRequest -Uri "$BaseUrl/api/v1/health" -TimeoutSec 5 -UseBasicParsing).StatusCode
            if ($code -eq 200) { $healthy = $true; break }
        }
        catch { }
    }
    Add-Result "server: health endpoint" $healthy $BaseUrl

    if ($healthy) {
        # 4/5. the device-facing smokes need an enrolled device in the current business
        $businessId = (docker exec $Container psql -U $User -d $Database -t -A -c "select id from businesses limit 1;" | Select-Object -First 1).ToString().Trim()
        $locationId = (docker exec $Container psql -U $User -d $Database -t -A -c "select id from business_locations limit 1;" | Select-Object -First 1).ToString().Trim()
        $deviceId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"

        docker exec $Container psql -U $User -d $Database -c "insert into device_registrations (id, business_id, location_id, name, platform, app_version, status, enrolled_at_utc, version, created_at_utc, updated_at_utc) values ('$deviceId', '$businessId', '$locationId', 'rehearsal device', 'windows', '1.0.0', 'Active', now(), 1, now(), now()) on conflict (id) do update set business_id = excluded.business_id, status = 'Active';" | Out-Null

        Invoke-Step "sync: idempotent push (replay, conflict, device, business)" {
            dotnet run scripts/qb20-sync-push-smoke.cs $deviceId $businessId $locationId 2>&1 | Out-String
        }
        Invoke-Step "sync: change feed (order, cursor, resync)" {
            dotnet run scripts/qb21-change-feed-smoke.cs 2>&1 | Out-String
        }
    }

    # 6. a real backup and a restore drill
    Invoke-Step "backup: pg_dump with SHA-256 manifest" { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-postgres.ps1 2>&1 | Out-String }
    Invoke-Step "restore: drill into a disposable database" { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore-drill.ps1 2>&1 | Out-String }
}
finally {
    if ($serverProcess) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        Write-Host "server stopped"
    }

    Pop-Location
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ""
Write-Host ("rehearsal: {0} step(s), {1} failed" -f $results.Count, $failed.Count)

if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Host ("  FAIL {0}: {1}" -f $item.Step, $item.Detail) }
    exit 1
}

exit 0
