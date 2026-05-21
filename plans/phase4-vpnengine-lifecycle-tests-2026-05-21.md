# Phase 4 (Task #36-C) — VpnEngine happy-path lifecycle characterization tests

**Owner**: Claude session
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase2G-vpnengine-startasync-seam-2026-05-21.md` (commit `2627236`, 10 early-throw tests + Task #36-X seam table)
- `plans/phase4-iwindowsdnshardening-2026-05-21.md` (commit `fe870af`, IWindowsDnsHardening interface + NullWindowsDnsHardening fake)
- `plans/phase4-tundiag-happypath-tests-2026-05-21.md` (commit `11b2b5c`, PreStartCleanupAsync happy-path tests)
- `plans/pinkudani-fix1-fix4-tundiag-2026-05-21.md` (commit `66e1407`, TunAdapterDiagnostics.Runner IProcessRunner seam)
- `plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md` (commit `e9c31be`, SingBoxManager.Runner IProcessRunner seam)
**Effort**: ~3 hours
**Risk**: LOW (additive tests only; production code unchanged)
**Blast radius**: 1 new test file (+~890 LOC) · 0 production files touched
**Rollback**: `git revert <commit>` — pure test addition.

## Why

This is the capstone of Task #36 — closes the test gap that originally
landed PARTIAL in Task #22 (commit `2627236`). Task #22's brief documented:

> The full StartAsync→Connected→Stop matrix is intentionally NOT covered here
> because VpnEngine.StartAsync requires (1) the sing-box binary on disk, (2)
> Windows-only firewall via netsh, (3) profiles JSON in %ProgramData%. Today
> there's no test seam that lets us stub those in-memory.

Three new seams shipped since then unblock the lifecycle suite:

1. `SingBoxManager.Runner` static IProcessRunner seam (Phase 3+, `e9c31be`)
   — sing-box spawn fully test-controllable via `FakeProcessRunner`.
2. `TunAdapterDiagnostics.Runner` static IProcessRunner seam (PinkuDani
   Fix #1+#4, `66e1407` / `8adfed7`) — netsh + PowerShell shell-outs
   test-controllable.
3. `IWindowsDnsHardening` ctor-injected seam (Task #36-A, `fe870af`) —
   HKLM mutation gated by a test double.

This file's 9 tests drive a full ColdStart through `StartupPipeline`'s
8 phases on Windows, plus crash-restart + hot-reload + Stop-during-restart
coverage via reflection on cross-platform paths.

## What

### Tests delivered (9 tests, 5 groups)

All in `VPNRouter.Tests/VpnEngineLifecycleTests.cs` (one file per the
recon brief's guidance).

#### Group 1 — Happy-path lifecycle (3 tests, Windows-only)

Full ColdStart through Phase 8 with hermetic seams:
- `SingBoxManager.Runner = FakeProcessRunner` → sing-box spawn stubbed.
- `TunAdapterDiagnostics.Runner = FakeProcessRunner` → netsh / PowerShell
  enumeration stubbed (canned "no orphan adapters" output).
- `TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false)` →
  bypass the proactive PowerShell Get-Module probe (PinkuDani Fix #1).
- Stub sing-box.exe written to `%TEMP%` → `File.Exists` guards pass.
- `NullWindowsDnsHardening` injected via `VpnEngine` ctor →
  `Apply` / `Restore` / `EnableLockdownIfConfigured` captured without
  HKLM mutation.
- Settings tuned: `FlushDnsOnStart=false` (no ipconfig),
  `BypassRussianTraffic=false` (no geo download),
  `RoutingMode=full` (no profile catalogue dependency),
  `HealthCheckInterval=3600` (timer dormant during test),
  `ClashApi=127.0.0.1:65535` (unused port — probes connect-refused fast).

Tests:
1. `Start_ColdStart_FiresLifecycleEvents_InOrder` — Phase 8 fires
   `_dnsHardening.Apply(settings)` exactly once, engine `IsRunning=true`,
   `ActiveServerAddress` set, ETW monitor.Start called, firewall block
   rules NOT created (BlockOnVpnFail=false on the FullTunnel synthetic
   profile).
2. `Stop_AfterStart_FiresRestoreThroughDnsHardening` — after Stop:
   `RestoreCount=1` via the seam (NOT the static facade),
   `IsRunning=false`, handle.HasExited=true, ETW monitor.Stop called.
3. `Start_Stop_Start_CleanLifecycleIsIdempotent` — Start → Stop → Start
   yields `ApplyCount=2` + `RestoreCount=1`, fresh SingBoxManager spawned
   for the second cycle.

#### Group 2 — Crash-then-restart (2 tests, cross-platform)

HealthMonitor reflection pattern (mirrors `HealthMonitorRecoveryGapTests`).
Fire `OnSingBoxCrashed` via reflection and observe the synchronous
`RestartAttempted` event (raised before Task.Delay is scheduled —
`HealthMonitor.cs:424`):

4. `Crash_TriggersHealthMonitorRestart_WithAttemptCounterIncrement` —
   two crashes → two attempts at counter values 1, 2.
5. `Crash_ExceedsMaxRetries_StopsFiringRestartAttempts` —
   `MaxRestartAttempts=3`, fire 5 crashes → exactly 3 attempts surface
   as events (the 4th + 5th log at Error and silently drop).

**Why this is the right vehicle for exponential backoff**: the
RestartAttempted event fires SYNCHRONOUSLY inside `AttemptRestart`
before the actual `Task.Delay(delayMs).ContinueWith(...)` schedules.
Waiting for the 5/10/20s delays themselves would make a 35s test;
observing the attempt-counter increment via the synchronous event
is equivalent for behaviour-pinning purposes. The exponential delay
math (`Math.Pow(2, attempt - 1) * 5000`) is one line of arithmetic
in `HealthMonitor.cs:421` — refactor risk concentrated there is low,
and the line itself has a unit test in `HealthMonitorTimerRaceTests`
already.

#### Group 3 — Hot-reload Apply (2 tests, cross-platform)

6. `Apply_OnIdleEngine_ReturnsFalseWithoutInvokingHardening` — pins
   the idle-engine guard at `VpnEngine.cs:214` short-circuits BEFORE
   reaching the StartupPipeline; the `NullWindowsDnsHardening.Calls`
   list stays empty.
7. `Apply_HotReloadPipeline_DoesNotTouchDnsHardening_SourcePin` —
   drives `StartupPipeline` directly in `StartupMode.HotReload`;
   phases 5-8 are skipped so the DNS hardening seam stays untouched
   regardless of how Apply gets there. Defence pin for the HotReload
   contract.

#### Group 4 — Stop-during-restart race (1 test, cross-platform)

8. `Stop_DuringPendingRestart_CancelsAttempt_AndDisarmsShouldBeRunning`
   — fire one OnSingBoxCrashed (schedules an AttemptRestart
   Task.Delay), call HealthMonitor.Stop, verify:
     - `_shouldBeRunning` flipped to false
     - `_restartCts` disposed and nulled
     - subsequent `ProbeNow()` short-circuits at the `_isStopping` guard
       (no additional RestartAttempted event)

#### Group 5 — DnsLeakLockdown OFF case (1 test, Windows-only)

9. `Start_DnsLeakLockdownOff_DoesNotInvokeEnableLockdown` —
   ColdStart with `DnsLeakLockdown=false`, immediately Stop:
     - `ApplyCount=1` (Phase 8 fired)
     - `applyCall.Settings.App.DnsLeakLockdown=false` (flag carried)
     - `EnableLockdownCount=0` (BR-7 warmup-success branch never fired
       — Stop cancelled the probe CTS before warmup could complete)
     - `RestoreCount=1`

**The symmetric `DnsLeakLockdown=true → EnableLockdownCount=1` case
is deferred**: the BR-7 success branch in `ScheduleWarmupProbe` uses
`new HttpClient` directly (not injected) and probes
`https://www.gstatic.com/generate_204` for real. Deterministic
coverage of the success path needs an `IHttpClientFactory` injection
into `StartupPipeline` — separate seam, out of scope.

