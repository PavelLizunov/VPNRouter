# Phase 1 Audit Remediation — P02 Failover Wiring

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p02-failover-wiring-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~3-4 h
**Risk**: HIGH (concurrency / lifecycle gate; deadlock history in v2.44.3/v2.46.1)
**Blast radius**: 1 Core product file (`VpnEngine.cs`) + tests
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| FAIL-1 | P1 | CONFIRMED | **P1** | High |

ONLY FAIL-1. No other finding.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

`VpnEngine._failover` is a single slot assigned via `??=` and never reset. Two
wire-up methods write it with incompatible restart delegates: the pre-start
`WireFailover` installs an UNSAFE delegate (calls `StartAsyncInternal` directly —
no teardown, no lifecycle gate, no session pre-check), while the post-start
`WireFailoverWithStop` installs the SAFE delegate (`ExecuteProbeFailoverRestartAsync`
— gated teardown + session guard). Because both use `??=`, whichever runs first
wins. A startup dead-config failover runs `WireFailover` first, so every later
post-start failover reuses the unsafe delegate — bypassing teardown (orphaned
`SingBoxManager`: TUN lock + ProcessExit subscription + HttpClient), bypassing the
gate (races a concurrent user `Stop()`), and opening a disconnect-resurrection
window. User impact: orphaned sing-box/TUN, possible tunnel resurrection after
disconnect, and a race with user stop.

## Current root cause (verified against current code)

- [FACT] `VPNRouter.Core/Services/VpnEngine.cs:44` — `private AutoFailoverEngine? _failover;`
  assigned only via `??=`; never reset to null (only `_failover?.ResetCycle()` at :1382,
  which clears the tried-server set, not the instance/delegate).
- [FACT] `WireFailover` (pre-start) :1491-1516 — `_failover ??= new AutoFailoverEngine(...
  restart: async (innerCt) => { await StartAsyncInternal(...); return true; })` (:1495,:1505).
  No `TeardownInternal`, no `_lifecycleGate`, no session pre-check.
- [FACT] `WireFailoverWithStop` (post-start) :1525-1540 — `_failover ??= new AutoFailoverEngine(...
  restart: (innerCt) => ExecuteProbeFailoverRestartAsync(...))` (:1527,:1538). This is the safe delegate.
- [FACT] Pre-start path runs first on dead config: `StartupPipeline.cs:1035` `if (!preCheck.IsDead) return false;`
  → `:1041 _host.WireFailover(sanityCheck)`. Later post-start consumers reuse the won (unsafe) delegate:
  `OnFailoverRequested` :1406 and post-start probe :1593 (both call `WireFailoverWithStop`, a no-op once non-null).
- [FACT] Safe delegate `ExecuteProbeFailoverRestartAsync` :495-557 provides gate :497, `TeardownInternal()` :500,
  session guard :501-507, bring-up under `_sessionCts.Token` :508, failed-bring-up teardown :543, gate release :555.
- [FACT] Aggravators: `StartAsyncInternal` :332 lacks the `HasLiveOrStartingSingBox()` guard (only public `StartAsync` :294 has it);
  `SetSingBoxManager` :1437 overwrites `_singBox` without disposing the old manager; unsafe path runs ungated, racing user `Stop()` (gate :751).
- [INFER] v2.44.3/v2.46.1 fixed a self-cancel deadlock and a gate-join — NOT this slot collision.

## What

### Minimal expected file list
- `VPNRouter.Core/Services/VpnEngine.cs` (only product file).
- `VPNRouter.Tests/VpnEngineFailoverPhaseDispatchTests.cs` (new, cross-platform).
- `VPNRouter.Tests/VpnEngineLifecycleTests.cs` (one Windows-only end-to-end test added — recommended).

### Explicit non-goals
- No changes to `AutoFailoverEngine.cs`, `StartupPipeline.cs`, `IStartupHost`/`StartupHostInternal`,
  `SingBoxManager.cs`, or public `StartAsync`/`Stop`/`Apply` contracts.
