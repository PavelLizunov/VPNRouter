# Phase 5 — Retire deprecated `[Obsolete]` APIs

**Owner**: Wave 24 agent
**Roadmap ref**: Phase 3F/3G/4 follow-ups (UpdateChecker + SettingsLoader)
**Effort**: 1 day
**Risk**: LOW (the [Obsolete] period has passed; remaining callers documented)

## Why

Phase 3F (commit `c2809fb`) + Wave 18 marked `UpdateChecker.CheckForUpdateAsync`
as `[Obsolete(error: false)]`. Phase 3G-1 + Wave 19 marked
`SettingsLoader.Load` / `SettingsLoader.Save` as `[Obsolete(error: false)]`.

The warning period has held. No internal callers remain (all migrated
via IUpdateSource and ISettingsStore respectively). Phase 5 deletes
the obsolete methods + retires the back-compat surface.

## What

### 5-RD-1: Delete `UpdateChecker.CheckForUpdateAsync`
- Remove the `[Obsolete]` legacy method from `UpdateChecker.cs`
- Verify zero callers via grep
- Keep `UpdateChecker` itself as the `IDesktopInstaller`/`IAndroidInstaller`
  adapter — it's still wired by `UpdateNotificationViewModel`/`TestUpdateCommand`/
  `AndroidApp.AutoUpdate` for the download+apply paths
- May also delete `AndroidUpdater.CheckAsync(channel)` and
  `AndroidUpdater.DownloadApkAsync(AndroidUpdateInfo)` if Wave 18's
  AndroidInstallerAdapter has fully subsumed them

### 5-RD-2: Retire `SettingsLoader.Load` + `SettingsLoader.Save` static methods
- ~2 remaining call sites (per Phase 3G-1 Wave 19 outcome doc):
  - `VPNRouter.App/Program.cs:80 ResetToDefaults()` — fallback for `--reset`
    flag in static Main. Migrate to ISettingsStore via service locator OR
    keep as the sole approved internal caller (`#pragma warning disable`)
  - `VPNRouter.Android/AndroidApp.Notifications.cs:60 ConsumeRecoveryNotice()` —
    Android has parallel AndroidStorage path; can migrate or keep `#pragma`
- After both call sites resolved (migrated OR `#pragma`-ed), make the
  obsolete static methods `[Obsolete(error: true)]` OR delete them
- Keep `RealSettingsStore` singleton + `ISettingsStore.Load/Save`
  delegations — those are the new canonical surface

### 5-RD-3: Delete `RealSettingsStore.Instance` singleton if all DI complete
- Audit grep for `RealSettingsStore.Instance`
- If only `RealSettingsStore` self-references + a tiny static fallback
  remain, leave singleton in place
- If migrate-able, delete singleton — force every caller to inject

## How

**Step 1** — Grep for all callers:
```bash
grep -rnE "UpdateChecker\.CheckForUpdateAsync|SettingsLoader\.(Load|Save)" VPNRouter.* --include="*.cs"
```

**Step 2** — For each remaining caller, migrate OR `#pragma`. Document
in Outcome which path chosen.

**Step 3** — Delete `[Obsolete]` methods. Verify build 0 errors.

**Step 4** — Run scoped suite + sing-box check integration to verify
nothing breaks via reflection / dynamic invocation.

**Step 5** — Update brief Outcome with grep-verified zero-caller proof.

## Verification gate

- [x] `UpdateChecker.CheckForUpdateAsync` deleted (grep-verified zero callers)
- [x] `SettingsLoader.Load/Save` static methods deleted OR escalated to
      `error: true` Obsolete (with documented `#pragma` exceptions for
      the 2 known callers)
  - **Outcome**: kept at `error: false`. The CS0619 "obsolete-as-error"
    diagnostic is NOT pragma-suppressible (Roslyn limitation), so
    escalation would force a refactor that breaks the four legitimate
    suppression sites (`RealSettingsStore` delegation, in-file internal
    callers, `SettingsLoaderRobustnessTests`, `SettingsValidatorTests`).
    Marker updated with Phase 5 doc + rationale; zero external callers
    re-verified via grep. Deletion-and-reintroduce-as-internal-only
    flagged as Phase 6 candidate.
