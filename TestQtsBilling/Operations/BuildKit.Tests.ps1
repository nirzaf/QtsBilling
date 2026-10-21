# Build kit admission tests (self-hosted SMB completion, specification task 11).
#
#   pwsh -NoProfile -File TestQtsBilling/Operations/BuildKit.Tests.ps1
#   pwsh -NoProfile -File TestQtsBilling/Operations/BuildKit.Tests.ps1 -SkipRestore   # source/contract checks only
#
# T11.01 a clean build user with no public network restores from the kit, with no undeclared source;
# T11.02 a changed package hash, a missing workload/native runtime or an unreviewed licence fails admission;
# T11.03 the same source and lock manifests record their provenance and their actual dependency versions.
#
# The failure cases are proven by injecting them into a throwaway kit, not by reading the script: each asserts
# the exact message the admission check emits and its non-zero exit. Nothing here reaches a public network, and
# no case is reported as passed because it was skipped.

[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [string]$KitRoot = 'build-kit'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repoRoot

$results = @()
$failures = @()

function Invoke-Kit {
    param([string[]]$Arguments)

    $output = & pwsh -NoProfile -File 'scripts/prepare-build-kit.ps1' @Arguments 2>&1
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (@($output) -join "`n") }
}

function Add-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)

    $script:results += [pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail }
    if (-not $Passed) {
        $script:failures += "$Name : $Detail"
    }
}

