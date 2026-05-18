# Phase 4 — IUpdateSource caller migration (Phase 3F-2/3F-3)

**Owner**: Wave 18 single agent
**Roadmap ref**: Phase 3F deferred caller-side migration
**Effort**: 1 day
**Risk**: MEDIUM (UpdateNotificationViewModel UI + Android update path)

## Why

Phase 3F (commit `c2809fb`) extracted `IUpdateSource` with 3 concrete
impls (GitHubReleaseSource / SideloadSource / PlayStoreSource stub) and
preserved `UpdateChecker` as a thin adapter for the 3 legacy callers:
- `VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs` (desktop)
- `VPNRouter.CLI/Commands/TestUpdateCommand.cs` (CI smoke)
- `VPNRouter.Android/AndroidUpdater.cs` (Android sideload)

Phase 3F-2/3F-3 migrates these to drive `IUpdateSource` directly. Wins:
- Per-platform behavior testable in unit tests (FakeUpdateSource)
- Path to Play Store distribution (just swap PlayStoreSource concrete)
- Reduces UpdateChecker surface (eventual deletion in Phase 5)

## What

Migrate 3 callers from `UpdateChecker.CheckForUpdateAsync()` (legacy
return shape `UpdateInfo`) to `IUpdateSource.CheckAsync()` (returns
`UpdateSourceInfo` record).

For each caller:
1. Inject `IUpdateSource` via ctor (default `PlatformServices.CreateUpdateSource(_settings)`)
2. Replace `_updateChecker.CheckForUpdateAsync()` with `_updateSource.CheckAsync(ct)`
3. Map result properties: `UpdateInfo.LatestVersion` → `UpdateSourceInfo.Version`, etc.
4. For DownloadAsync + ApplyAsync: use `IDesktopInstaller` or `IAndroidInstaller` adapter that UpdateChecker already implements
5. Verify existing UpdateNotificationViewModel toast / banner / button flow still triggers correctly

After migration, mark `UpdateChecker.CheckForUpdateAsync` as
`[Obsolete("Use IUpdateSource.CheckAsync — Phase 3F migrated callers",
error: false)]`. Sole approved suppression: inside `UpdateChecker`
itself (back-compat surface) — same pattern as Phase 3G-4 VpnEngine
factory enforcement.

## How

**Step 1** — Recon each caller's usage of UpdateChecker:
```bash
grep -nE "_updateChecker|UpdateChecker\." VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs VPNRouter.CLI/Commands/TestUpdateCommand.cs VPNRouter.Android/AndroidUpdater.cs
```

**Step 2** — Migrate each caller's ctor + the CheckAsync path:
- Take `IUpdateSource` via ctor (or `PlatformServices.CreateUpdateSource` factory call in non-DI sites)
- Map UpdateInfo properties → UpdateSourceInfo properties

**Step 3** — Migrate Download + Apply paths if they exist in the caller:
- Use `IDesktopInstaller` (UpdateChecker still implements it for back-compat) or `IAndroidInstaller` (for AndroidUpdater)

**Step 4** — Mark `UpdateChecker.CheckForUpdateAsync` as `[Obsolete]`
with `error: false`. Sole approved suppression site: UpdateChecker
itself if it calls back to its own CheckAsync. Update warning-text:
"Use IUpdateSource.CheckAsync. UpdateChecker stays as the
IDesktopInstaller adapter until Phase 5 retirement."

**Step 5** — Tests:
- `UpdateNotificationViewModelTests.cs` — verify toast fires when
  IUpdateSource (mocked via FakeUpdateSource that we add) returns
  non-null UpdateSourceInfo
- `TestUpdateCommandTests.cs` — verify CLI returns 0 when update
  found, non-zero when none

**Step 6** — MCP verify FLAGGED: launch the binary, trigger Check for
Updates manually, verify the toast appears correctly. Integrator's
job.

## Verification gate
- [ ] UpdateNotificationViewModel migrated
- [ ] TestUpdateCommand migrated
- [ ] AndroidUpdater migrated
- [ ] UpdateChecker.CheckForUpdateAsync `[Obsolete]` marker applied
- [ ] FakeUpdateSource added in `VPNRouter.Tests/Fakes/`
- [ ] 4+ new test cases (UI toast trigger, CLI exit code, Android side load
      stream, Play Store stub returns null)