- No other audit finding.
- Do NOT force-replace the `AutoFailoverEngine` instance at phase change (would reset `_tried`
  and break the `MaxAttempts=3` cap). The instance persists; only the stored delegate becomes phase-aware.

## How (ordered; fix the shared root cause once)

Chosen approach: **make the single stored delegate phase-aware** (unify on the safe delegate
for post-start without losing the `AutoFailoverEngine` instance). All in `VpnEngine.cs`:

1. Add phase flag near `_warmupConfirmed` (~:77): `private volatile bool _postStartPhase;`.
2. Reset it at the top of `StartAsyncInternal` (:332, beside `_warmupConfirmed = false;` ~:341): `_postStartPhase = false;`.
3. Set it when sing-box starts: host `OnSingBoxStarted` (:1362) → call a new
   `internal void EnterPostStartPhase() => _postStartPhase = true;` (fires for both custom and
   generated configs; `OnSingBoxStarted` is the correct single point).
4. Add dispatch + pre-start helper beside `ExecuteProbeFailoverRestartAsync` (:495):
   - `internal Task<bool> ExecuteFailoverRestartAsync(AppSettings captured, CancellationToken ct)
     => _postStartPhase ? ExecuteProbeFailoverRestartAsync(captured, ct) : ExecutePreStartFailoverRestartAsync(captured, ct);`
   - `internal async Task<bool> ExecutePreStartFailoverRestartAsync(...)` = the current unsafe body
     (`await StartAsyncInternal(captured, ct, _skipVpnConflictCheck)` → `true`; `catch` → log + `false`),
     with `ConfigureAwait(false)`. It MUST stay gate-free (runs inside the already-held gate; re-taking
     the non-reentrant `SemaphoreSlim` deadlocks per the v2.44.3 comment at :1502-1504).
5. Unify both wire methods (:1491,:1525) to install the SAME delegate via a private `WireFailoverCore`:
   `_failover ??= new AutoFailoverEngine(CapturedSettings(), sanityCheck, restart: (innerCt) =>
   _engine.ExecuteFailoverRestartAsync(CapturedSettings(), innerCt), logger: _engine._logger);`
   Keep `WireFailover` and `WireFailoverWithStop` as thin wrappers (interface preserved). The `??=`
   slot collision is now harmless — the stored delegate dispatches on live phase.
6. Defense-in-depth (satisfies "disposed exactly once before replacement"): harden `SetSingBoxManager`
   (:1437) to dispose a non-null, non-same old manager before assigning: capture `old`, assign,
   `if (old is not null && !ReferenceEquals(old, manager)) try { old.Dispose(); } catch { }`.
   In the fixed flow `TeardownInternal` already nulls `_singBox`, so this is a pure safety net
   (no double-dispose; `SingBoxManager.Dispose` is an idempotent superset of `Stop`, :803-810).
7. Add snapshot seam beside :132: `internal bool PostStartPhaseSnapshot => _postStartPhase;`
   (mirrors `SkipVpnConflictCheckSnapshot`).

Why minimal/correct: post-start callers now always reach `ExecuteProbeFailoverRestartAsync` →
teardown disposes the old manager before the inner start (orphan fixed), gate serializes with
`Stop()` (race fixed), session guard + token abort on disconnect (resurrection fixed). Pre-start
stays gate-free/teardown-free (deadlock avoided, nothing to tear down at phase 5). No
interface/`AutoFailoverEngine`/`StartupPipeline` changes.

## Callers / consumers to preserve

| What | Where | Note |
|---|---|---|
| `WireFailover` (pre-start, dead config) | `StartupPipeline.cs:1041` | only pre-start caller |
| `WireFailoverWithStop` (post-start) | `VpnEngine.cs:1406`, `:1593` | both post-start |
| `_failover` consumed via `HandleDeadConfigAsync` | `StartupPipeline.cs:1042`, `VpnEngine.cs:1408,:1594` | |
| `_failover?.ResetCycle()` | `VpnEngine.cs:1382` (`OnConnected`) | clears tried-set on connect |
| Interface contract (both methods) | `StartupPipeline.cs:272,:280`, `StartupHostInternal:1442` | both must remain |
| `_singBox` set/read | set `:1437`; nulled in `TeardownInternal:843`; read `IsRunning:99`, `HasLiveOrStartingSingBox:314` | |
| session cancel | `Stop():745` cancels `_sessionCts`; `TeardownInternal:795` cancels `_probeCts` | |
| `AutoFailoverEngine._restart` invoked | `AutoFailoverEngine.cs` step 6; ctor stores delegate :69; `MaxAttempts=3 :34` | instance persistence load-bearing |

