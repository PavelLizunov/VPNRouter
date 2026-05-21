# Task #41 Stage 2 — App-side two-phase VM Start timer

**Date**: 2026-05-21
**Owner**: PinkuDani session
**Branch**: main (direct commit)
**Predecessor brief**: `plans/phase4-vpnengine-connected-event-stage1-2026-05-21.md`
(commit `b012fe6` — Stage 1, typed `VpnEngine.Connected` event)
**Effort**: ~2 hours
**Risk**: LOW (additive helper + 3 callsite rewrites, all gated by existing
catches; helper is internal + unit-tested in isolation)
**Blast radius**: 1 new helper file · 1 new test file · 3 modified call sites
in MVM partials · 2 localization strings
**Rollback**: `git revert <commit>` — clean revert. Helper is standalone;
removing it does not affect anything else.

## Why

The original "Fix #2" specification (PinkuDani 2026-05-21) wanted to split
the App-side VM Start timer into Phase A (sing-box launch budget) +
Phase B (TUN warm-up budget). A prior attempt at that change was correctly
REFUSED because the only existing "Connected" signal was the ambiguous
`StatusChanged` string `"Connected (PID N)"` — emitted by
`StartupPipeline.ScheduleWarmupProbe` on BOTH success (line ~1100) AND
failure (line ~1138) branches.

Stage 1 (`b012fe6`, 2026-05-21) closed that gap by adding a typed
`VpnEngine.Connected` event fired ONLY from the success branch. Stage 2
(this change) consumes that event to implement the split-budget timer.

### Pre-Stage-2 state (before this commit)

3 callsites used a single 60s `CancellationTokenSource` wrapping the entire
`StartAsync` invocation:

| File:line | Site |
|---|---|
| `MainWindowViewModel.cs:3785` | `ToggleConnectionAsync` (main Connect) |
| `MainWindowViewModel.cs:5158` | `ReconnectAsync` retry loop (each retry) |
| `MainWindowViewModel.FreeConfigs.cs:129` | `ApplyFreeConfigAsync` |

`ToggleConnectionAsync` followed `StartAsync` with a 10s `_engine.IsRunning`
poll on a thread-pool thread (v2.20.6) — a workaround for the macOS
3-second `IsClashApiAlive` UI-thread block. With Stage 2 the polling is
gone: the typed `Connected` event is the authoritative routability signal.

### Stage 2 budgets