- [x] `AndroidUpdater.CheckAsync(channel)` + `DownloadApkAsync` deleted
      if subsumed by Wave 18 adapter
  - `CheckAsync(channel)` — **DELETED** (zero callers; `AndroidApp.AutoUpdate`
    migrated to `IUpdateSource.CheckAsync` via `SideloadSource` in Wave 18).
  - `DownloadApkAsync(AndroidUpdateInfo)` — **KEPT** (still called by
    `AndroidInstallerAdapter.DownloadApkAsync` which bridges
    `IAndroidInstaller` onto this static helper). `BeginInstall`,
    `CanRequestInstall`, `RequestInstallPermission` kept for the same
    reason (platform-only Intent / FileProvider / PackageManager surface).
- [x] Build 0 errors (`dotnet build VPNRouter.sln -c Release` — 0 warnings, 0 errors)
- [x] Scoped suite green (1121 passed / 4 skipped / 0 failed across
      ~1125-test scoped run; sing-box check integration 3/3 pass)
- [x] Hook gates pass

## Outcome

**Wave 24 status: complete.**

### Deletions

| Target | Result | Verification |
|---|---|---|
| `UpdateChecker.CheckForUpdateAsync` | **DELETED** | `grep -rE "UpdateChecker\.CheckForUpdateAsync\s*\("` → 0 matches |
| `UpdateChecker.UpdateAvailable` event | **DELETED** | sole consumer was `CheckForUpdateAsync`; zero subscribers in app code |
| `UpdateChecker` private DTOs (`GitHubRelease`, `GitHubAsset`, `GitHubApiJsonOptions`) | **DELETED** | only used inside the deleted method; `GitHubReleaseSource` owns the canonical copies |
| `UpdateChecker.FindFullAsset` / `FindLiteAsset` / `FindChecksumAsset` / `IsSharedRuntimeInstall` / `PlatformSuffix` | **DELETED** | only called from `CheckForUpdateAsync`; `GitHubReleaseSource` / `SideloadSource` host the surviving equivalents |
| `AndroidUpdater.CheckAsync(channel)` | **DELETED** | `grep -rE "AndroidUpdater\.CheckAsync\s*\("` → 0 matches |
| `AndroidUpdater._httpCheck` field + `FindApkAsset` helper | **DELETED** | only `CheckAsync` consumed them |

### Preserved (in-scope but flagged as still load-bearing)

- `UpdateChecker.CheckAsync(ct)` — public surface that `UpdateNotificationViewModel`
  + `TestUpdateCommand` rely on; delegates to `GitHubReleaseSource`.
- `UpdateChecker.DownloadAndStageAsync(UpdateInfo, ct)` — invoked by
  `IDesktopInstaller.DownloadAndStageAsync` (explicit interface impl).
- `UpdateChecker.ApplyUpdate(string)` / `CleanupStagingDir` / Win/Mac/Linux
  helpers — `IDesktopInstaller` surface.
- `AndroidUpdater.DownloadApkAsync(AndroidUpdateInfo, ...)` — called by
  `AndroidInstallerAdapter`.
- `AndroidUpdater.BeginInstall` / `CanRequestInstall` /
  `RequestInstallPermission` — `IAndroidInstaller` plumbing + `AndroidApp.AutoUpdate`
  permission-gate callers.
- `SettingsLoader.Load` + `SettingsLoader.Save` — still warning-only
  `[Obsolete]`. RealSettingsStore + tests + internal SettingsLoader
  callers depend on the delegation surface. Pragma sites stay as
  `#pragma warning disable CS0618`. Doc + attribute message updated
  to record Phase 5 re-verification.

### The "2 remaining `SettingsLoader` call sites" reconciliation

The brief cited `VPNRouter.App/Program.cs:80 ResetToDefaults()` and
`VPNRouter.Android/AndroidApp.Notifications.cs:60 ConsumeRecoveryNotice()`
as the two known external callers. Re-grep showed those are calling
`ResetToDefaults` / `ConsumeRecoveryNotice` — **neither method is
marked `[Obsolete]`**. They sit on `SettingsLoader` as part of the
non-deprecated static surface alongside `ConsumePlaceholderPruneNotice`,
`Parse`, `StartWatching`, `StopWatching`, `LastRecoveryNotice`. No
migration needed for either site; the brief's reference appears to
conflate "files touching SettingsLoader" with "files calling Load/Save".