Existing helpers to reuse: `ExecuteProbeFailoverRestartAsync` :495-557; `TeardownInternal` :769
(disposes `_singBox` once :810 then nulls :843, idempotent); `_lifecycleGate` :65 / `_sessionCts` :66;
`HasLiveOrStartingSingBox` :314; `SkipVpnConflictCheckSnapshot` :132 (internal-seam precedent);
`NullWindowsDnsHardening` spy (`VPNRouter.Tests/Fakes/NullWindowsDnsHardening.cs`, `RestoreCount`) —
`TeardownInternal` calls `_dnsHardening.Restore` (:828), so `RestoreCount` is a clean cross-platform "teardown ran" discriminator.

## Regression tests (exact)

New cross-platform `VPNRouter.Tests/VpnEngineFailoverPhaseDispatchTests.cs` (mirror `VpnEngineStartAsyncSeamTests`
stubs + inject `NullWindowsDnsHardening` spy; `RestoreCount` is the safe-path discriminator):
- `PostStartPhaseSnapshot_FreshEngine_IsFalse` — fresh engine → `false`.
- `EnterPostStartPhase_SetsSnapshotTrue` — `EnterPostStartPhase()` → `true`.
- `ExecuteFailoverRestart_PostStartPhase_RoutesThroughSafeTeardownPath` — **core FAIL-1 pin.** Fresh engine + spy;
  `EnterPostStartPhase()`; idle. `await ExecuteFailoverRestartAsync(settings, CancellationToken.None)` → assert `false`,
  `IsRunning == false`, **and `dns.RestoreCount >= 1`** (teardown ran ⇒ safe gated path). Pre-fix → `RestoreCount == 0`.
- `ExecuteFailoverRestart_PreStartPhase_SkipsTeardown_UsesDirectPath` — fresh engine + spy; phase default `false`;
  empty-servers settings (early phase-2 throw, cross-platform safe). → assert `false` **and `dns.RestoreCount == 0`**.
- `ExecuteFailoverRestart_SingleStoredDelegate_AdaptsToPhaseChange` — **slot-collision pin.** phase `false` → `RestoreCount == 0`;
  then `EnterPostStartPhase()`; call again → `RestoreCount` increments.

Windows-only end-to-end (add to `VPNRouter.Tests/VpnEngineLifecycleTests.cs`, matching `ProbeFailoverRestart_*` :447/:470):
- `PostStartFailover_AfterSingBoxStarted_UsesSafeDelegate_EndToEnd` — `Assert.SkipUnless(OperatingSystem.IsWindows(), …)`;
  `StartHappyPathWithSettingsAsync()`; assert `PostStartPhaseSnapshot == true`; `await ExecuteFailoverRestartAsync(settings, cancelledProbeToken)`
  → assert `true` + `IsRunning == true`.

Already-covered (cite, do NOT duplicate): disconnect-never-resurrects (`VpnEngineLifecycleTests:470`,
`VpnEngineStartAsyncSeamTests:497`); self-cancel (`AutoFailoverRestartSelfCancelTests`); gate serialization +
dispose-once (`FailoverRestartConcurrencyAuditTests`); user-intent rollback (`AutoFailoverUserIntentGuardTests`).

## Risks

- **Concurrency/deadlock (v2.44.3 deadlock, v2.46.1 gate-join)**: [INFER] no regression — pre-start branch stays
  gate-free (no nested `WaitAsync` on the non-reentrant gate); post-start branch takes the gate exactly once via the
  unchanged `ExecuteProbeFailoverRestartAsync`; dispatch reads one `volatile bool` (no new lock). Gate serializes all writers.
