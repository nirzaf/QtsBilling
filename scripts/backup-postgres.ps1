# QtsBilling PostgreSQL backup (QB-14).
#
# Produces a real logical backup with pg_dump (custom format) from the database container and copies it
# to the host, together with a sidecar manifest carrying the SHA-256 digest. The manifest describes a
# real dump; it is never treated as - or mistaken for - the backup itself.
#
# Example:
#   powershell -File scripts/backup-postgres.ps1
#   powershell -File scripts/backup-postgres.ps1 -OutDir D:\qts-backups

[CmdletBinding()]
param(
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$User = "qtsbilling",
    [string]$OutDir = "backups",
    [string]$ConnectionString = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

if (-not (Test-Path $resolvedOut)) {
    New-Item -ItemType Directory -Path $resolvedOut -Force | Out-Null
}

Write-Output "docker container: $Container"
Write-Output "database        : $Database (user $User)"

$containerProbe = docker ps --filter "name=$Container" --format "{{.Names}}"

if ($containerProbe -ne $Container -and [string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Container '$Container' is not running; start it before backing up."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$fileName = "$Database-$stamp.dump"
$targetPath = Join-Path $resolvedOut $fileName
$remotePath = "/tmp/qts-backup.dump"

# Dump inside the container, then copy the bytes out: binary data never passes through a shell redirect.
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    docker exec $Container pg_dump --username $User --format=custom --no-owner --no-privileges --compress=6 --file=$remotePath $Database 2>&1 | ForEach-Object { $_ }

    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE."
    }

    docker cp "${Container}:${remotePath}" $targetPath | Out-Null
}
else {
    # The bytes go straight to the file: a binary dump never passes through a shell redirect.
    & pg_dump $ConnectionString --format=custom --no-owner --no-privileges --compress=6 --file=$targetPath 2>&1 | ForEach-Object { $_ }

    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE."
    }
}
docker exec $Container rm -f $remotePath | Out-Null

if (-not (Test-Path $targetPath)) {
    throw "Backup file was not created."
}

$file = Get-Item $targetPath
$hash = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash

$manifest = [ordered]@{
    database      = $Database
    container     = $Container
    createdAtUtc  = (Get-Date).ToUniversalTime().ToString("o")
    fileName      = $fileName
    sizeBytes     = $file.Length
    sha256        = $hash
    format        = "pg_dump custom"
    postgresTool  = (docker exec $Container pg_dump --version)
}

$manifestPath = "$targetPath.json"
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Write-Output "backup file     : $($file.FullName)"
Write-Output "size (bytes)    : $($file.Length)"
Write-Output "sha256          : $hash"
Write-Output "manifest        : $manifestPath"
