# Dependency and build-kit inventory (specification task 11)

This is the reviewed dependency record for a self-hosted QtsBilling build. It exists so an operator can rebuild
the application on a machine with **no internet access at all** and know exactly what was used, where it came
from, and what may be redistributed. It is not a vulnerability report and it makes no claim of zero third-party
code (definition of done, task 11).

## 1. Pinned toolchain

| Item | Pin | Where it is enforced |
|---|---|---|
| .NET SDK | `10.0.401`, `rollForward: latestFeature` (same `10.0.4xx` band or higher patch, never a different feature band) | `global.json` |
| Shared runtimes on the acquiring machine | `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App` / `Microsoft.WindowsDesktop.App` `10.0.12` | recorded per acquisition in `build-kit/dependency-manifest.json` (`source.runtimes`) |
| Workload(s) for the Windows device shell | `maui-windows` (manifest `10.0.0/10.0.100`) | recorded in the manifest and re-checked by `-Action Verify`; the workload ships `Microsoft.Maui.*` from the SDK's own `library-packs` folder |
| Local tools | `dotnet-ef 10.0.13`, `rollForward: false` | `dotnet-tools.json`; the package is sealed in the kit and installed from it during verification |
| Restore graph | committed `packages.lock.json` for **all twelve** retained projects, enabled by `Directory.Build.props` | `dotnet restore … --locked-mode` fails when the graph drifts |

Nothing was upgraded to make this inventory: the reviewed set is exactly what the project files already asked for
(decision D12). A version change is a change to the kit and needs a fresh review plus a re-acquisition.

## 2. Direct dependencies by project

Production and device code:

| Project | Direct packages (reviewed versions) |
|---|---|
| `QtsModels` | none |
| `QtsBilling.Contracts` | `Microsoft.IdentityModel.JsonWebTokens 8.19.2` |
| `QtsBilling.Persistence.Postgres` | `Microsoft.EntityFrameworkCore 10.0.13`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.13`, `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `OpenIddict.EntityFrameworkCore 7.7.2` |
| `QtsBilling.Persistence.Sqlite` | `Microsoft.EntityFrameworkCore.Sqlite.Core 10.0.13`, `SQLitePCLRaw.bundle_e_sqlcipher 2.1.10` |
| `QtsBilling.Ui` | `MudBlazor 9.11.0` |
| `QtsWebApp` | `MudBlazor 9.11.0`, `Microsoft.AspNetCore.Components.WebAssembly.Server 10.0.13`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.13`, `Microsoft.AspNetCore.OpenApi 10.0.13`, `OpenIddict.AspNetCore 7.7.2`, `OpenTelemetry.Extensions.Hosting 1.19.1`, `OpenTelemetry.Exporter.Prometheus.AspNetCore 1.19.1-beta.1` |
| `QtsWebApp.Client` | `MudBlazor 9.11.0`, `Blazored.LocalStorage 4.5.0`, `Microsoft.AspNetCore.Components.WebAssembly 10.0.12`, `Microsoft.AspNetCore.WebUtilities 10.0.12`, `Microsoft.Extensions.Localization 10.0.12` |
| `QtsBilling.Maui` | `Microsoft.Maui.Controls 10.0.0` and `Microsoft.AspNetCore.Components.WebView.Maui 10.0.0` (both from the `maui-windows` workload), `Microsoft.Extensions.Logging.Debug 10.0.0`, `Microsoft.Extensions.Http 10.0.0`, `System.Security.Cryptography.ProtectedData 10.0.13`, `MudBlazor 9.11.0` |
| `QtsBilling.DataImport` | none (project references only) |

Build-time (development-only, never shipped): `Microsoft.EntityFrameworkCore.Design 10.0.13`.

Test-only (never in a client or device asset): `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.5`,
`Microsoft.NET.Test.Sdk 18.11.0`, `coverlet.collector 10.1.0`, `Moq 4.21.0`, `Newtonsoft.Json 13.0.4`,
`Bogus 35.6.5`, `Microsoft.AspNetCore.Mvc.Testing 10.0.13`, `Microsoft.Data.Sqlite.Core 10.0.13`,
`System.Security.Cryptography.Pkcs 10.0.13`, `System.Security.Cryptography.Xml 10.0.13`.

Optional browser-journey driver, restored only with `-p:QtsBrowserTests=true`: `Microsoft.Playwright 1.63.0`. It is
kept out of the default graph on purpose, and `TestQtsBilling/Operations/BuildKit.Tests.ps1` asserts that no
test-only driver or test framework appears in the lock file of anything a browser or device ships. The Playwright
**browser binaries** are not NuGet packages: an offline machine needs the separate browser kit, and that acquisition
lands with the release kit (task 42) and the acceptance run (task 45).

## 3. The sealed kit

```text
build-kit/
  packages/                 every .nupkg the restore graph needs, one flat local feed
  notices/<id>/<version>/   the licence text each package actually carries
  verify-cache/             the empty cache each hermetic restore starts from (per run)
  tools/                    proof that the pinned tool installs from the feed alone
  dependency-manifest.json  the reviewed record: committed
  artifact-provenance.json  per-project resolved versions: regenerated, not committed