- **Compatibility**: interface + `AutoFailoverEngine` + all callers preserved; `SetSingBoxManager` hardening additive + guarded against double-dispose.
- **Cross-platform**: phase flag/dispatch OS-agnostic; `OnSingBoxStarted` fires on all platforms; new cross-platform tests avoid OS shell-outs.
- **Rollback**: single-file product change; trivial revert.

## Dependencies and file overlap with the other seven packages

- **P05 (DATA-1)**: failover persists via `AutoFailoverEngine`→`_store.Save`→`SettingsLoader.Save`; FAIL-1 touches no
  persistence code. Different file — independent.
- **P06 (FLOW-1)**: P06 touches `MainWindowViewModel` + the engine START path (public `StartAsync`/`Stop`); FAIL-1 touches
  internal failover dispatch. Public contract unchanged → low semantic overlap; both may be near `VpnEngine.cs` vicinity —
  coordinate to avoid merge conflict if landed together.
- **P07 (CLI-1)**: SEMANTIC dependency — CLI-1 relies on the `HealthMonitor.Stop()`→`_isStopping`→`OnSingBoxCrashed`
  early-return contract (:308/:728) and `VpnEngine.Stop()` ordering (:750 before :754). **P02 MUST NOT regress the
  graceful-stop-no-restart path.** No file overlap.
- No other overlap.

## Zone CLAUDE.md constraints (`VPNRouter.Core/CLAUDE.md`)

- async-first with `ConfigureAwait(false)` → new async helper follows.
- `#nullable enable` → new field is a non-nullable `volatile bool`.
- Reuse established lifecycle naming (`_sessionCts`, `_lifecycleGate`, `TeardownInternal`, `ExecuteProbeFailoverRestartAsync`);
  `_postStartPhase` mirrors existing `volatile bool _warmupConfirmed` style.
- `SingBoxManager.Dispose` is a safe superset of `Stop` (documented :803-810) → dispose-once hardening is safe.
- `InternalsVisibleTo VPNRouter.Tests` configured; the `internal … for the seam test` pattern is established precedent.

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `VpnEngineFailoverPhaseDispatchTests` (+ the Windows-only lifecycle test)
      green in CI; full existing suite stays green (concurrency/failover tests included).
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review; **security/concurrency review** of the gate/dispatch change
      (lifecycle-gate + dispose-once). Record result in Outcome.
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT fake PASS.
- [ ] **Gate 6 — Characterization**: N/A (no god-file split; no MVM surface change).

## Outcome

**Status**: IMPLEMENTED / REMOTE CI GREEN
**Commits**: `23ed44a0` (fix(core): dispatch failover restart by phase)
**Pushed**: draft PR #57, branch `codex/qwen-audit-p02-failover-wiring-2026-07-29`
**Test deltas**: +209 / -0 (1 new test file: `VpnEngineFailoverPhaseDispatchTests.cs` +209)
**Files changed**: 2 · +256 / -45

**Gate results:**
- [x] Gate 1 build (remote CI): PASS — dotnet test run 30444418700 SUCCESS
- [x] Gate 2 tests (remote CI): PASS — run 30444418700 SUCCESS; new `VpnEngineFailoverPhaseDispatchTests` green; full existing suite (concurrency/failover tests) stayed green
- [x] Gate 3 docs: PASS — Outcome filled; no README change needed
- [x] Gate 4 self-review / concurrency review: PASS — static self-review performed during implementation; phase-aware failover dispatch, lifecycle-gate/teardown/session-cancellation preservation reviewed
- [-] Gate 5 UI/live: deferred (owner constraint) — not live-verified
- [-] Gate 6 characterization: N/A

**Local build/test**: NOT run. The mandatory git hook attempted SDK resolution and found SDK 10.0.301 absent; this is not a pass.
**Surprises encountered**: none
**Follow-ups spawned**: none
**Rollback**: `git revert 23ed44a0` / branch delete