Final grep proof for the actually-deprecated `Load`/`Save` static
methods (excluding documented suppression sites):

```
grep -rnE "SettingsLoader\.(Load|Save)\s*\(" --include="*.cs"
  ISettingsStore.cs:127         # RealSettingsStore.Load delegation (#pragma)
  ISettingsStore.cs:131         # RealSettingsStore.Save delegation (#pragma)
  SettingsLoader.cs:244,395,420 # internal Parse/migration/prune saves (#pragma)
  SettingsLoader.cs:575         # ScheduleReload internal call (#pragma)
  SettingsLoader.cs:609         # ResetToDefaults internal call (#pragma)
  SettingsLoader.cs:711         # WriteExample internal call (#pragma)
  SettingsValidatorTests.cs:331,358   # pin tests, file-scope #pragma
  SettingsLoaderRobustnessTests.cs:108..488  # pin tests, file-scope #pragma
```

Zero external production callers remain.

### Build + test deltas

- `dotnet build VPNRouter.sln -c Release` → **0 Warning(s), 0 Error(s)**.
  (`VPNRouter.Android` excluded from the default solution build — its
  workload isn't installed on this dev VM. Android-only source files
  do not reference the deleted symbols; static analysis via grep
  confirms `AndroidApp.AutoUpdate.cs` still calls only the kept
  helpers — `CanRequestInstall`, `RequestInstallPermission`,
  `_updateSource.CheckAsync`.)
- `dotnet test ... --filter "FullyQualifiedName!~Headless&!~PageScreenshot&!~VisualDiff"`
  → **Passed: 1121, Failed: 0, Skipped: 4, Total: 1125, Duration: 38s**.
- `dotnet test ... --filter "FullyQualifiedName~SingBoxCheck"`
  → **Passed: 3, Failed: 0** (sing-box check integration green —
  no reflection / dynamic-invocation regressions).
- LOC delta: **-349 lines** (105 insertions, 454 deletions across 5
  files; bulk from `UpdateChecker.cs` -228 net and `AndroidUpdater.cs`
  -148 net).
- Test count: unchanged (no behaviour change; pure dead-code retirement).

### Files changed

| File | Change |
|---|---|
| `VPNRouter.Core/Services/UpdateChecker.cs` | Deleted `CheckForUpdateAsync` + `UpdateAvailable` event + `GitHubRelease`/`GitHubAsset` DTOs + `GitHubApiJsonOptions` + `FindFullAsset` / `FindLiteAsset` / `FindChecksumAsset` / `IsSharedRuntimeInstall` + `PlatformSuffix` field. Updated class doc + remove unused `System.Text.Json` / `System.Text.Json.Serialization` usings. |
| `VPNRouter.Android/AndroidUpdater.cs` | Deleted `CheckAsync(channel)` + `_httpCheck` + `FindApkAsset` helper + `GitHubRepo` const. Updated class doc to describe the survival rationale (Phase 5). Removed unused `System.Text.Json` using. |
| `VPNRouter.Core/Services/SettingsLoader.cs` | `[Obsolete]` markers on `Load` / `Save` updated with Phase 5 doc + rationale (kept `error: false` because CS0619 not pragma-suppressible). No code change. |
| `VPNRouter.Tests/SettingsLoaderRobustnessTests.cs` | Comment update (Phase 5 note). |
| `VPNRouter.Tests/SettingsValidatorTests.cs` | Comment update (Phase 5 note). |

## Follow-up

- Delete `RealSettingsStore.Instance` singleton when full DI rollout
  ready (probably Phase 6 once Program.cs + AndroidApp.Notifications.cs
  callers migrate).
- **Phase 6 candidate**: refactor `SettingsLoader.Load`/`Save` from
  obsolete-public-static to internal-only helpers behind
  `ISettingsStore`. That allows hard removal of the public surface
  (no `[Obsolete]` warning leakage to external assemblies) while
  preserving the legitimate internal-delegation pattern. Requires
  moving the two test suites onto a test-only internal accessor
  (e.g. friend-assembly `InternalsVisibleTo` already exists for
  `VPNRouter.Tests` — exposing `SettingsLoader.LoadCoreForTest` /
  `SaveCoreForTest` as `internal static` would suffice).