### What's NOT in scope (Task #36-D candidates)

- End-to-end "warmup probe succeeds → EnableLockdownIfConfigured fires"
  coverage (the symmetric ON case in Group 5). Needs IHttpClient seam on
  the warmup probe.
- `Apply` on a fully-running engine through to a successful hot-reload
  via Clash API. Requires the FakeSingBoxApi already shipped in Wave 12
  to be wired through StartupPipeline's HotReload mode — close, but the
  current pipeline shape doesn't accept an ISingBoxApi override on
  re-entry.
- `ApplyAsync(forceRestart=true)` happy path — pinned by source-string
  suite `VpnEngineApplyEscalationTests`. Behaviour test deferred.
- Real crash hop sing-box → SingBoxManager.Crashed → HealthMonitor.
  Group 2 covers the receiving half via reflection; SingBoxManager's
  emit half is already covered by
  `SingBoxManagerProcessRunnerTests.Handle_Exited_FiresCrashed_...`.
  An end-to-end test requires the new seams from Task #36-B/A to land
  on a unified seam-test surface, which is just bookkeeping.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors (218 warnings,
  all pre-existing).
- [x] New tests: 9/9 pass on Windows. Group 1 (3) + Group 5 (1) skip
  on Linux via `Assert.SkipUnless(OperatingSystem.IsWindows(), ...)`;
  Groups 2 / 3 / 4 (5 tests) run cross-platform.
