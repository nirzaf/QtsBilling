# QtsBilling PostgreSQL backup (QB-14).
#
# Produces a real logical backup with pg_dump (custom format) from the database container and copies it
# to the host, together with a sidecar manifest carrying the SHA-256 digest. The manifest describes a
# real dump; it is never treated as - or mistaken for - the backup itself.
#
# Local document evidence (specification task 39) is copied alongside the dump and inventoried file by file, because
# the bytes live outside the database: restoring a dump alone would bring back documents whose evidence is gone.
# A directory that does not exist is recorded as absent, never as empty.
#
# Example:
#   powershell -File scripts/backup-postgres.ps1
#   powershell -File scripts/backup-postgres.ps1 -OutDir D:\qts-backups
#   powershell -File scripts/backup-postgres.ps1 -EvidenceDirectory D:\qts-evidence

[CmdletBinding()]
param(
    [string]$Container = "qtsbilling-pg",
    [string]$Database = "qtsbilling",
    [string]$User = "qtsbilling",
    [string]$OutDir = "backups",
    [string]$ConnectionString = "",
    [string]$EvidenceDirectory = ""
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

# The evidence this instance holds on its own disk, copied and inventoried beside the dump.
$evidenceSource = if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    Join-Path $repoRoot 'QtsWebApp/QtsWebApp/document-evidence/files'
}
elseif ([System.IO.Path]::IsPathRooted($EvidenceDirectory)) {
    $EvidenceDirectory
}
else {
    Join-Path $repoRoot $EvidenceDirectory
}

$evidenceOut = Join-Path $resolvedOut "evidence-$stamp"
$evidenceFiles = @()
$evidencePresent = Test-Path -LiteralPath $evidenceSource -PathType Container

if ($evidencePresent) {
    New-Item -ItemType Directory -Path $evidenceOut -Force | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $evidenceSource -File | Sort-Object Name) {
        $copy = Join-Path $evidenceOut $item.Name
        Copy-Item -LiteralPath $item.FullName -Destination $copy
        $evidenceFiles += [ordered]@{
            name      = $item.Name
            sizeBytes = $item.Length
            sha256    = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
}

$manifest.evidence = [ordered]@{
    present     = [bool]$evidencePresent
    directory   = $evidenceSource
    copy        = $evidenceOut
    fileCount   = $evidenceFiles.Count
    totalBytes  = [long]((($evidenceFiles | Measure-Object -Property sizeBytes -Sum).Sum))
    files       = @($evidenceFiles)
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Output "backup file     : $($file.FullName)"
Write-Output "size (bytes)    : $($file.Length)"
Write-Output "sha256          : $hash"
Write-Output "evidence files  : $(if ($evidencePresent) { $evidenceFiles.Count } else { 'no evidence directory at ' + $evidenceSource })"
Write-Output "manifest        : $manifestPath"
