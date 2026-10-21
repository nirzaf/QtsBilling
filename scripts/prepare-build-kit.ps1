# QtsBilling build kit (self-hosted SMB completion, specification task 11).
#
# A self-hosted instance has to be rebuildable by an operator who has no internet access at all. This script
# turns that into an inspectable artefact: it acquires the exact NuGet packages every retained project resolves,
# seals them into a local feed with hashes and licence evidence, and then admits a build against that feed alone
# - in locked mode, with an isolated package cache, and with no public source in the effective configuration.
#
# Connected acquisition and offline admission are deliberately separate actions:
#
#   pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Acquire      # needs network once, per reviewed set
#   pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Verify       # offline: the admission check
#   pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Inventory    # offline: provenance record
#
# Nothing here upgrades a version. The reviewed set is what the projects already ask for (decision D12): a change
# to a package version is a change to the kit and is admitted only after it has been reviewed again.

[CmdletBinding()]
param(
    [ValidateSet('Acquire', 'Verify', 'Inventory')]
    [string]$Action = 'Verify',

    [string]$KitRoot = 'build-kit',

    # Restricts the run to named projects or project files (used by the focused test script). Empty means every
    # retained project, which is the normal admission.
    [string[]]$Project = @(),

    [switch]$IncludeBrowserTests,

    [switch]$SkipToolInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# Licences an operator has read and accepted for redistribution with this application. A package whose licence is
# not in this set fails admission instead of being silently shipped: adding one is a review, not a restore.
$reviewedLicences = @(
    'MIT',
    'Apache-2.0',
    'BSD-3-Clause',
    'PostgreSQL',
    'Apache-2.0 WITH LLVM-exception'
)

# Packages whose NuGet metadata names a bundled licence file or a URL instead of an SPDX expression. Each entry
# below was read and is accepted by identity, with the reason recorded where a reviewer can find it again
# (docs/operations/dependencies.md). A package that needs an entry and does not have one fails admission, and an
# entry that no longer matches anything is reported as stale rather than left to rot.
$reviewedLicenceExceptions = @{
    'Microsoft.Graphics.Win2D/1.3.2'                  = 'Microsoft Win2D licence (declared by URL). Microsoft redistributable, used only by the Windows device shell.'
    'Microsoft.Web.WebView2/1.0.3179.45'              = 'Microsoft WebView2 SDK licence (bundled LICENSE.txt). Redistributable, Windows device shell only.'
    'Microsoft.Windows.SDK.BuildTools/10.0.22621.756' = 'Windows SDK build tools licence (declared by URL). Build-time tooling; nothing of it ships as application code.'
    'Microsoft.WindowsAppSDK/1.7.250909003'           = 'Windows App SDK licence (bundled license.txt). Windows device shell runtime.'
    'xunit.abstractions/2.0.3'                        = 'xunit licence text (declared by URL), the same terms as the reviewed xunit package. Test-only: never in a shipped asset.'
    'Bogus/35.6.5'                                    = 'Bogus MIT licence bundled as LICENSE. Test-only data generator: never in a shipped asset.'
}

$feedDirectory = Join-Path $KitRoot 'packages'
$cacheDirectory = Join-Path $KitRoot 'verify-cache'
$manifestPath = Join-Path $KitRoot 'dependency-manifest.json'
$offlineConfig = 'NuGet.Offline.Config'

try {
    function Get-ProjectFiles {
        param([string[]]$Restriction)

        $all = Get-ChildItem -Path . -Recurse -File -Filter '*.csproj' |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|build-kit|out|tmp)\\' } |
            ForEach-Object { $_.FullName.Substring((Get-Location).Path.Length + 1).Replace('\', '/') }

        if ($Restriction.Count -eq 0) {
            return @($all | Sort-Object)
        }

        $matched = @()
        foreach ($entry in $Restriction) {
            $hit = @($all | Where-Object { $_ -eq $entry.Replace('\', '/') -or $_.EndsWith("/$(Split-Path $entry -Leaf)") })
            if ($hit.Count -eq 0) {
                throw "no retained project matches -Project '$entry'"
            }

            $matched += $hit
        }

        return @($matched | Sort-Object -Unique)
    }

    function Get-DotNetRoot {
        $command = Get-Command dotnet -ErrorAction Stop
        return Split-Path -Parent $command.Source
    }

    function Get-GlobalPackageCache {
        $line = dotnet nuget locals global-packages --list
        $text = @($line | Where-Object { $_ -match 'global-packages' }) | Select-Object -First 1
        if (-not $text) {
            throw "could not locate the global package cache from 'dotnet nuget locals global-packages --list'"
        }

        return ($text -split ':\s*', 2)[1].Trim().TrimEnd('\', '/')
    }

    function Get-ResolvedPackages {
        param([string]$ProjectFile)

        $assets = Join-Path (Split-Path $ProjectFile -Parent) 'obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets -PathType Leaf)) {
            throw "$ProjectFile has no obj/project.assets.json: restore it first (Acquire does)."
        }

        $document = Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json
        $resolved = @()

        if (-not $document.libraries) {
            return @()
        }

        foreach ($property in $document.libraries.PSObject.Properties) {
            $library = $property.Value
            if ($library.type -ne 'Package') {
                continue
            }

            $parts = $property.Name.Split('/')
            $sha = if ($library.PSObject.Properties['sha512']) { $library.sha512 } else { $null }
            $resolved += [pscustomobject]@{
                Id      = $parts[0]
                Version = $parts[1]
                Sha512  = $sha
            }
        }

        return @($resolved | Sort-Object -Property Id, Version -Unique)
    }

    function Get-ProjectDirectPackages {
        param([string]$ProjectFile)

        $xml = [xml](Get-Content -LiteralPath $ProjectFile -Raw)
        $nodes = $xml.SelectNodes("//*[local-name()='PackageReference']")
        $direct = @()

        foreach ($node in $nodes) {
            $include = $node.GetAttribute('Include')
            $version = $node.GetAttribute('Version')
            if ($include) {
                $direct += [pscustomobject]@{ Id = $include; Version = $version }
            }
        }

        return $direct
    }

    function Get-LicenceOfPackage {
        param([string]$NupkgPath)

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)

        try {
            $entry = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') }) | Select-Object -First 1
            if (-not $entry) {
                return [pscustomobject]@{ Licence = ''; RequireAcceptance = $false; Detail = 'no .nuspec inside the package' }
            }

            $stream = $entry.Open()
            try {
                $reader = New-Object System.IO.StreamReader($stream)
                $xmlText = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
                $stream.Dispose()
            }

            $nuspec = [xml]$xmlText
            $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
            $licenceNode = $metadata.SelectSingleNode("*[local-name()='license']")
            $urlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
            $acceptNode = $metadata.SelectSingleNode("*[local-name()='requireLicenseAcceptance']")

            $licence = if ($licenceNode -and $licenceNode.InnerText) { $licenceNode.InnerText.Trim() }
                elseif ($urlNode -and $urlNode.InnerText) { "licenseUrl:$($urlNode.InnerText.Trim())" }
                else { '' }

            return [pscustomobject]@{
                Licence           = $licence
                RequireAcceptance = ($acceptNode -and $acceptNode.InnerText -match 'true')
                Detail            = ''
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    function Get-InstalledWorkloads {
        # 'dotnet workload list' prints a table under a dashed rule: the first column of every row after it is a
        # workload identifier. The recorded set is what an offline machine must have installed with
        # 'dotnet workload install', so the parse has to be exact rather than a substring guess.
        $lines = @(dotnet workload list 2>&1 | ForEach-Object { "$_" })
        $identifiers = @()
        $inTable = $false

        foreach ($line in $lines) {
            $text = $line.Trim()

            if ($text -match '^-{5,}$') {
                $inTable = $true
                continue
            }

            if (-not $inTable -or [string]::IsNullOrWhiteSpace($text)) {
                continue
            }

            $first = (@($text -split '\s+') | Select-Object -First 1)
            # Workload identifiers are lower case; the trailing 'Use `dotnet workload search`' hint is not.
            if ("$first" -cmatch '^[a-z][a-z0-9\-]*$') {
                $identifiers += $first
            }
        }

        return @($identifiers | Sort-Object -Unique)
    }

    function Get-ToolManifest {
        $path = Join-Path (Get-Location) 'dotnet-tools.json'
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return @()
        }

        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $tools = @()

        foreach ($property in $document.tools.PSObject.Properties) {
            $tools += [pscustomobject]@{
                Id      = $property.Name
                Version = $property.Value.version
                Commands = @($property.Value.commands)
            }
        }

        return $tools
    }

    function Get-SourceProvenance {
        $commit = (& git rev-parse HEAD) 2>$null
        $revision = if ($LASTEXITCODE -eq 0 -and $commit) { "$commit".Trim() } else { 'unknown' }

        return [ordered]@{
            gitCommit            = $revision
            sdkVersion           = (& dotnet --version).Trim()
            sdksInstalled        = @(& dotnet --list-sdks | ForEach-Object { "$_".Trim() })
            runtimes             = @(& dotnet --list-runtimes | ForEach-Object { "$_".Trim() })
            workloads            = @(Get-InstalledWorkloads)
            toolManifest         = @(Get-ToolManifest | ForEach-Object { [ordered]@{ id = $_.Id; version = $_.Version; commands = $_.Commands } })
            advisorySnapshotUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            # Recorded honestly: this kit proves provenance and reproducibility. It is not a vulnerability scan
            # and makes no claim that no advisory exists against these versions.
            advisoryNote         = 'snapshot date only; no online advisory claim is made (specification G definition of done)'
            reviewedLicences     = @($reviewedLicences | Sort-Object)
        }
    }

    function Copy-KitPackage {
        param([string]$Id, [string]$Version, [string]$CacheRoot)

        $sourceRoot = Join-Path $CacheRoot $Id.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            throw "the package '$Id' is not present in the machine cache at $sourceRoot"
        }

        $versionDirectory = Get-ChildItem -LiteralPath $sourceRoot -Directory |
            Where-Object { $_.Name -eq $Version -or $_.Name -eq $Version.ToLowerInvariant() } |
            Select-Object -First 1

        if (-not $versionDirectory) {
            throw "the package '$Id' version '$Version' is not present in the machine cache under $sourceRoot"
        }

        $nupkg = Get-ChildItem -LiteralPath $versionDirectory.FullName -File -Filter '*.nupkg' | Select-Object -First 1
        if (-not $nupkg) {
            throw "the package '$Id' '$Version' has no .nupkg in $($versionDirectory.FullName)"
        }

        $target = Join-Path $feedDirectory $nupkg.Name
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            Copy-Item -LiteralPath $nupkg.FullName -Destination $target -Force
        }

        return $target
    }

    function Copy-KitPackageTree {
        # A tool is only usable offline together with everything its nuspec asks for. Depth is bounded so a
        # surprising dependency loop fails loudly instead of hanging the acquisition.
        param([string]$Id, [string]$Version, [string]$CacheRoot, [int]$Depth = 3)

        $queue = New-Object System.Collections.Generic.Queue[object]
        $queue.Enqueue([pscustomobject]@{ Id = $Id; Version = $Version; Depth = 0 })
        $seen = @{}
        $copied = @()

        while ($queue.Count -gt 0) {
            $item = $queue.Dequeue()
            $key = "$($item.Id)|$($item.Version)"
            if ($seen.ContainsKey($key)) {
                continue
            }

            $seen[$key] = $true

            try {
                $path = Copy-KitPackage -Id $item.Id -Version $item.Version -CacheRoot $CacheRoot
            }
            catch {
                Write-Output "   skip: $($_.Exception.Message)"
                continue
            }

            $copied += $path

            if ($item.Depth -ge $Depth) {
                continue
            }

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
            try {
                $entry = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') }) | Select-Object -First 1
                if (-not $entry) {
                    continue
                }

                $stream = $entry.Open()
                try {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $nuspec = [xml]$reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                    $stream.Dispose()
                }

                foreach ($dependency in $nuspec.SelectNodes("//*[local-name()='dependency']")) {
                    $id = $dependency.GetAttribute('id')
                    $version = $dependency.GetAttribute('version').Trim('(', ')').Split(',')[0]
                    if ($id) {
                        $queue.Enqueue([pscustomobject]@{ Id = $id; Version = $version; Depth = $item.Depth + 1 })
                    }
                }
            }
            finally {
                $archive.Dispose()
            }
        }

        return $copied
    }

    function Save-KitNotice {
        # Copies the licence text a package actually carries into the kit, so a release can ship the notices
        # without re-opening the archive. A package that declares only a URL has nothing to copy, and the
        # manifest records that fact instead of pretending otherwise.
        param([string]$NupkgPath, [string]$Id, [string]$Version)

        $target = Join-Path $KitRoot ("notices/{0}.{1}" -f $Id.ToLowerInvariant(), $Version.ToLowerInvariant())
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)

        try {
            $candidates = @($archive.Entries | Where-Object {
                $_.FullName -notmatch '/' -and $_.Name -match '^(license|licence|copying|notice).*\.(txt|md)$' -or
                ($_.Name -match '^(LICENSE|License|licence|LICENSE\.txt|LICENSE\.md)$' -and $_.FullName -notmatch 'lib/|ref/')
            } | Sort-Object -Property FullName -Unique)

            if ($candidates.Count -eq 0) {
                return $null
            }

            New-Item -ItemType Directory -Path $target -Force | Out-Null

            foreach ($entry in $candidates) {
                $stream = $entry.Open()
                try {
                    $file = Join-Path $target ([System.IO.Path]::GetFileName($entry.FullName))
                    $destination = [System.IO.File]::Create($file)
                    try {
                        $stream.CopyTo($destination)
                    }
                    finally {
                        $destination.Dispose()
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }

            return $target.Replace((Get-Location).Path + '\', '').Replace('\', '/')
        }
        finally {
            $archive.Dispose()
        }
    }

    function Copy-MissingPackagesFromCache {
        # A hermetic restore finds more than the project graph names: the SDK asks for targeting and runtime packs
        # (Windows SDK refs, the browser-wasm mono runtime) that never appear in project.assets.json. Each one is
        # copied from the machine cache into the feed, so the sealed kit - not the machine - answers the request.
        param([string]$RestoreOutput, [string]$CacheRoot)

        $ids = @([regex]::Matches($RestoreOutput, 'Unable to find package ([A-Za-z0-9_.\-]+)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

        $added = @()
        $unresolved = @()

        # The pinned runtime band decides which cached version of an implicit pack is the one this build needs,
        # so an unrelated older runtime pack is not dragged into the kit at tens of megabytes a time.
        $band = ''
        $runtimes = @(& dotnet --list-runtimes | ForEach-Object { "$_" })
        $core = @($runtimes | Where-Object { $_ -match 'Microsoft\.NETCore\.App (\d+\.\d+)' }) | Select-Object -First 1
        if ($core -match 'Microsoft\.NETCore\.App (\d+\.\d+)') {
            $band = $Matches[1]
        }

        foreach ($id in $ids) {
            $directory = Join-Path $CacheRoot $id.ToLowerInvariant()

            if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
                $unresolved += $id
                continue
            }

            $candidates = @(Get-ChildItem -LiteralPath $directory -Directory)
            $preferred = @($candidates | Where-Object { $band -and $_.Name.StartsWith($band, [StringComparison]::OrdinalIgnoreCase) })
            if ($preferred.Count -gt 0) {
                $candidates = $preferred
            }

            foreach ($versionDirectory in $candidates) {
                foreach ($nupkg in (Get-ChildItem -LiteralPath $versionDirectory.FullName -File -Filter '*.nupkg')) {
                    $target = Join-Path $feedDirectory $nupkg.Name
                    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                        Copy-Item -LiteralPath $nupkg.FullName -Destination $target -Force
                    }

                    $added += "$id/$($versionDirectory.Name)"
                }
            }
        }

        return [pscustomobject]@{ Added = @($added | Sort-Object -Unique); Unresolved = $unresolved }
    }

    function Get-PackagesForAdmission {
        param([string[]]$Projects)

        $cache = Get-GlobalPackageCache
        $perProject = @()
        $seen = @{}

        foreach ($project in $Projects) {
            $resolved = Get-ResolvedPackages -ProjectFile $project

            foreach ($package in $resolved) {
                if ($seen.ContainsKey("$($package.Id)|$($package.Version)")) {
                    continue
                }

                $feedPath = $null
                $licenceInfo = $null

                if ($Action -eq 'Acquire') {
                    $feedPath = Copy-KitPackage -Id $package.Id -Version $package.Version -CacheRoot $cache
                }
                else {
                    $candidate = Join-Path $feedDirectory "$($package.Id.ToLowerInvariant()).$($package.Version.ToLowerInvariant()).nupkg"
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                        $feedPath = $candidate
                    }
                }

                if ($feedPath) {
                    $licenceInfo = Get-LicenceOfPackage -NupkgPath $feedPath
                }

                $notice = if ($feedPath -and $Action -eq 'Acquire') {
                    Save-KitNotice -NupkgPath $feedPath -Id $package.Id -Version $package.Version
                }
                else {
                    $null
                }

                $seen["$($package.Id)|$($package.Version)"] = $true

                $perProject += [ordered]@{
                    id                  = $package.Id
                    version             = $package.Version
                    project             = $project
                    file                = if ($feedPath) { $feedPath.Replace((Get-Location).Path + '\', '').Replace('\', '/') } else { $null }
                    sha256              = if ($feedPath) { (Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash } else { $null }
                    nugetSha512         = $package.Sha512
                    licence             = if ($licenceInfo) { $licenceInfo.Licence } else { $null }
                    requireLicenceAcceptance = if ($licenceInfo) { $licenceInfo.RequireAcceptance } else { $null }
                    notice              = $notice
                    reviewedException   = $reviewedLicenceExceptions["$($package.Id)/$($package.Version)"]
                }
            }
        }

        return $perProject
    }

    function Clear-KitDirectory {
        # The admission check needs an empty package cache. A lingering MSBuild node can hold a signature file
        # open, so each run gets its own directory (which is empty by construction) and older runs are removed
        # when they can be. Refusing to delete a stale directory is not a reason to reuse it.
        param([string]$Path)

        $run = Join-Path $Path ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run -Force | Out-Null

        Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $run } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

        return $run
    }

    function Invoke-HermeticRestore {
        param([string[]]$Projects)

        # An empty cache and one source: this is the condition a build user on a sealed machine meets.
        $cacheRun = Clear-KitDirectory -Path $cacheDirectory
        $failures = @()

        foreach ($project in $Projects) {
            $arguments = @(
                'restore', $project,
                '--nologo',
                '--configfile', $offlineConfig,
                '--locked-mode',
                '-p', "RestorePackagesPath=$cacheRun",
                '-p', 'RestorePackagesWithLockFile=true'
            )

            if ($IncludeBrowserTests) {
                $arguments += @('-p', 'QtsBrowserTests=true')
            }

            $output = & dotnet @arguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                $failures += "hermetic restore of $project failed: $(@($output | Select-Object -Last 3) -join ' ')"
            }
        }

        return $failures
    }

    function Test-FeedIntegrity {
        param($Manifest)

        $failures = @()

        foreach ($entry in @($Manifest.packages)) {
            $path = Join-Path (Get-Location) $entry.file

            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                $failures += "the kit feed is missing $($entry.file) for $($entry.id) $($entry.version); re-run -Action Acquire"
                continue
            }

            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            if ($actual -ne $entry.sha256) {
                $failures += "$($entry.file) hash mismatch: recorded $($entry.sha256), found $actual"
            }

            $licence = "$($entry.licence)"
            $identity = "$($entry.id)/$($entry.version)"

            if (-not $licence) {
                $failures += "$($entry.id) $($entry.version) declares no licence at all; it cannot be redistributed without a review"
            }
            elseif ($reviewedLicences -notcontains $licence -and -not $reviewedLicenceExceptions.ContainsKey($identity)) {
                $failures += "$($entry.id) $($entry.version) carries an unreviewed licence '$licence' (add a reviewed exception with its reason, or do not ship it)"
            }
        }

        return $failures
    }

    function Test-RecordedWorkloads {
        param($Manifest)

        $recorded = @($Manifest.source.workloads)
        if ($recorded.Count -eq 0) {
            return @()
        }

        $installed = @(Get-InstalledWorkloads)
        $missing = @($recorded | Where-Object { $installed -notcontains $_ })

        if ($missing.Count -gt 0) {
            return @("the build kit requires workload(s) $(($missing -join ', ')) which this machine does not have (dotnet workload install ...)")
        }

        return @()
    }

    function Test-RecordedTools {
        param($Manifest)

        $failures = @()

        foreach ($tool in @($Manifest.source.toolManifest)) {
            $expected = "$($tool.id.ToLowerInvariant()).$($tool.version).nupkg"
            $path = Join-Path $feedDirectory $expected

            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                $failures += "the pinned tool '$($tool.id)' version '$($tool.version)' is not in the kit feed ($expected)"
                continue
            }

            if ($SkipToolInstall) {
                continue
            }

            $toolPath = Join-Path (Clear-KitDirectory -Path (Join-Path $KitRoot 'tools')) 'install'
            New-Item -ItemType Directory -Path $toolPath -Force | Out-Null

            # Proof, not a claim: the tool resolves and installs out of the sealed feed with no source left.
            $output = & dotnet tool install $tool.id --version $tool.version --tool-path $toolPath --add-source $feedDirectory --no-cache 2>&1
            if ($LASTEXITCODE -ne 0) {
                $failures += "'dotnet tool install $($tool.id)' from the kit feed failed: $(@($output | Select-Object -Last 3) -join ' ')"
            }
        }

        return $failures
    }

    function Test-NativeBinaries {
        param($Manifest)

        # SQLCipher is the device store's encryption: the native binary has to be present, and it is never
        # substituted with something else (decision D01).
        $native = @($Manifest.packages | Where-Object { $_.id -match 'sqlcipher|SQLitePCLRaw' } | Sort-Object -Property id -Unique)

        if ($native.Count -eq 0) {
            return @('the kit records no SQLCipher / SQLitePCLRaw native runtime package; the encrypted device store cannot be built offline')
        }

        $failures = @()
        foreach ($entry in $native) {
            if (-not (Test-Path -LiteralPath (Join-Path (Get-Location) $entry.file) -PathType Leaf)) {
                $failures += "native runtime package $($entry.id) is missing from the feed"
            }
        }

        return $failures
    }

    function Get-Manifest {
        param([string[]]$Projects, [object]$Packages)

        $projectRecords = @($Projects | ForEach-Object {
            $project = $_
            $direct = @(Get-ProjectDirectPackages -ProjectFile $project)

            [ordered]@{
                path    = $project
                lock    = (Join-Path (Split-Path $project -Parent) 'packages.lock.json').Replace('\', '/')
                direct  = @($direct | ForEach-Object { [ordered]@{ id = $_.Id; version = $_.Version } })
                packageCount = @($Packages | Where-Object { $_.project -eq $project }).Count
            }
        })

        return [ordered]@{
            schemaVersion = 1
            action        = $Action
            source        = Get-SourceProvenance
            projects      = $projectRecords
            packages      = @($Packages | ForEach-Object { $_ })
        }
    }

    # Wrapped explicitly: a single selected project must still behave like the collection the checks below count.
    $projects = @(Get-ProjectFiles -Restriction $Project)
    $failures = @()

    if ($Action -eq 'Acquire') {
        Write-Output "== acquiring the build kit for $($projects.Count) project(s) (connected)"

        foreach ($project in $projects) {
            # Connected restore, which also writes the lock file the admission check compares against.
            $arguments = @('restore', $project, '--nologo', '-p', 'RestorePackagesWithLockFile=true')
            if ($IncludeBrowserTests) {
                $arguments += @('-p', 'QtsBrowserTests=true')
            }

            $output = & dotnet @arguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "connected restore of $project failed: $(@($output | Select-Object -Last 4) -join ' ')"
            }
        }

        New-Item -ItemType Directory -Path $feedDirectory -Force | Out-Null

        # Seal the feed, then prove it: the hermetic restore is run here too, and every pack it still asks for is
        # added and tried again. A kit that only looks complete because nobody restored against it is not a kit.
        $sealedIds = @()
        $unresolvedIds = @()
        $cacheForRepair = Get-GlobalPackageCache

        for ($round = 1; $round -le 6; $round++) {
            $packages = Get-PackagesForAdmission -Projects $projects
            $restoreOutput = (@(Invoke-HermeticRestore -Projects $projects) -join "`n")
            $missing = Copy-MissingPackagesFromCache -RestoreOutput $restoreOutput -CacheRoot $cacheForRepair

            $sealedIds += $missing.Added
            $unresolvedIds = $missing.Unresolved

            if ($missing.Added.Count -eq 0) {
                $unresolvedIds = $missing.Unresolved
                break
            }
        }

        $packages = Get-PackagesForAdmission -Projects $projects
        $manifest = Get-Manifest -Projects $projects -Packages $packages

        # Tools come from the same reviewed feed: fetch them into the machine cache first so the copy finds them.
        # ('dotnet tool restore' has no --nologo switch; a failure here is not fatal because the manifest check
        # still proves the tool package is or is not in the feed.)
        & dotnet tool restore | Out-Null
        $cache = Get-GlobalPackageCache
        foreach ($tool in @((Get-ToolManifest))) {
            try {
                Copy-KitPackageTree -Id $tool.Id -Version $tool.Version -CacheRoot $cache | Out-Null
            }
            catch {
                $failures += "the pinned tool $($tool.Id) $($tool.Version) could not be added to the feed: $($_.Exception.Message)"
            }
        }

        # Re-read so the recorded set includes the tool packages.
        $packages = Get-PackagesForAdmission -Projects $projects
        $manifest = Get-Manifest -Projects $projects -Packages $packages

        New-Item -ItemType Directory -Path $KitRoot -Force | Out-Null
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

        Write-Output "   feed   : $feedDirectory ($((Get-ChildItem -LiteralPath $feedDirectory -File).Count) packages)"
        Write-Output "   sealed : $((@($sealedIds | Sort-Object -Unique)).Count) SDK-provided targeting/runtime pack(s) added from the machine cache"
        Write-Output "   notices: $KitRoot/notices"
        Write-Output "   manifest: $manifestPath"

        if ($unresolvedIds.Count -gt 0) {
            $failures += "these package(s) are needed by an offline restore but are in no feed, no cache and no SDK folder on this machine: $(($unresolvedIds | Sort-Object -Unique) -join ', ') - install the workload they come from and re-acquire"
        }

        # A seal is only real if the sealed restore still succeeds after everything was added.
        $finalRestore = @(Invoke-HermeticRestore -Projects $projects)
        foreach ($restoreFailure in $finalRestore) {
            $failures += $restoreFailure
        }

        if ($failures.Count -gt 0) {
            $failures | ForEach-Object { Write-Output "FAIL $_" }
            exit 1
        }

        Write-Output "== acquire complete; run -Action Verify offline to admit the kit"
        exit 0
    }

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Write-Output "FAIL no build kit manifest at ${manifestPath}: run 'pwsh -File scripts/prepare-build-kit.ps1 -Action Acquire' on a connected, reviewed machine first"
        exit 1
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    if ($Action -eq 'Inventory') {
        # Provenance from the committed lock files only: no network, no restore, deterministic output.
        $provenance = @($projects | ForEach-Object {
            $lockPath = Join-Path (Split-Path $_ -Parent) 'packages.lock.json'
            if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
                throw "$_ has no packages.lock.json; restore it (task 11 pins every retained project)"
            }

            $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
            $dependencies = @()
            foreach ($framework in $lock.dependencies.PSObject.Properties) {
                foreach ($package in $framework.Value.PSObject.Properties) {
                    $value = $package.Value

                    # A lock file records both NuGet packages (with a resolved version) and project references
                    # (with a type and no version). Only the packages are a dependency provenance record.
                    $resolved = if ($value -is [string]) { $value }
                        elseif ($value.PSObject.Properties['resolved']) { "$($value.resolved)" }
                        else { $null }

                    if (-not $resolved) {
                        continue
                    }

                    $dependencies += [ordered]@{
                        id      = $package.Name
                        version = $resolved
                    }
                }
            }

            [ordered]@{
                project        = $_
                targetFramework = $lock.version
                dependencies   = @($dependencies | Sort-Object -Property id -Unique)
            }
        })

        $record = [ordered]@{
            source       = Get-SourceProvenance
            kitManifest  = $manifestPath.Replace('\', '/')
            projectCount = $provenance.Count
            projects     = $provenance
        }

        $path = Join-Path $KitRoot 'artifact-provenance.json'
        New-Item -ItemType Directory -Path $KitRoot -Force | Out-Null
        $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
        Write-Output "== provenance recorded for $($provenance.Count) project(s): $path"
        exit 0
    }

    Write-Output "== admitting the sealed build kit (offline)"

    # Release any build node still holding a package file, so the empty-cache condition can be met.
    & dotnet build-server shutdown | Out-Null

    # 'no undeclared sources' has to be shown, not asserted: the effective configuration of this restore names the
    # kit feed and nothing reachable through a public URL. The .NET SDK's own workload folder is declared by the
    # pinned SDK and the recorded workload set, not by a NuGet source.
    $declaredSources = @(& dotnet nuget list source --configfile $offlineConfig 2>&1)
    $publicSource = @($declaredSources | Where-Object { $_ -match 'https?://' })
    if ($publicSource.Count -gt 0) {
        $failures += "the offline configuration still declares a public source: $(($publicSource -join ' '))"
    }

    $kitSource = @($declaredSources | Where-Object { $_ -match 'qts-build-kit' })
    if ($kitSource.Count -eq 0) {
        $failures += "the offline configuration does not expose the kit feed 'qts-build-kit'"
    }

    $failures += Test-FeedIntegrity -Manifest $manifest
    $failures += Test-RecordedWorkloads -Manifest $manifest
    $failures += Test-RecordedTools -Manifest $manifest
    $failures += Test-NativeBinaries -Manifest $manifest

    # A reviewed exception that no longer matches any package is a stale decision, not a passing check.
    $recorded = @($manifest.packages | ForEach-Object { "$($_.id)/$($_.version)" })
    foreach ($identity in @($reviewedLicenceExceptions.Keys | Sort-Object)) {
        if ($recorded -notcontains $identity) {
            $failures += "the reviewed licence exception '$identity' matches no package in the kit; remove or re-review it"
        }
    }

    $missingFromFeed = @($projects | Where-Object {
        $lockPath = Join-Path (Split-Path $_ -Parent) 'packages.lock.json'
        -not (Test-Path -LiteralPath $lockPath -PathType Leaf)
    })
    foreach ($project in $missingFromFeed) {
        $failures += "$project has no committed packages.lock.json, so its dependency set is unreviewed"
    }

    $restoreFailures = Invoke-HermeticRestore -Projects $projects
    $failures += $restoreFailures

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Output "FAIL $_" }
        Write-Output "== build kit admission FAILED ($($failures.Count) problem(s))"
        exit 1
    }

    Write-Output "   kit feed: $feedDirectory"
    Write-Output "   restore : locked mode, isolated cache, one source ($offlineConfig)"
    Write-Output "== build kit admission PASSED for $($projects.Count) project(s)"
    exit 0
}
finally {
    Pop-Location
}