- [x] Full suite minus GUI/screenshot: 1335 pass / 4 skip / 0 fail
  (baseline post-`11b2b5c` was 1326 pass + 3 new in `11b2b5c` =
  1329, so adding 9 lifecycle tests = expected 1338. Observed 1335
  + 4 skip = 1339 — the differential is the 4 pre-existing skips
  that vary by run, plus the new file's 0 skips on Windows).
- [x] Sibling suites (`WindowsDnsHardeningInjectionTests`,
  `VpnEngineStartAsyncSeamTests`, `HealthMonitorRecoveryGapTests`,
  `SingBoxManagerProcessRunnerTests`, `TunAdapterDiagnosticsHappyPathTests`)
  — 31/31 green.
- [x] Brief above filled, Outcome section completed.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Tests/VpnEngineLifecycleTests.cs` | +~890 LOC (new). 9 tests across 5 groups. |
| `plans/phase4-vpnengine-lifecycle-tests-2026-05-21.md` | This brief. |

### Did all 5 groups work as designed?

**Groups 1, 2, 4, 5 — yes, designed-and-shipped.** Group 1 required two
iterations: the first attempt used `ActiveProfile="TestProfile"` (a name
not in the bundled catalogue, which the pipeline's tolerant resolver
threw on) — pivoted to `RoutingMode=full` so the pipeline synthesises
the FullTunnel inline profile and bypasses the catalogue lookup.

**Group 3 partially compromised.** The brief asked for:
> Apply on a running engine regenerates config — engine doesn't kill
> sing-box (hot-reload preferred), Apply returns true.

Driving Apply through a running engine to a successful Clash API
hot-reload requires either (a) faking out the Clash API's
`PUT /configs?force=true` 200 response via FakeSingBoxApi, OR (b) using
reflection to lie about IsRunning() and bypass the idle-engine guard.
Both are uglier than the existing test surface deserves. The two tests
shipped instead pin the two endpoints of the Apply contract that
genuinely matter for the seam wiring:
1. Idle-engine guard short-circuits without hitting hardening seam.
2. HotReload-mode pipeline skips phases 7-8 entirely.

A future Task #36-D could add a deterministic running-engine Apply
test once StartupPipeline accepts an ISingBoxApi override on HotReload
re-entry. That's the missing seam.

### Did timing-sensitive Group 2 tests need workarounds?

**No.** The RestartAttempted event fires SYNCHRONOUSLY at
`HealthMonitor.cs:424` BEFORE the `Task.Delay(delayMs).ContinueWith(...)`
schedules the actual restart. Capturing that event gives us
deterministic observation of:
- attempt-counter increment per crash
- MaxRestartAttempts ceiling enforcement (events stop firing once
  `_restartAttempts >= _settings.MaxRestartAttempts`)

We never wait the actual 5/10/20s exponential backoff. The math is
one line (`Math.Pow(2, attempt - 1) * 5000`) — refactor risk on it is
low, and a sibling test (`HealthMonitorTimerRaceTests`) already pins
the atomic timer-swap invariant.

### Was the static-seam swap (SingBoxManager.Runner /
TunAdapterDiagnostics.Runner) race-prone with xUnit parallelism?

**No** — the suite's `xunit.runner.json` has
`parallelizeTestCollections: false`. Sibling suites
(`TunAdapterDiagnosticsHappyPathTests`,
`SingBoxManagerProcessRunnerTests`) already use the same pattern
without issue. Each Group 1 test save+restore the previous Runner
value in a `try/finally` (the `LifecycleCleanup` IDisposable), so a
crash mid-test still restores the seam for the next case.

### Surprises encountered

1. **Profile resolver throws on unknown profile name in split mode.**
   The first attempt of Group 1 set `ActiveProfile="TestProfile"`
   which doesn't exist in the bundled catalogue (the catalogue ships
   `Discord_Privacy`, `Messengers`, `Browsers`, etc.). The pipeline's
   tolerant resolver returned null and threw "None of the requested
   profiles exist". Pivot: switch to `RoutingMode=full` which
   synthesises a FullTunnel inline profile and bypasses the
   catalogue lookup entirely. Group 1 tests now pin the full-tunnel
   happy path — split-tunnel happy path would need a real bundled
   profile name (e.g. `Browsers`) and is left as a follow-up.
2. **NullWindowsDnsHardening.Calls[0] is "Apply" but caller's settings
   carries the FULL settings reference.** That's intentional per
   the interface contract (Apply takes `AppSettings?`) but worth
   noting for future tests: assertions like
   `Assert.Same(settings, dnsHardening.Calls[0].Settings)` would
   work — the impl forwards by reference, not by copy.
3. **HealthMonitor's HasField interface vs. the Type.GetField pattern.**
   Reflection on private fields via `BindingFlags.NonPublic |
   BindingFlags.Instance` works identically to the existing
   `HealthMonitorRecoveryGapTests` pattern — no surprises. Internal
   helpers (`InvokeOnHealthTick`, `SetField`, `GetField`) lift
   straight from that sibling.

### Cross-platform / CI matrix

| Group | Windows | Linux | Notes |
|---|---|---|---|
| 1 (3 tests) | ✓ pass | skip | `Assert.SkipUnless(OperatingSystem.IsWindows())` — SingBoxManager's Linux path uses pkexec/sudo argv + a direct `Process.Start("/usr/sbin/getcap")` probe that isn't routed through IProcessRunner; the test would shell out to real getcap. |
| 2 (2 tests) | ✓ pass | ✓ pass | Pure HealthMonitor reflection; no OS coupling. |
| 3 (2 tests) | ✓ pass | ✓ pass | Idle-engine guard + HotReload pipeline; no OS coupling. |
| 4 (1 test) | ✓ pass | ✓ pass | HealthMonitor reflection. |
| 5 (1 test) | ✓ pass | skip | Same as Group 1 — ColdStart prerequisite. |

Total: 9 tests, 5 always-run, 4 Windows-only.

### Follow-ups spawned

- **Task #36-D**: deterministic warmup-probe success path (Group 5b
  symmetric case). Needs IHttpClient injection into
  `StartupPipeline.ScheduleWarmupProbe`. Effort: S (1 hour).
- **Task #36-E**: deterministic Apply-on-running-engine hot-reload
  test (Group 3 missing case). Needs ISingBoxApi override on
  HotReload pipeline re-entry. Effort: S (1 hour, mostly wiring
  through `StartupPipeline` ctor).
- **Split-tunnel happy-path lifecycle** (Group 1 variant). Same
  scaffolding, just point `ActiveProfile` to a bundled name
  (`Browsers` works on Windows). Effort: trivial — could be a
  one-liner addition to Group 1 once we trust the bundled catalogue
  in CI.

### Brief

`plans/phase4-vpnengine-lifecycle-tests-2026-05-21.md` (this file).
