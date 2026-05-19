# Phase 6 — Refactor SettingsLoader.Load/Save to internal-only

**Owner**: Wave 27 agent
**Roadmap ref**: Phase 5 rollup follow-up #3 + #7
**Effort**: 1 day
**Risk**: MEDIUM (touches SettingsLoader + ISettingsStore + tests)

## Why

Phase 4/5 marked `SettingsLoader.Load` + `SettingsLoader.Save` as
`[Obsolete(error: false)]`. Wave 24 confirmed zero external callers
but kept the methods because escalation to `error: true` is blocked
by CS0619 (NOT pragma-suppressible — Roslyn limitation). The 4
legitimate suppression sites are:
- `RealSettingsStore` delegation (`ISettingsStore.cs`)
- In-file internal callers (5 sites in `SettingsLoader.cs`)
- `SettingsLoaderRobustnessTests` pin tests (file-scope #pragma)
- `SettingsValidatorTests` pin tests (file-scope #pragma)

Phase 6 path: make `SettingsLoader.Load` + `SettingsLoader.Save`
`internal` (drop `public` + `[Obsolete]`). The `RealSettingsStore`
delegation site already lives in the same `VPNRouter.Core` assembly,
so it sees the internal methods directly. Test classes get
`InternalsVisibleTo("VPNRouter.Tests")` (already configured).

This eliminates the [Obsolete] noise + the #pragma blocks + closes
the migration loop started in Phase 3G-1.

## What

### 6-SL-1: Refactor

`VPNRouter.Core/Services/SettingsLoader.cs`:
- `public static AppSettings Load(string path)` → `internal static AppSettings Load(string path)`
- `public static void Save(AppSettings settings, string path)` → `internal static`
- Remove the 2 `[Obsolete(...)]` attributes entirely
- Remove the 5 `#pragma warning disable CS0618` blocks (no longer needed — internal methods don't emit obsolete warnings to in-assembly callers)

`VPNRouter.Core/Services/ISettingsStore.cs`:
- `RealSettingsStore.Load(string path)` and `.Save(...)` now call the internal `SettingsLoader.Load/Save` directly
- Remove the `#pragma warning disable CS0618` block around the delegations

### 6-SL-2: Test file updates

`VPNRouter.Tests/SettingsLoaderRobustnessTests.cs`:
- Remove `#pragma warning disable CS0618` (the file-scope one at the top)

`VPNRouter.Tests/SettingsValidatorTests.cs`:
- Same.

`VPNRouter.Core/VPNRouter.Core.csproj`:
- Verify `<InternalsVisibleTo Include="VPNRouter.Tests" />` is present
  (should be — Phase 1 era)
- If missing, add it

### 6-SL-3: Optional — retire `RealSettingsStore.Instance` singleton

Current `RealSettingsStore.Instance` is a back-compat singleton that
the unmigrated `Program.cs ResetToDefaults()` + `AndroidApp.Notifications.cs ConsumeRecoveryNotice()`
might use. Those don't use `Load/Save` though — they use
`ResetToDefaults` + `ConsumeRecoveryNotice` which are non-deprecated
static methods.

Grep proof:
```bash
grep -rnE "RealSettingsStore\.Instance" VPNRouter.* --include="*.cs"
```

If callers exist:
- Migrate them to ctor-injected `ISettingsStore` (like Wave 19 did
  for the 15 main call sites), OR
- Keep `RealSettingsStore.Instance` if migration is heavyweight

If zero callers: delete `RealSettingsStore.Instance` static property.

## How

**Step 1**: Edit `SettingsLoader.cs` — change `public static` to
`internal static` on `Load` + `Save`. Remove `[Obsolete]` attributes.

**Step 2**: Remove 5 `#pragma` blocks inside `SettingsLoader.cs` and
1 inside `ISettingsStore.cs`.

**Step 3**: Build — expect 0 errors (internal callers in same
assembly + tests via InternalsVisibleTo).

**Step 4**: Remove file-scope `#pragma` from
`SettingsLoaderRobustnessTests.cs` + `SettingsValidatorTests.cs`.

**Step 5**: Run scoped suite — must pass (existing pin tests still
target the same static methods, just now internal).

**Step 6**: Grep `RealSettingsStore.Instance` — if zero callers,
delete the singleton; else document why kept.

## Verification gate

- [ ] SettingsLoader.Load/Save now `internal static`
- [ ] 2 `[Obsolete]` attributes removed
- [ ] 6 `#pragma warning disable CS0618` blocks removed (5 in SettingsLoader + 1 in ISettingsStore + 2 file-scope in tests = 8 actually, but tests stay if helpful)
- [ ] Build 0 errors
- [ ] Scoped suite green (SettingsLoaderRobustness + SettingsValidator + ISettingsStoreContract all green)
- [ ] `RealSettingsStore.Instance` audited (deleted if zero callers OR rationale documented)
- [ ] Hook gates pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 7: full DI rollout for the remaining `RealSettingsStore.Instance`
  consumers if any are left.
