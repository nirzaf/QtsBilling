# QtsBilling PostgreSQL restore (QB-14).
#
# Restores a pg_dump custom-format backup into an explicitly named target database. The live database
# name is refused outright: restores are for disposable environments (drills, migrations rehearsals), and
# overwriting a target requires -Force. The SHA-256 digest is verified against the sidecar manifest when
# one is present.
#
# Example:
#   powershell -File scripts/restore-postgres.ps1 -BackupFile backups/qtsbilling-20261019-120000.dump -TargetDatabase qtsbilling_restore_drill

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,

    [Parameter(Mandatory = $true)]
    [string]$TargetDatabase,

    [string]$Container = "qtsbilling-pg",
    [string]$User = "qtsbilling",
    [string]$LiveDatabase = "qtsbilling",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ($TargetDatabase -eq $LiveDatabase) {
    throw "Refusing to restore over the live database '$LiveDatabase'. Point -TargetDatabase at a disposable database."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedBackup = if ([System.IO.Path]::IsPathRooted($BackupFile)) { $BackupFile } else { Join-Path $repoRoot $BackupFile }

if (-not (Test-Path $resolvedBackup)) {
    throw "Backup file '$resolvedBackup' does not exist."
}

$manifestPath = "$resolvedBackup.json"

if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $hash = (Get-FileHash -Path $resolvedBackup -Algorithm SHA256).Hash

    if ($manifest.sha256 -ne $hash) {
        throw "Backup digest mismatch: manifest says $($manifest.sha256), file is $hash. Refusing to restore a changed artifact."
    }

    Write-Output "digest verified against manifest: $hash"
}

$containerProbe = docker ps --filter "name=$Container" --format "{{.Names}}"

if ($containerProbe -ne $Container) {
    throw "Container '$Container' is not running."
}

$existsRaw = docker exec $Container psql --username $User --dbname postgres --tuples-only --no-align --command "SELECT 1 FROM pg_database WHERE datname = '$TargetDatabase'"
$exists = ("$existsRaw").Trim()

if ($exists -eq "1") {
    if (-not $Force) {
        throw "Target database '$TargetDatabase' exists; pass -Force to drop and recreate it."
    }

    Write-Output "dropping existing target '$TargetDatabase' (-Force)"
    docker exec $Container dropdb --username $User --force $TargetDatabase | ForEach-Object { $_ }
}

docker exec $Container createdb --username $User --owner $User $TargetDatabase | ForEach-Object { $_ }

$remotePath = "/tmp/qts-restore.dump"
docker cp $resolvedBackup "${Container}:${remotePath}" | Out-Null

Write-Output "restoring into   : $TargetDatabase"
docker exec $Container pg_restore --username $User --dbname $TargetDatabase --no-owner --no-privileges --exit-on-error $remotePath 2>&1 | ForEach-Object { $_ }

$restoreExit = $LASTEXITCODE
docker exec $Container rm -f $remotePath | Out-Null

if ($restoreExit -ne 0) {
    throw "pg_restore failed with exit code $restoreExit."
}

Write-Output "restore complete : $TargetDatabase"