try {
    $manifestPath = Join-Path $KitRoot 'dependency-manifest.json'
    $offlineConfig = 'NuGet.Offline.Config'

    # ---------------------------------------------------------------- contract of the kit itself

    foreach ($required in @('global.json', $offlineConfig, 'Directory.Build.props', 'dotnet-tools.json', 'scripts/prepare-build-kit.ps1')) {
        Add-Result -Name "artifact $required exists" -Passed (Test-Path -LiteralPath $required) -Detail "expected $required in the repository"
    }

    $sdkPin = Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json
    Add-Result -Name 'the SDK is pinned with a bounded roll-forward' `
        -Passed ($sdkPin.sdk.version -match '^\d+\.\d+\.\d+$' -and $sdkPin.sdk.rollForward -in @('patch', 'feature', 'latestPatch', 'latestFeature')) `
        -Detail "global.json says version=$($sdkPin.sdk.version) rollForward=$($sdkPin.sdk.rollForward)"

    $installedSdk = (& dotnet --version).Trim()
    Add-Result -Name 'this machine satisfies the pinned SDK' `
        -Passed ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installedSdk)) `
        -Detail "dotnet --version answered '$installedSdk'"

    $config = Get-Content -LiteralPath $offlineConfig -Raw
    Add-Result -Name 'the offline configuration declares no public source' `
        -Passed ($config -match '<clear\s*/>' -and $config -notmatch 'value="https?://') `
        -Detail 'NuGet.Offline.Config must clear the sources and name only the kit feed'

    Add-Result -Name 'the offline configuration is not named NuGet.config' `
        -Passed (-not (Test-Path -LiteralPath 'NuGet.config') -and -not (Test-Path -LiteralPath 'nuget.config')) `
        -Detail 'a file named NuGet.config would silently change every connected restore'

    # Every retained project carries a lock file, because the property lives in Directory.Build.props.
    $projects = Get-ChildItem -Path . -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|build-kit|out|tmp)\\' }
    $withoutLock = @($projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json')) })
    Add-Result -Name 'every retained project has a packages.lock.json' `
        -Passed ($withoutLock.Count -eq 0 -and $projects.Count -ge 12) `
        -Detail "$($projects.Count) project(s); missing lock: $(($withoutLock | ForEach-Object { $_.Name }) -join ', ')"

    # The test-only browser driver must stay out of anything a client ships (specification task 02 and 10).
    $clientProjects = @('QtsWebApp/QtsWebApp.Client', 'QtsBilling.Ui', 'QtsBilling.Maui') |
        ForEach-Object { Get-ChildItem -Path $_ -Recurse -File -Filter 'packages.lock.json' }
    $leaked = @($clientProjects | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Microsoft\.Playwright|"xunit|Moq|Bogus|Microsoft\.NET\.Test\.Sdk' })
    Add-Result -Name 'no test-only driver reaches a shipped client lock' `
        -Passed ($leaked.Count -eq 0) `
        -Detail "leak in: $(($leaked | ForEach-Object { $_.FullName }) -join ', ')"

    # A browser-flag restore rewrites TestQtsBilling's lock with the Playwright driver. The committed record must
    # stay the reviewed default graph, otherwise an offline build would need a package that only the optional
    # browser journey consumes.
    $testLock = Get-Content -LiteralPath 'TestQtsBilling/packages.lock.json' -Raw
    Add-Result -Name 'the committed test lock is the default graph, without the optional browser driver' `
        -Passed ($testLock -notmatch 'Microsoft\.Playwright') `
        -Detail 'run -Action Acquire without -IncludeBrowserTests to restore this record'

    # ---------------------------------------------------------------- T11.01 restore from the sealed kit

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Result -Name 'T11.01 the kit has been acquired' -Passed $false `
            -Detail "no ${manifestPath}: run 'pwsh -File scripts/prepare-build-kit.ps1 -Action Acquire' first; nothing is claimed without it"
    }
    else {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

        Add-Result -Name 'T11.01 the manifest records every package with a hash and a licence' `
            -Passed (@($manifest.packages).Count -gt 0 -and @($manifest.packages | Where-Object { -not $_.sha256 -or -not $_.licence }).Count -eq 0) `
            -Detail "$(@($manifest.packages).Count) package(s) recorded"

        if ($SkipRestore) {
            Write-Output 'note: -SkipRestore given, the hermetic restore and the injected-failure cases were NOT RUN'
        }
        else {
            # A real offline admission over the sealed feed: locked mode, an empty package cache, one source.
            $restoreTargets = @(
                'QtsBilling.Contracts/QtsBilling.Contracts.csproj',
                'QtsBilling.Persistence.Sqlite/QtsBilling.Persistence.Sqlite.csproj'
            )

            $restoreFailures = @()
            foreach ($target in $restoreTargets) {
                $run = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $KitRoot, '-SkipToolInstall', '-Project', $target)
                if ($run.Exit -ne 0) {
                    $restoreFailures += "$target -> exit=$($run.Exit) $((@($run.Text -split "`n") | Select-Object -Last 3) -join ' | ')"
                }
            }

            Add-Result -Name 'T11.01 an isolated locked restore resolves from the kit alone' `
                -Passed ($restoreFailures.Count -eq 0) `
                -Detail ($restoreFailures -join ' ;; ')

            # ...and the pinned tool installs out of the same feed, with no source left.
            $withTool = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $KitRoot, '-Project', 'QtsModels/QtsModels.csproj')
            Add-Result -Name 'T11.01 the pinned dotnet-ef tool installs from the kit feed' `
                -Passed ($withTool.Exit -eq 0) `
                -Detail "exit=$($withTool.Exit) $(($withTool.Text -split "`n" | Select-Object -Last 3) -join ' | ')"

            # ---------------------------------------------------------------- T11.02 injected defects
            $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('qts-buildkit-tests-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $scratch 'packages') -Force | Out-Null

            try {
                # A tampered package file: the recorded hash no longer matches what is in the feed.
                $victim = @($manifest.packages | Where-Object { $_.file -and (Test-Path -LiteralPath (Join-Path (Get-Location) $_.file)) }) | Select-Object -First 1
                $tampered = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                $tampered.packages[0].sha256 = ('F' * 64)
                $tampered | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Encoding utf8

                $hashRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 a changed package hash fails admission' `
                    -Passed ($hashRun.Exit -ne 0 -and $hashRun.Text -match 'hash mismatch') `
                    -Detail "exit=$($hashRun.Exit) $(($hashRun.Text -split "`n" | Where-Object { $_ -match 'hash mismatch' } | Select-Object -First 1))"

                # A package the feed does not contain at all.
                $absent = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                $absent.packages[0].file = 'build-kit/packages/does-not-exist.1.0.0.nupkg'
                $absent | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Encoding utf8

                $absentRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 a missing package in the feed fails admission' `
                    -Passed ($absentRun.Exit -ne 0 -and $absentRun.Text -match 'the kit feed is missing') `
                    -Detail "exit=$($absentRun.Exit) $(($absentRun.Text -split "`n" | Where-Object { $_ -match 'kit feed is missing' } | Select-Object -First 1))"

                # A licence nobody reviewed.
                $unreviewed = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                $unreviewed.packages[0].licence = 'AGPL-3.0-only'
                $unreviewed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Encoding utf8

                $licenceRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 an unreviewed licence fails admission' `
                    -Passed ($licenceRun.Exit -ne 0 -and $licenceRun.Text -match 'unreviewed licence') `
                    -Detail "exit=$($licenceRun.Exit) $(($licenceRun.Text -split "`n" | Where-Object { $_ -match 'unreviewed licence' } | Select-Object -First 1))"

                # A workload the acquiring machine recorded and this machine does not have.
                $workload = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                $workload.source.workloads = @('maui-totally-made-up')
                $workload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Encoding utf8

                $workloadRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 a missing workload fails admission' `
                    -Passed ($workloadRun.Exit -ne 0 -and $workloadRun.Text -match 'requires workload') `
                    -Detail "exit=$($workloadRun.Exit) $(($workloadRun.Text -split "`n" | Where-Object { $_ -match 'requires workload' } | Select-Object -First 1))"

                # The encrypted device store's native runtime, removed from the record.
                $native = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                $native.packages = @($native.packages | Where-Object { $_.id -notmatch 'sqlcipher|SQLitePCLRaw' })
                $native | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Encoding utf8

                $nativeRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 a missing SQLCipher native runtime fails admission' `
                    -Passed ($nativeRun.Exit -ne 0 -and $nativeRun.Text -match 'SQLCipher') `
                    -Detail "exit=$($nativeRun.Exit) $(($nativeRun.Text -split "`n" | Where-Object { $_ -match 'SQLCipher' } | Select-Object -First 1))"

                # A manifest that does not exist at all cannot be admitted.
                Remove-Item -LiteralPath (Join-Path $scratch 'dependency-manifest.json') -Force
                $emptyRun = Invoke-Kit -Arguments @('-Action', 'Verify', '-KitRoot', $scratch, '-SkipToolInstall',
                    '-Project', 'QtsModels/QtsModels.csproj')
                Add-Result -Name 'T11.02 a kit with no manifest fails admission' `
                    -Passed ($emptyRun.Exit -ne 0 -and $emptyRun.Text -match 'no build kit manifest') `
                    -Detail "exit=$($emptyRun.Exit) $(($emptyRun.Text -split "`n" | Select-Object -Last 2) -join ' | ')"
            }
            finally {
                Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
            }

            # ---------------------------------------------------------------- T11.03 provenance

            $first = Join-Path $scratch 'first.json'
            $second = Join-Path $scratch 'second.json'
            New-Item -ItemType Directory -Path $scratch -Force | Out-Null

            $null = Invoke-Kit -Arguments @('-Action', 'Inventory', '-KitRoot', $KitRoot, '-Project', 'QtsBilling.Ui/QtsBilling.Ui.csproj')
            $provenancePath = Join-Path $KitRoot 'artifact-provenance.json'
            Add-Result -Name 'T11.03 the provenance record is written' `
                -Passed (Test-Path -LiteralPath $provenancePath -PathType Leaf) `
                -Detail "expected $provenancePath"

            if (Test-Path -LiteralPath $provenancePath -PathType Leaf) {
                Copy-Item -LiteralPath $provenancePath -Destination $first -Force
                $null = Invoke-Kit -Arguments @('-Action', 'Inventory', '-KitRoot', $KitRoot, '-Project', 'QtsBilling.Ui/QtsBilling.Ui.csproj')
                Copy-Item -LiteralPath $provenancePath -Destination $second -Force

                # Identical dependency versions from the same source and locks; only the snapshot date may differ.
                $a = (Get-Content -LiteralPath $first -Raw) -replace '"advisorySnapshotUtc":.*', ''
                $b = (Get-Content -LiteralPath $second -Raw) -replace '"advisorySnapshotUtc":.*', ''
                Add-Result -Name 'T11.03 the same source and locks record the same dependency set' `
                    -Passed ($a -eq $b) `
                    -Detail 'two Inventory runs over the same locks produced different provenance'

                $record = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
                $ui = @($record.projects | Where-Object { $_.project -eq 'QtsBilling.Ui/QtsBilling.Ui.csproj' }) | Select-Object -First 1
                $unversioned = @($ui.dependencies | Where-Object { -not $_.version }).Count

                Add-Result -Name 'T11.03 the record names the actual resolved versions of every dependency' `
                    -Passed ($null -ne $ui -and @($ui.dependencies).Count -gt 0 -and $unversioned -eq 0) `
                    -Detail "ui packages=$(@($ui.dependencies).Count), without a version: $unversioned"

                Add-Result -Name 'T11.03 the record carries its source provenance' `
                    -Passed ("$($record.source.gitCommit)" -match '^[0-9a-f]{40}$' -and "$($record.source.sdkVersion)" -match '^\d+\.\d+\.\d+$') `
                    -Detail "git=$($record.source.gitCommit) sdk=$($record.source.sdkVersion) workloads=$(($record.source.workloads) -join '+') advisoryNote=$($record.source.advisoryNote)"

                $lockText = Get-Content -LiteralPath 'QtsBilling.Ui/packages.lock.json' -Raw
                $mud = @($ui.dependencies | Where-Object { $_.id -eq 'MudBlazor' }) | Select-Object -First 1
                Add-Result -Name 'T11.03 the recorded version is the one the lock file actually pins' `
                    -Passed ($null -ne $mud -and $lockText -match [regex]::Escape($mud.version)) `
                    -Detail "MudBlazor recorded as '$($mud.version)' against the committed lock"
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Output ''
Write-Output '== build kit admission tests (specification task 11) =='
foreach ($result in $results) {
    Write-Output ("  {0} {1}" -f ($(if ($result.Passed) { 'PASS' } else { 'FAIL' })), $result.Name)
    if (-not $result.Passed) {
        Write-Output "       $($result.Detail)"
    }
}

Write-Output ''
Write-Output "   recorded: $($results.Count)  failed: $($failures.Count)"

if ($failures.Count -gt 0) {
    Write-Output '== RESULT: FAIL =='
    exit 1
}

Write-Output '== RESULT: PASS =='
exit 0
