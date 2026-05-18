# Phase 3 — 3G: Service architecture polish

**Owner**: Wave 13 (sequential cleanup — last Phase 3 task to soak up incidental fixes)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3G
**Depends on**: Waves 10-12 landed (so we polish the FINAL Phase 3 state)
**Effort**: 1 week
**Risk**: LOW-MEDIUM (many small touches; bisect-friendly per-fix commits)

## Why

Audit D enumerates 4 small architectural smells. Each is independently shippable but they cluster around "service lifecycle + global state" — best done together so we don't churn the same files multiple times.

## What

4 sub-tasks (each its own commit):

### 3G-1: `SettingsLoader.Load/Save` static → `ISettingsStore` injection

17 call sites use `SettingsLoader.Load()` / `SettingsLoader.Save(...)` statically. Inject `ISettingsStore` via constructor. `RealSettingsStore` wraps the current static logic. `InMemorySettingsStore` for tests (fixes the documented `SettingsLoaderRobustnessTests` parallelism flake — Wave 7 noted this).

### 3G-2: 6 `static readonly HttpClient` fields → single `IHttpClient`

Phase 2D-3 introduced `IHttpClient`. 6 services still have their own `static readonly HttpClient` field (architectural audit §4). Migrate each to take `IHttpClient` via ctor with `new PolicyHttpClient()` as back-compat default.

### 3G-3: Fix `.Result` blocking call in `VpnEngine.cs:461`

A `Task.Run(...).Result` blocking call in StartAsync that should be `await`. Risk: thread-pool starvation under load. Fix: convert to `await`, update caller's signature to async if needed.

### 3G-4: `PlatformServices.CreateVpnEngine` factory enforcement

Some sites construct `VpnEngine` directly (`new VpnEngine(...)`), bypassing the platform-specific wiring in `PlatformServices.CreateVpnEngine`. Either: (a) make `VpnEngine` ctor `internal` + `InternalsVisibleTo` only the factory, or (b) add a `[Obsolete("Use PlatformServices.CreateVpnEngine")]` on the public ctor.

## How

For EACH sub-task: separate commit, separate verification gate.

**Step 1 (3G-1)**:
1. Define `ISettingsStore` in `VPNRouter.Core/Services/`
2. `RealSettingsStore` implements it via current static logic
3. Inject through DI / factory in 17 call sites (use grep + Edit)
4. `InMemorySettingsStore` in tests/Fakes/
5. Switch `SettingsLoaderRobustnessTests` to `InMemorySettingsStore` — verify flake disappears

**Step 2 (3G-2)**:
1. Grep `static readonly HttpClient` across solution
2. For each: add `IHttpClient` ctor param with `new PolicyHttpClient()` default
3. Verify Phase 2D-3 IHttpClient covers all required surfaces; extend interface if not

**Step 3 (3G-3)**:
1. Read VpnEngine.cs line ~461 to understand the blocking call
2. Convert `Task.Run(...).Result` → `await Task.Run(...)`
3. If caller signature must change, propagate the async upward
4. Verify HealthMonitorTimerRaceTests + HealthMonitorRecoveryGapTests still pass

**Step 4 (3G-4)**:
1. Grep `new VpnEngine(` across solution
2. Verify each call site IS `PlatformServices.CreateVpnEngine` internal
3. Mark public ctor `[Obsolete("Use PlatformServices.CreateVpnEngine")]` with hard error level after grace period

## Verification gate (per sub-task)

- [ ] 3G-1: ISettingsStore + 17 sites migrated + flake fixed (verified by running SettingsLoaderRobustnessTests 10× in a row, all pass)
- [ ] 3G-2: 6 `static HttpClient` sites consolidated
- [ ] 3G-3: VpnEngine.cs:461 blocking call eliminated; no new deadlocks
- [ ] 3G-4: Factory enforcement: no `new VpnEngine(` outside factory
- [ ] **Gate 1**: build 0 errors after each commit
- [ ] **Gate 2**: scoped suite green after each commit
- [ ] **Gate 4 simplify**: per-sub-task diff < 300 LOC (some grep+replace, some restructuring)
- [ ] **Gate 4 security-review**: 3G-2 (HTTP consolidation may consolidate auth token paths)
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 4 may extend ISettingsStore with file-watching + change events for live-reload scenarios.
- Document `PlatformServices` as the sole VpnEngine construction path in `VPNRouter.Core/CLAUDE.md`.