```

Commands:

```powershell
# connected, on a reviewed machine, once per reviewed dependency set
pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Acquire

# offline, on the build machine: this is the admission check
pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Verify

# offline provenance from the committed lock files only
pwsh -NoProfile -File scripts/prepare-build-kit.ps1 -Action Inventory

# the focused task-11 tests (T11.01 - T11.03)
pwsh -NoProfile -File TestQtsBilling/Operations/BuildKit.Tests.ps1
```

`Acquire` does not trust itself: after copying every package the restore graph names, it runs the **same hermetic
restore that `Verify` uses** and adds whatever the SDK still asks for. Two such implicit packs were found this way
and are sealed in the feed rather than being left to a machine's accident:

| Implicit pack | Version | Why the graph does not name it |
|---|---|---|
| `Microsoft.Windows.SDK.NET.Ref` | `10.0.19041.57` | Windows targeting pack pulled by the MAUI/WinUI framework reference, not by a `PackageReference` |
| `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` | `10.0.12` | runtime pack the WebAssembly linker resolves for `QtsWebApp.Client` |

`Verify` then checks, in one run: the effective configuration exposes exactly one feed (`qts-build-kit`) and no
public URL; every recorded package file exists with the recorded SHA-256; every licence is either a reviewed SPDX
expression or a reviewed per-package exception; the pinned workload set and the pinned tool resolve; the SQLCipher
native runtime is present; and every retained project restores **in locked mode from an empty cache**. A missing
file, a changed byte, an unreviewed licence, an absent workload or a lost native pack each fails admission with the
offending package named.

## 4. Licences and redistribution

Reviewed SPDX expressions, accepted as a set: `MIT`, `Apache-2.0`, `BSD-3-Clause`, `PostgreSQL`,
`Apache-2.0 WITH LLVM-exception`.

Packages whose metadata names a bundled licence file or a URL instead of an SPDX expression are accepted **by
identity**, each with its reason, and any new one fails admission until it is reviewed here:

| Package | Declared licence | Position |
|---|---|---|
| `Microsoft.Graphics.Win2D 1.3.2` | URL (Microsoft Win2D EULA) | Microsoft redistributable, used only by the Windows device shell |
| `Microsoft.Web.WebView2 1.0.3179.45` | bundled `LICENSE.txt` | Redistributable WebView2 SDK, Windows device shell only |
| `Microsoft.WindowsAppSDK 1.7.250909003` | bundled `license.txt` | Windows App SDK runtime for the device shell |
| `Microsoft.Windows.SDK.BuildTools 10.0.22621.756` | URL (Windows SDK licence) | Build-time tooling; none of it is application code |
| `xunit.abstractions 2.0.3` | URL (xunit licence text) | Test-only, same terms as the reviewed `xunit` |
| `Bogus 35.6.5` | bundled `LICENSE` (MIT) | Test-only data generator |

`SQLitePCLRaw.bundle_e_sqlcipher 2.1.10` and `Microsoft.Data.Sqlite.Core` are kept as they are: SQLCipher is the
device store's encryption and is never substituted with another implementation (decision D01). Its native binaries
and their notices are part of the sealed feed, and the device key hierarchy stays as task 38 describes it.

Licence texts are extracted into `build-kit/notices/` during acquisition so a release can ship them without opening
each package again (task 42).

## 5. What this does not claim

* The manifest records the acquisition date as an **advisory snapshot**, not a scan. Nothing here states that no
  advisory exists against these versions; checking them is a separate, network-requiring action.
* No bit-for-bit reproducibility is claimed. The kit pins the *inputs* (SDK band, workload ids, package hashes,
  lock files); the release kit's signing and digest work (task 42) reports per-artifact differences rather than
  asserting identity without evidence.
* The feed itself (240 packages, ~0.4 GB) is **not committed** - `/build-kit/*` is ignored except the manifest. A
  fresh clone therefore runs `-Action Acquire` once on a connected, reviewed machine; that is the documented
  acquisition path, not a gap in the pinning.
* Full-solution build and test on both supported hosts is a gate-G7 activity; this task proves restore and
  admission, and says so.
