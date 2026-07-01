# Corrected decomposition — Phase 2 "Build-Spine" (UPDATE-NET10-MIGRATION)

## Why the current plan can't build

The current Phase 2 is a linear chain of **build-gated** stages:

```
2.1 SDK Pin + Single-TFM Directory.Build.props   (deps: none)   ← runs `dotnet build` on the whole repo
2.2 CPM Package Swap (Directory.Packages.props)  (deps: 2.1)
2.3 NuGet.Config Local Feeds                     (deps: 2.2)
2.4 Remove packages.config + Restore-Green       (deps: 2.3)
```

Two ordering inversions make 2.1 impossible to pass:

1. **Package/feed inversion.** 2.1 flips the TFM to `net10.0-windows` and immediately runs a whole-repo `dotnet build`, but the package versions it needs (2.2) and the local feeds that restore them (2.3) are scheduled *after* it. Observed on the live run: 2.1 build-failing iters 4/6/7/8 with **CS0246/CS0234 ×20** (`System.Management` WMI types), **CS0012 ×2** (MEF `ExportAttribute`), and an unresolvable asz-infra/HCI graph.
2. **Warnings-as-errors inversion.** 2.1 keeps `TreatWarningsAsErrors=true`, but the cleanup for those warnings is deferred to **Phase 5** (5.1 culture/cast = CA1305/CA1307/CA1310/CA2101; 5.2 WarningsAsErrors policy). So even after packages+feeds resolve, the gate stays red on **SYSLIB0057 ×6 / CS0618 ×8 / CA1305 ×12 / CA1307 ×8 / CA1310 ×4 / CA2101 ×4** — all promoted to errors, none scheduled to be fixed until the last phase.

The TFM flip, the CPM version swap, and the NuGet.Config feeds are **one atomic buildable unit** — a whole-repo build can't restore or compile with any one missing. They must ship together, behind a single gate, with the migration-noise warnings deferred (not treated as errors) until Phase 5 cleans them up.

---

## Corrected Phase 2

### Stage 2.1: Build-Spine Bring-Up — SDK Pin, Single-TFM, CPM Swap, Local Feeds  (MERGES old 2.1+2.2+2.3)

**Implementation Steps**
- Add repo-root `global.json` pinning the net10 SDK (`10.0.x`, `rollForward: latestFeature`) — attachment Sec.7.1.
- `Directory.Build.props`: replace `<TargetFrameworks>net472</TargetFrameworks>` with a single `<TargetFramework>net10.0-windows</TargetFramework>`; drop net472/net8 RID conditions and binding-redirect nodes. Keep `SelfContained=false`, `LangVersion=latest`, linked `WindowsPlatform.AssemblyInfo.cs` (CA1416), test `CopyLocalLockFileAssemblies=true`. Drop the `update2` multi-target scaffolding + `NU1701;NU1608` NoWarn.
- **Bridge the warnings-as-errors gap so bring-up isn't blocked by work the plan defers to Phase 5.** In `Directory.Build.props` add, tagged `<!-- TEMP: removed in Stage 5.2 once 5.1 hardening lands -->`:
  `<WarningsNotAsErrors>SYSLIB0057;SYSLIB0051;CS0618;CA1305;CA1307;CA1310;CA1304;CA2101</WarningsNotAsErrors>`
  Keep `TreatWarningsAsErrors=true` for every other / newly-introduced warning.
