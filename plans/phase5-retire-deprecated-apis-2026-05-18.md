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

- [ ] `UpdateChecker.CheckForUpdateAsync` deleted (grep-verified zero callers)
- [ ] `SettingsLoader.Load/Save` static methods deleted OR escalated to
      `error: true` Obsolete (with documented `#pragma` exceptions for
      the 2 known callers)
- [ ] `AndroidUpdater.CheckAsync(channel)` + `DownloadApkAsync` deleted
      if subsumed by Wave 18 adapter
- [ ] Build 0 errors
- [ ] Scoped suite green
- [ ] Hook gates pass

## Outcome
*(filled by agent)*

## Follow-up

- Delete `RealSettingsStore.Instance` singleton when full DI rollout
  ready (probably Phase 6 once Program.cs + AndroidApp.Notifications.cs
  callers migrate).