* **Phase A: 60s** — wait for `SingBoxStarted` event. If we hit the budget,
  sing-box never spawned (real hang in `DeployAndSetupFirewall` / wintun
  / netsh / `TunAdapterDiagnostics`). Same 60s value as the pre-Stage-2
  single budget — keeps the Win10 LTSC / missing-NetAdapter PowerShell
  module class hardening (Fix #1, commit `2f2c1a8`).
* **Phase B: 20s** — wait for `Connected` event after `SingBoxStarted`.
  TUN warm-up probe `ScheduleWarmupProbe` loops up to 15 attempts × ~1s.
  Happy-path completion is sub-5s on healthy installs; 20s gives a
  generous backstop for slow networks without making real driver hangs
  feel locked-up.

If Phase A times out → distinct diagnostic ("sing-box failed to spawn").
If Phase B times out → distinct diagnostic ("TUN warm-up failed").
Total wall-clock cap: 80s (vs. pre-Stage-2 60s + 10s polling = 70s).
Within ±10s of pre-Stage-2 ceiling, but now with surgical diagnostics.

## What

### A. New helper — `TwoPhaseStartCoordinator`

**File**: `VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs`
(new). Internal static class that accepts:

* `Task startTask` — the `_engine.StartAsync(...)` invocation.
* `Func<Action<int>, Action> subscribeStarted` — wires the
  `SingBoxStarted` handler; returns the unsubscribe lambda.
* `Func<Action<int>, Action> subscribeConnected` — wires the
  `Connected` handler; returns the unsubscribe lambda.
* `TimeSpan? phaseABudget` (default 60s) + `TimeSpan? phaseBBudget`
  (default 20s).
* `CancellationToken cancellationToken` — outer cancel.

Returns `TwoPhaseStartOutcome` enum:

* `Connected` — Phase A + Phase B both passed; caller flips
  `IsConnected = true`.
* `StartTaskCompleted` — `startTask` returned BEFORE either event fired
  (instant-throw like `ConflictingVpnException`); caller awaits the task
  to surface the exception.
* `PhaseATimeout` — sing-box never spawned; caller fires `Stop()` +
  Phase A diagnostic.
* `PhaseBTimeout` — sing-box up but TUN never confirmed; caller fires
  `Stop()` + Phase B diagnostic.
* `Cancelled` — outer CT tripped.

Design notes:

* Subscriptions are attached BEFORE awaiting anything; unsubscribe runs in
  `finally` regardless of outcome — pinned by the
  `SubscriptionsUnhookedOnTimeout` test.
* `TaskCreationOptions.RunContinuationsAsynchronously` on the internal
  TCS pair so a hot subscriber doesn't reentrantly drive Phase B from
  Phase A's call frame.
* `Task.WhenAny` includes `startTask` so an early-throw maps cleanly to
  `StartTaskCompleted` without waiting for Phase A's full budget.
* Helper does NOT call `_engine.Stop()` on timeout — caller owns shutdown
  so all teardown logic stays in one place.
* Helper is decoupled from `VpnEngine` itself (accepts lambdas, not a
  typed `VpnEngine` reference) — this is what unblocks unit-testing
  without needing an `IVpnEngine` interface (out-of-scope refactor).

### B. Localized status strings

**File**: `VPNRouter.Core/Localization/Strings.cs` (+ pass-through in
`VPNRouter.App/Localization/Strings.cs`):

```csharp
public static string StartTimeoutPhaseA => Ru
    ? "Таймаут запуска (60 с). Sing-box не стартовал."
    : "Start timed out (60s). sing-box never started.";

public static string StartTimeoutPhaseB => Ru
    ? "Таймаут TUN (20 с). Запуск не завершён."
    : "TUN warm-up timed out (20s). Start incomplete.";
```

Match existing style ("Таймаут при запуске (60 сек)" pattern from earlier
Fix #2 commit).

### C. Three callsite rewrites

#### C.1 `MainWindowViewModel.cs:3776+` — main `ToggleConnectionAsync`

Replaced the single-CTS + 10s polling pattern with `TwoPhaseStartCoordinator.RunAsync`.
Five branch handlers map outcome → UI state:

* `Connected` → `IsConnected = true`, clear conflict banner, etc.
* `StartTaskCompleted` → await `startTask` to surface exception; leave UI
  state to `OnEngineStatus`.
* `PhaseATimeout` → Stop engine + log + set `Strings.StartTimeoutPhaseA`.
* `PhaseBTimeout` → Stop engine + log + set `Strings.StartTimeoutPhaseB`.
* `Cancelled` → Stop engine + Phase A diagnostic (conservative default
  when there's no signal at all).

Existing `TunOwnershipException` / `ConflictingVpnException` /
`OperationCanceledException` catches preserved — the `OCE` catch's status
text now uses `Strings.StartTimeoutPhaseA` instead of the stale "Таймаут
при запуске (60 сек)" inline string.

#### C.2 `MainWindowViewModel.cs:5147+` — `ReconnectAsync` retry loop

Each retry attempt wrapped in the coordinator. Phase A / B timeouts log +
set status + return immediately (don't retry on these — they're not
`TunOwnershipException`-class). `await startTask` after a successful
`Connected` / `StartTaskCompleted` so any latent exception re-enters the
outer catch for the retry loop.

#### C.3 `MainWindowViewModel.FreeConfigs.cs:124+` — `ApplyFreeConfigAsync`

Same pattern. Phase A / B timeouts return `false` (the apply-button
handler treats `false` as failure and unspins the button). Status text
updated for both phases so the user sees which phase failed.

### D. Tests — `MvmTwoPhaseStartTimerTests.cs`

**File**: `VPNRouter.Tests/MvmTwoPhaseStartTimerTests.cs` (new). 8 tests
driven entirely against the coordinator helper — NO `VpnEngine` instance
constructed.

Tests use a `FakeEngineEvents` inline class that records subscribe /
unsubscribe counts AND lets the test body fire `SingBoxStarted` /
`Connected` synchronously. Production callers pass actual
`_engine.SingBoxStarted +=` / `-=` closures; tests pass closures that fire
against a stored handler reference. Same shape, no `IVpnEngine` needed.

| Test | Pin |
|---|---|
| `PhaseA_SingBoxStartsBefore60s_ProceedsToPhaseB` | Phase A success → Phase B success → `Connected`. Tight 500ms budgets. |
| `PhaseA_NoSingBoxIn60s_PhaseATimeout` | No `SingBoxStarted` → `PhaseATimeout`. Elapsed time check. |
| `PhaseB_ConnectedFiresBefore20s_ReturnsConnected` | `SingBoxStarted` then `Connected` within budget → `Connected`. |
| `PhaseB_NoConnectedIn20s_PhaseBTimeout` | `SingBoxStarted` but no `Connected` → `PhaseBTimeout`. |
| `PreCancelled_ReturnsCancelled` | Pre-cancelled CT → `Cancelled`. |
| `StartTaskFaultsBeforeEvents_ReturnsStartTaskCompleted` | `startTask` exception before events → `StartTaskCompleted`; caller surfaces by awaiting. |
| `SubscriptionsUnhookedOnTimeout` | Unsubscribe always runs regardless of outcome (handler-leak defence). |
| `DefaultBudgets_Are60sPhaseA_And20sPhaseB` | Default budget values pinned. |

## Did test fakery require production change?

**NO.** Helper accepts `Func<Action<int>, Action>` subscribe/unsubscribe
lambdas. Production passes `handler => { _engine.SingBoxStarted += w; ... }`;
tests pass `handler => { _fakeHandler = handler; ... }`. The seam is in
the helper's signature itself, not in `VpnEngine`. No `IVpnEngine` was
extracted — that would have been scope creep per the brief's refuse-to-proceed
rule.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → **0 errors / 0 new warnings**
  (incremental rebuild).
- [x] New tests alone: `MvmTwoPhaseStartTimerTests` — **8/8 pass / 1 s**.
- [x] Related VM tests: `MvmTwoPhaseStartTimerTests` (8) +
  `VpnEngineConnectedEventTests` (4) + `ViewModelTests` (7) — **19/19 pass /
  1 s**.
- [x] Full suite (excluding GUI/Visual + known-flaky `VpnEngineLifecycleTests` /
  `SingBoxManagerProcessRunnerTests` / `HealthMonitorTunOrphanRestartTests` /
  `VpnEngineSplitTunnelLifecycleTests` / `VpnEngineHotReloadLifecycleTests`
  on this dev VM):  **1328/1328 + 4 skipped / 1 m 3 s**.
- [x] Each excluded suite passes individually in isolation:
  * `VpnEngineLifecycleTests`: 9/9
  * `SingBoxManagerProcessRunnerTests`: 7/7
  * `VpnEngineHotReloadLifecycleTests` (parallel agent's new file): 4/4
  * `VpnEngineSplitTunnelLifecycleTests` (parallel agent's new file): 3/3
  * `HealthMonitorTunOrphanRestartTests`: pre-existing
* Their full-suite-run instability is documented in `VPNRouter.Tests/CLAUDE.md`
  "Headless tests — known issues" and pre-dates this commit (confirmed
  with `git stash` baseline run on `b012fe6`).

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs` | +245 LOC (new). `TwoPhaseStartOutcome` enum + static `RunAsync` helper. |
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | ~+85 LOC (refactor). Main `ToggleConnectionAsync` rewrite + matching `ReconnectAsync` retry-loop rewrite. |
| `VPNRouter.App/ViewModels/MainWindowViewModel.FreeConfigs.cs` | ~+45 LOC (refactor). `ApplyFreeConfigAsync` rewrite. |
| `VPNRouter.Core/Localization/Strings.cs` | +15 LOC. `StartTimeoutPhaseA` + `StartTimeoutPhaseB` strings. |
| `VPNRouter.App/Localization/Strings.cs` | +3 LOC. Pass-through getters. |
| `VPNRouter.Tests/MvmTwoPhaseStartTimerTests.cs` | +260 LOC (new). 8 tests against the coordinator. |
| `plans/phase4-vm-two-phase-timer-stage2-2026-05-21.md` | This brief. |

### Build + test numbers

* Build: **0 errors / 0 new warnings** (preexisting MVVMTK0034 +
  CA1416 + xUnit1051 noise from other files).
* New tests alone: **8 passed / 0 failed / 1 s**.
* Combined critical-path: **19 passed / 0 failed / 1 s** (8 new + 4
  Stage 1 + 7 ViewModelTests).
* Full suite (excluding known-flaky lifecycle suites on this dev VM):
  **1328 passed / 4 skipped / 0 failed / 63 s**.

### Status text wording (final copy)

| Phase | RU | EN |
|---|---|---|
| A | "Таймаут запуска (60 с). Sing-box не стартовал." | "Start timed out (60s). sing-box never started." |
| B | "Таймаут TUN (20 с). Запуск не завершён." | "TUN warm-up timed out (20s). Start incomplete." |

### Surprises

* The existing 10-second `_engine.IsRunning` polling in `ToggleConnectionAsync`
  (v2.20.6 macOS-block workaround) becomes redundant when `Connected`
  drives the wait. Dropping it net-saves 10s on the warmup-failed path
  (engine reaches `Connected`-or-give-up much faster than the 10s
  Thread.Sleep loop).
* The `OperationCanceledException` catch was kept and rewritten to use
  `Strings.StartTimeoutPhaseA` as a safe fallback — Stage 2's coordinator
  outcomes mostly avoid hitting it now, but if a deep StartAsync call
  surfaces OCE post-`StartTaskCompleted` (or the outer CTS race) we
  still want a user-facing diagnostic.
* No `IVpnEngine` extraction needed. The lambda-based seam in
  `TwoPhaseStartCoordinator.RunAsync(subscribeStarted, subscribeConnected, ...)`
  achieves the test-decoupling per the brief's preferred path (and avoids
  scope creep that would force re-typing every `_engine.X` callsite in
  MVM).

### Follow-ups spawned

None for Stage 2. The end-to-end "real warmup probe → coordinator returns
Connected" test still depends on the deferred `IHttpClient` seam in
`StartupPipeline` (documented in `VpnEngineLifecycleTests.cs` file header
+ Stage 1 brief). That work is task #49 (c) per the brief.

### Brief

`plans/phase4-vm-two-phase-timer-stage2-2026-05-21.md` (this file).