- `Directory.Packages.props` (CPM): remove all `Microsoft.AzureStack.Common.Infrastructure.*` pins EXCEPT the two Telemetry pins; add the granular `Microsoft.AzureStack.HCI.Infrastructure.*` pins at `$(AszInfraPackageVersion)` (Sec.6.3); bump `Microsoft.Management.Infrastructure` to `3.0.0`; single-version `System.Text.Json`; single net10 test SDKs (`Microsoft.NET.Test.Sdk 17.x` + `MSTest 3.x`); keep `Download`/`VSR` `5.0.9001` pins.
- **Add the packages the single-TFM flip newly requires that were BCL under net472** (this is the direct fix for the CS0246/CS0234/CS0012 errors):
  - `System.Management` — the code uses `ManagementObject`/`ManagementObjectSearcher`/`ManagementScope` (WMI); on net10 that is a **separate Windows-only NuGet package**, NOT `Microsoft.Management.Infrastructure` (MI is a different API — bumping MI alone does not resolve these).
  - `System.ComponentModel.Composition` (MEF `ExportAttribute`).
  - Re-home the other facade warnings surfaced by the flip as needed (`System.IO.Compression(.FileSystem)`, `System.ComponentModel.DataAnnotations`, `System.Net.Http`).
- `NuGet.Config` at repo root (`update2`'s config with `LocalCommInfraFeed` → `AszInfraFeed` = `E:\work\hci\asz-infra2\out\packages`; keep `DownloadFeed`, `VsrFeed`, `EdgeManagementFeed`, `InfraHealthFeed`, official `ASZ-UpdateService-Consumption`), leading `<clear/>` — Sec.11.0. Stage the five `out\packages` folder feeds as the committed/generated folder-feed fixture.

**Dependencies**
- none — start stage

**Gate (single, post-change)**
- `dotnet restore` resolves against the local feeds with **zero missing-package errors**, THEN `dotnet build` is **green** (with the Phase-5-owned rules deferred per the bridge above).

**Test Scenarios** (union of old 2.1/2.2/2.3)
- Single TFM: exactly one `<TargetFramework>` = `net10.0-windows`; no net472/net8/RID/binding-redirect nodes.
- CPM pins: `Common.Infrastructure.*` removed except Telemetry; `HCI.Infrastructure.*` prefix set present at `$(AszInfraPackageVersion)`.
- **New**: `System.Management` + `System.ComponentModel.Composition` are pinned (the net472-BCL types the flip breaks).
- NuGet.Config: the 5 local feeds + consumption feed present after `<clear/>`.
- Folder-feed restore: for a `net10.0-windows` project the asz-infra + Download/VSR/Edge/InfraHealth graph resolves with no missing-package errors.
- **New**: the temporary `<WarningsNotAsErrors>` bridge is present and carries the `TEMP: removed in Stage 5.2` marker (so it can't silently outlive Phase 5).

### Stage 2.2: Remove Last packages.config and Restore-Green  (was Stage 2.4)

**Dependencies**
- phase-build-spine/stage-build-spine-bring-up

(unchanged otherwise)

---

## Downstream rewiring

- Old 2.2 / 2.3 stage slugs are removed; their content is absorbed into the merged bring-up stage.
- Only old 2.4 depended on the trio → now depends on `…/stage-build-spine-bring-up`.
- No stage outside Phase 2 depends on old 2.1/2.2/2.3 directly.
- **Phase 5 closes the bridge**: Stage 5.1 fixes the CA globalization + obsolete-API code (CA1305/1307/1310/2101, SYSLIB0057, CS0618); Stage 5.2 **removes the temporary `<WarningsNotAsErrors>`** and re-tightens `TreatWarningsAsErrors`. Add that removal explicitly to 5.2's steps + a scenario asserting the bridge is gone, so warnings-as-errors is fully restored where the plan already intends.

## Capability-gap flag (for the architect)

The code's WMI usage is the **old** `System.Management` API (`ManagementObjectSearcher`, `ManagementObject`), not MI. The plan's CPM step bumps `Microsoft.Management.Infrastructure` but that does **not** satisfy `using System.Management;`. Decide per the migration spec: (a) add the `System.Management` compat package (fastest, Windows-only — matches `net10.0-windows`), or (b) rewrite to MI in a Phase-3 infra stage. Bring-up should take (a) to get green; (b) if chosen is a separate code-migration stage, not a packaging change.
