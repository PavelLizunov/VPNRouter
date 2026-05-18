# Phase 4 — ISettingsStore broader DI rollout

**Owner**: Wave 19 single agent
**Roadmap ref**: Phase 3G-1 left 14 call sites on static SettingsLoader
**Effort**: 1 day
**Risk**: MEDIUM (touches MainWindowViewModel + Program.cs + CLI commands)

## Why

Phase 3G-1 introduced `ISettingsStore` + `RealSettingsStore` (delegating
singleton facade) + `InMemorySettingsStore` test fake. 3 critical
production paths migrated to ctor injection. ~14 remaining call sites
still use `SettingsLoader.Load(...)` / `Save(...)` statically — they
work via the `RealSettingsStore` facade but bypass the DI testability
benefit.

Completing the migration:
- Enables direct unit-testing of MVM Load/Save logic without
  filesystem (use `InMemorySettingsStore`)
- Lets Phase 5 deprecate the static `SettingsLoader` API entirely
- Removes the singleton facade (currently a TODO)

## What

Migrate ~14 call sites to take `ISettingsStore` via ctor.

Expected call sites (grep first to confirm):
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (likely several
  `SettingsLoader.Load()` / `Save()` calls in Load/Save methods)
- `VPNRouter.App/ViewModels/MainWindowViewModel.*.cs` (partials)
- `VPNRouter.App/Program.cs` (boot)
- `VPNRouter.CLI/Program.cs` (boot)
- `VPNRouter.CLI/Commands/*.cs` (start / status / profile commands)
- Other test or tool consumers

For each:
1. Add `ISettingsStore _settingsStore` field
2. Add ctor param with `RealSettingsStore.Instance` default for back-compat
3. Replace `SettingsLoader.Load(path)` → `_settingsStore.Load(path)`
4. Replace `SettingsLoader.Save(s, path)` → `_settingsStore.Save(s, path)`

After migration:
- Mark `SettingsLoader.Load` / `SettingsLoader.Save` static methods
  `[Obsolete("Use ISettingsStore via DI — Phase 3G-1 migrated callers", error: false)]`. Sole suppression at `RealSettingsStore` itself (which delegates to the static API for back-compat until Phase 5 retires it).
- Consider deleting `RealSettingsStore.Instance` singleton if all
  callers now go through ctor injection. (May defer if some Phase 5
  retirement-locked code still needs it.)

## How

**Step 1** — Catalog all call sites:
```bash
grep -rnE "SettingsLoader\.(Load|Save)" VPNRouter.App VPNRouter.CLI --include="*.cs"
```

**Step 2** — Walk each call site, prefer ctor injection (use
RealSettingsStore.Instance default to preserve back-compat).

**Step 3** — For test classes that touch settings, switch to
`InMemorySettingsStore` from `VPNRouter.Tests/Fakes/`. This should
incidentally fix any remaining filesystem-race flakes (Wave 13's
flake fix was already targeted but more callers in tests means more
ISettingsStore usage = less filesystem race surface).

**Step 4** — `[Obsolete]` markers on the static SettingsLoader.Load/Save
methods. Sole approved suppression: `RealSettingsStore.cs`.

**Step 5** — Verify build 0/0 + scoped suite green + SettingsLoader-
adjacent tests stay green (especially the ones in
`SettingsLoaderRobustnessTests` / `SettingsValidatorTests` — they may
benefit from direct ISettingsStore injection).

## Verification gate
- [ ] All ~14 call sites migrated to ISettingsStore ctor injection
- [ ] SettingsLoader.Load/Save static methods `[Obsolete]` (warning-only)
- [ ] Test classes using settings switched to InMemorySettingsStore
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite green
- [ ] **Gate 4 simplify**: per-call-site diff is mechanical (ctor + 1-line replace)
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 5 deletes `RealSettingsStore.Instance` singleton + retires the
  static `SettingsLoader.Load/Save` methods entirely. Requires every
  caller to be ctor-injection-only.
- File-watcher contract on `ISettingsStore` (live-reload on disk change)
  could be a Phase 5 win.
