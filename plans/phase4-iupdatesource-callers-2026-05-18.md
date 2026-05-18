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
*(filled by agent)*

## Follow-up

- Phase 5 retires `UpdateChecker.CheckForUpdateAsync` once the obsolete
  warning period passes + no external callers remain.
- Play Store distribution: implement PlayStoreSource concrete (Phase 5,
  separate task — requires Play Console API account + signing key
  configuration).
