[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) {
    throw "Publish output does not exist: $PublishRoot"
}

$frameworkRoot = Join-Path (Join-Path (Resolve-Path -LiteralPath $PublishRoot).Path "wwwroot") "_framework"
if (-not (Test-Path -LiteralPath $frameworkRoot -PathType Container)) {
    throw "Blazor WebAssembly framework directory is missing: $frameworkRoot"
}

$forbidden = @(
    "QtsBilling.Persistence.Postgres",
    "Npgsql",
    "Microsoft.EntityFrameworkCore.Relational",
    "OpenIddict.Server"
)
$leaks = @()

foreach ($file in (Get-ChildItem -LiteralPath $frameworkRoot -File -Recurse)) {
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($forbidden -contains $baseName) {
        $leaks += $file.FullName
    }
}

$bootManifest = Join-Path $frameworkRoot "blazor.boot.json"
if (Test-Path -LiteralPath $bootManifest -PathType Leaf) {
    $boot = Get-Content -LiteralPath $bootManifest -Raw | ConvertFrom-Json
    $assemblies = @()
    if ($boot.resources.assembly) {
        $assemblies = @($boot.resources.assembly.PSObject.Properties.Name)
    }

    foreach ($assembly in $assemblies) {
        $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($assembly)
        if ($forbidden -contains $assemblyName) {
            $leaks += "$bootManifest :: $assembly"
        }
    }
}

if ($leaks.Count -gt 0) {
    Write-Output "== WASM bundle verification FAILED: forbidden server assemblies found =="
    $leaks | ForEach-Object { Write-Output "  $_" }
    exit 1
}

$fileCount = @(Get-ChildItem -LiteralPath $frameworkRoot -File -Recurse).Count
Write-Output "== WASM bundle verification PASSED: checked $fileCount framework files; no forbidden server assemblies found =="