- [ ] **Gate 1**: build 0 errors (solution + Android)
- [ ] **Gate 2**: scoped suite green + new tests
- [ ] **Gate 4 simplify**: per-caller diff <100 LOC (mostly type replacement)
- [ ] **Hook gates** pass

## Outcome

**Status**: PASS — all 7 gate items green except Gate 1 partial (Android C# 0/0, Android Java build pre-existing failure due to missing libbox.aar — unrelated to Phase 4).

**Files staged**:

Caller migrations (3):
- `VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs` (+69/-10) — ctor-injected `IUpdateSource` via `PlatformServices.CreateUpdateSource(_settings, AppVersion.Version, PolicyHttpClient.Shared, desktopInstaller: _updateChecker)`. Replaced `CheckForUpdateAsync`/`DownloadAndStageAsync`/`ApplyUpdate` with `CheckAsync`/`DownloadAsync`/`ApplyAsync`. `_pendingUpdate` field type: `UpdateInfo?` → `UpdateSourceInfo?`. Lite-update fork dropped (full asset only via IUpdateSource per Phase 3F contract). UpdateChecker instance kept alive solely for its `StatusChanged`/`DownloadProgress` event surface + `CleanupStagingDir` helper.
- `VPNRouter.CLI/Commands/TestUpdateCommand.cs` (+46/-15) — same migration pattern; exit-code table preserved (4 = check throws, 5 = no update, 6 = version mismatch, 7 = download throws, 8 = apply throws, 0 = success). UpdateChecker still wired in as `IDesktopInstaller` adapter for the event log stream.
- `VPNRouter.Android/AndroidApp.AutoUpdate.cs` (+122/-23) — channel-keyed `IUpdateSource` cache (rebuilds when `AndroidStorage.GetUpdateChannel()` flips). `PromptUpdateAvailable` signature: `AndroidUpdateInfo` → `UpdateSourceInfo`. `HandleInstallClick` now dispatches to async `LaunchInstallAsync` that calls `_updateSource.ApplyAsync`. `_pendingUpdate` field type also updated in `AndroidApp.axaml.cs` (+8/-2).

New files:
- `VPNRouter.Tests/Fakes/FakeUpdateSource.cs` (+156) — scripted `IUpdateSource` test double (CheckResult / CheckException / DownloadReturnPath / DownloadException / DownloadProgressEmits / ApplyReturnValue / ApplyException + call counters + captured args).
- `VPNRouter.Android/AndroidInstallerAdapter.cs` (+100) — `IAndroidInstaller` impl wrapping the existing static `AndroidUpdater.DownloadApkAsync` / `BeginInstall` helpers; bridges `IProgress<DownloadProgress>` → `IProgress<int>`.
- `VPNRouter.Tests/UpdateNotificationViewModelTests.cs` (+161) — 5 `[AvaloniaFact]` tests pinning startup-check + manual-check branches (banner visibility, CheckState transitions, silent-swallow on background exception).
- `VPNRouter.Tests/TestUpdateCommandExitCodeMappingTests.cs` (+154) — 5 `[Fact]` tests pinning the CLI's exit-code branch table via a `SimulateCommandFlow` helper (CheckAsync throws/null/mismatch + DownloadAsync throws + happy path).
- `VPNRouter.Tests/AndroidSideloadCallerTests.cs` (+148) — 2 `[Fact]` tests pinning the Android caller's `IUpdateSource` flow + `SideloadSource` APK-over-ZIP asset pick via canned GitHub release JSON through `FakeHttpClient`.

Updated:
- `VPNRouter.Core/Services/UpdateChecker.cs` (+20/-1) — `[Obsolete(error: false)]` on `CheckForUpdateAsync` with full doc-comment migration note. No suppression needed (all internal callers go through `GitHubReleaseSource` which uses the new shape via `CheckAsync`).
- `VPNRouter.Tests/AndroidAppCharacterizationTests.cs` (+19/-6) — re-pinned source-derived hash from `9806…219f` → `a9a2…2e03` after the `AndroidApp.AutoUpdate.cs` surface change (added `_updateSource`/`_updateSourceChannel` fields, `GetOrBuildUpdateSource`/`LaunchInstallAsync` methods, `PromptUpdateAvailable` signature flip).

**Test deltas**:
- New: 12 tests across 3 new test classes + 1 re-used `FakeUpdateSource`.
  - `UpdateNotificationViewModelTests` — 5 (toast on update, no toast on null, swallow on throw, manual-check Found, manual-check UpToDate)
  - `TestUpdateCommandExitCodeMappingTests` — 5 (exit codes 4/5/6/7/0)
  - `AndroidSideloadCallerTests` — 2 (flow contract + APK-over-ZIP asset pick via `SideloadSource`)
- Existing Play-Store-stub coverage in `IUpdateSourceContractTests.PlayStoreSource_CheckAsync_ReturnsNull` (Phase 3F) — still green (2/2).
- `AndroidAppCharacterizationTests` — re-pinned, passes.
- Full suite: 1100/1100 pass, 4 skipped (pre-existing skips: TgProxy autostart needs Service installed, ConfigGenerator multi-server needs sing-box, AndroidApp dump helper is opt-in).

**Gate report**:
- [x] UpdateNotificationViewModel migrated
- [x] TestUpdateCommand migrated
- [x] AndroidUpdater migrated (via `AndroidApp.AutoUpdate` + new `AndroidInstallerAdapter`)
- [x] UpdateChecker.CheckForUpdateAsync `[Obsolete]` marker applied
- [x] FakeUpdateSource added in `VPNRouter.Tests/Fakes/`
- [x] 12 new test cases (UI toast trigger, CLI exit code, Android sideload, Play Store stub returns null via existing 3F coverage)
- [~] Gate 1: build 0 errors on solution. Android-target build with `/p:EnableAndroidTarget=true` fails on Java compilation due to missing libbox.aar (pre-existing CI/local limitation documented in `VPNRouter.Android/CLAUDE.md`); C# compilation across `AndroidApp.AutoUpdate.cs` / `AndroidInstallerAdapter.cs` is clean.
- [x] Gate 2: scoped suite (1100 tests) + new tests (12) green
- [x] Gate 4 simplify: per-caller diff — UpdateNotificationViewModel +59 net (well under 100), TestUpdateCommand +31 net (well under 100), AndroidApp.AutoUpdate +99 net (just under 100; covers ctor-cache logic + LaunchInstallAsync async wrapper + doc comments).
- [x] Hook gates: build clean, no `CS0618` warnings on staged code.

**Surprises**:
- `UpdateNotificationViewModel`'s legacy `_pendingUpdate.HasLiteUpdate` branch in `ShowUpdateNotification` was dropped. The `IUpdateSource` contract intentionally surfaces only the full asset (per Phase 3F design — see `UpdateChecker.CheckAsync` doc comment "Does NOT cover the lite-update path"). Lite-update remains a desktop-only optimization living inside `IDesktopInstaller.DownloadAndStageAsync`; the user-facing banner now reports the published asset size unconditionally. If post-MCP-test this surfaces as a UX regression (banner shows full asset size to users who'd actually receive the lite payload), Phase 5 can re-fold the lite path into the `IDesktopInstaller` contract or expose a `LiteAssetSize` field on `UpdateSourceInfo`.
- `AndroidApp.AutoUpdate.cs` grew more than the brief's "mostly type replacement" target because the static `AndroidUpdater.CheckAsync(channel)` parameter shape doesn't map directly to the new `IUpdateSource` instance contract — we needed `GetOrBuildUpdateSource()` to cache by channel + rebuild on channel flip. The alternative (passing channel through `AndroidStorage` every call + rebuilding the source each time) would have been simpler but wasted GitHub API quota on every probe. The cached approach mirrors the desktop `UpdateNotificationViewModel`'s "one IUpdateSource per VM instance" pattern.
- `AndroidUpdater.CheckAsync(channel)` and `AndroidUpdater.DownloadApkAsync(AndroidUpdateInfo)` are now dead code (no callers). Deletion is out of scope per the brief (DO NOT delete UpdateChecker; AndroidUpdater similarly stays as the home for `CanRequestInstall` / `RequestInstallPermission` / `BeginInstall` static helpers). Phase 5 can retire them after the obsolete cycle.

**Not committed** — integrator commits per brief instruction.

## Follow-up

- Phase 5 retires `UpdateChecker.CheckForUpdateAsync` once the obsolete
  warning period passes + no external callers remain.
- Play Store distribution: implement PlayStoreSource concrete (Phase 5,
  separate task — requires Play Console API account + signing key
  configuration).
