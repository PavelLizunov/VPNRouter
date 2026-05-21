# Phase 4 (Task #49) — Fill #36-C lifecycle test gaps

**Owner**: Claude session (Task #49 agent)
**Branch**: main (direct commit)
**Predecessor brief**: `plans/phase4-vpnengine-lifecycle-tests-2026-05-21.md`
(Task #36-C, commit `681b61c`, 9 lifecycle tests + 3 documented gaps).
**Coordination**: parallel agent on Task #41 Stage 2 (App-side two-phase
VM timer) — separate file scope, no overlap.
**Effort**: ~3 hours.
**Risk**: LOW. New test files + 1 production seam in `StartupPipeline.cs`
(static `WarmupHttp` field, default null preserves pre-Task-#49 behaviour).
**Blast radius**: 3 new test files (+~900 LOC) · 1 production file
(`StartupPipeline.cs`, +~40 LOC seam) · 0 existing files touched.
**Rollback**: `git revert <commit>` — pure additive change.

## Why

Task #36-C's outcome section documented 3 deferred test cases plus
the rationale for each deferral:

> **Follow-ups spawned**:
> - Task #36-D: deterministic warmup-probe success path (Group 5b
>   symmetric case). Needs IHttpClient injection into
>   `StartupPipeline.ScheduleWarmupProbe`. Effort: S (1 hour).
> - Task #36-E: deterministic Apply-on-running-engine hot-reload
>   test (Group 3 missing case). Needs ISingBoxApi override on
>   HotReload pipeline re-entry. Effort: S (1 hour, mostly wiring
>   through `StartupPipeline` ctor).
> - Split-tunnel happy-path lifecycle (Group 1 variant). Same
>   scaffolding, just point `ActiveProfile` to a bundled name
>   (`Browsers` works on Windows). Effort: trivial.

Task #49 is the best-effort sweep of those 3 items. Per the brief, we
ship what's cleanly shippable and refuse-to-proceed on scope creep.

## What

### Item (a) — Split-tunnel happy-path — SHIPPED

2 tests in `VPNRouter.Tests/VpnEngineSplitTunnelLifecycleTests.cs`
(new file, ~440 LOC).

Mirrors Task #36-C's Group 1 scaffolding but with `RoutingMode=split` +
`ActiveProfile="Browsers"` to engage the bundled-catalogue lookup. Tests:

1. `Start_SplitTunnel_Browsers_FiresLifecycleEvents` — full ColdStart
   pins ActiveProfileName="Browsers", split routing mode, MonitoredProcesses
   populated from stub scanner, firewall block rules NOT created (Browsers
   has BlockOnVpnFail=false in bundled catalogue), Apply seam fired once,
   ETW monitor started.

2. `Stop_SplitTunnel_FiresRestoreThroughDnsHardening` — symmetric Stop
   proves teardown path doesn't branch on routing mode; same Restore-
   via-seam invariant as Group 1's Stop test.

**Surprise**: none. Agent C's deferral was correctly assessed as trivial
once the bundled `profiles/default.json` was confirmed to be copied into
the test output bin/ via the .csproj profile glob (verified via Glob).

### Item (b) — Hot-reload Apply on running engine — SHIPPED

3 tests in `VPNRouter.Tests/VpnEngineHotReloadLifecycleTests.cs`
(new file, ~470 LOC).

**Key insight Agent C missed**: we DON'T need a successful Clash API
hot-reload to test the running-engine Apply path. The pipeline's
HotReload mode regenerates the config and `VpnEngine.ApplyAsync` calls
`_singBox.TryReloadConfigJson`. With ClashApi pointed at unused port
65535, HTTP fails fast (connection refused) and ReloadConfigJson falls
back to `Restart()`. The fresh-handle `FakeProcessRunner.OnStart` factory
provides a new alive handle for the restart's spawn, so the engine stays
"running" post-ApplyAsync. ApplyAsync returns `true` (restart-fallback
succeeded).

This is the FULL running-engine Apply pipeline exercised deterministically.
No FakeSingBoxApi needed (HTTP-refused-fast IS deterministic). No
reflection on IsRunning needed (fake handle reports alive truthfully).

Tests:

1. `Apply_OnRunningEngine_RunsHotReloadFallbackToRestart` — full path:
   running engine → ApplyAsync → hot-reload HTTP fails → restart fallback →
   ApplyAsync returns true, engine still running, ≥2 handles spawned,
   initial handle killed by Restart's StopInternal, latest handle alive.

2. `Apply_OnRunningEngine_DoesNotMutateDnsHardening` — defence pin:
   ApplyAsync on running engine does NOT touch Apply/Restore seams
   (HotReload mode skips phases 5-8). The DnsLeakLockdown
   EnableLockdownCount is intentionally NOT pinned — the warmup probe
   runs fire-and-forget and may fire EnableLockdownIfConfigured at any
   time independent of ApplyAsync, on machines with working internet.

3. `Apply_OnRunningEngine_PreservesFirewallAndMonitorReferences` —
   Phase 6 (firewall) + Phase 8 (ETW) reference the same _firewall /
   _etw instances after Apply; no new Start invocations recorded.

**Surprise**: I expected Agent C's documented blocker ("ISingBoxApi
override on HotReload re-entry") to be a real blocker, but it isn't.
The HTTP-refused-fast determinism on port 65535 is the existing
production-shape seam — no production code change needed for item (b).

### Item (c) — DnsLeakLockdown ON case — SHIPPED with 1 production seam

2 tests in `VPNRouter.Tests/VpnEngineDnsLockdownLifecycleTests.cs`
(new file, ~470 LOC) + 1 production seam in `StartupPipeline.cs`
(~40 LOC: static `WarmupHttp` field + branch in `ScheduleWarmupProbe`).

**Effort assessment**: per the brief's "If > 2 hours OR cascades into
3+ files → STOP" gate, I assessed the IHttpClient swap as ~1.5 hours
total. The change is contained to ONE file (`StartupPipeline.cs`) — no
VpnEngine plumbing needed because I used the static-seam pattern that
matches existing precedent (`SingBoxManager.Runner`,
`TunAdapterDiagnostics.Runner`). Both Group 1 (#36-C) and parallel
agents already understand this pattern.

**Production seam**: `StartupPipeline.WarmupHttp` is a `public static
IHttpClient?` field. Default null preserves the inline
`new HttpClient { Timeout = 3s }` production behaviour exactly. When
test code sets the field to a `FakeHttpClient`, the `ScheduleWarmupProbe`
body uses the seam (IHttpClient.SendAsync) instead. The inline branch
is kept as the default so a future refactor that wants to drop the
seam can do so safely (no behaviour change for un-overridden production).

Tests:

1. `Start_DnsLeakLockdownOn_WarmupSuccess_InvokesEnableLockdown` — drives
   a successful warmup probe via the new seam, polls `EnableLockdownCount`
   for up to 5s (warmup has 1s Task.Delay before first HTTP). Asserts
   BR-7 success branch fired exactly once with settings carrying
   DnsLeakLockdown=true. Captures the FakeHttpClient's SentRequests to
   prove the seam was used.

2. `Start_DnsLeakLockdownOn_WarmupFailure_DoesNotInvokeEnableLockdown` —
   symmetric defence pin: even with DnsLeakLockdown=true, a failing
   warmup probe (FakeHttpClient.ThrowOn) does NOT fire EnableLockdownIfConfigured.
   Stops engine after 2s to short-circuit the 15-attempt loop. Pin:
   EnableLockdownCount stays at 0, RestoreCount=1 (Stop drove Restore).

**Why polling instead of awaitable signal**: the fire-and-forget
Task.Run in ScheduleWarmupProbe has no exposed completion handle.
Adding one (TaskCompletionSource via new IStartupHost callback) would be
scope creep — the 50ms-tick poll-with-timeout approach gets us
deterministic coverage in O(1s) wall-clock without further production-API
surface area.

## Items refused / deferred

None. All 3 deferred items shipped.

## Verification gates

- [x] `dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release` →
  0 errors.
- [x] 4 lifecycle test classes (Group 1 + Split + HotReload + DnsLockdown):
  16/16 pass on Windows.
- [x] Existing 9 Group 1 tests remain green (frozen — coordination with
  Task #41 Stage 2).
- [x] Full test suite (Core + non-GUI): 1354 passed / 4 skipped / 0 failed,
  duration 1m44s. Baseline pre-Task-#49 was 1339 (post-`b012fe6`); 7 new
  Task-#49 tests + concurrent agent work brought the total to 1358.
- [ ] Post-push CI verify.
- [x] Brief: this file.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Tests/VpnEngineSplitTunnelLifecycleTests.cs` | +440 LOC (new). 2 tests, Windows-only. |
| `VPNRouter.Tests/VpnEngineHotReloadLifecycleTests.cs` | +470 LOC (new). 3 tests, Windows-only. |
| `VPNRouter.Tests/VpnEngineDnsLockdownLifecycleTests.cs` | +470 LOC (new). 2 tests, Windows-only. |
| `VPNRouter.Core/Services/StartupPipeline.cs` | +40 LOC. Static `WarmupHttp` IHttpClient seam + dual-branch ScheduleWarmupProbe. |
| `plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md` | This brief. |

### Test count delta

- Pre-Task-#49: 9 lifecycle tests (Group 1 only).
- Post-Task-#49: 9 + 7 = 16 lifecycle tests across 4 files.

### Cross-platform / CI matrix

All 7 new tests are Windows-only (Assert.SkipUnless gating). Same
reasoning as Task #36-C's Group 1 Windows-only justification:
SingBoxManager's Linux path uses pkexec/sudo argv + a direct
`Process.Start("/usr/sbin/getcap")` probe that isn't routed through
IProcessRunner; the test would shell out to real getcap.

| Test class | Windows | Linux |
|---|---|---|
| VpnEngineSplitTunnelLifecycleTests (2) | ✓ pass | skip |
| VpnEngineHotReloadLifecycleTests (3) | ✓ pass | skip |
| VpnEngineDnsLockdownLifecycleTests (2) | ✓ pass | skip |

### Surprises encountered

1. **TUN ownership lock leaks between testhost invocations.**
   Sequential `dotnet test` commands run in fresh testhost processes,
   but the previous testhost can take a few seconds to release the
   `Global\VPNRouter-SingBox-Owner` named semaphore. Workaround:
   `Stop-Process -Name testhost, VPNRouter.Tests -Force` before each
   isolated `dotnet test` run (documented in
   `VPNRouter.Tests/CLAUDE.md` "testhost lock" note). Tests pass cleanly
   within a single testhost invocation; the cross-invocation lag is
   not a behaviour-test concern.

2. **`engine.ApplyAsync`'s Restart fallback releases the TUN lock at
   `SingBoxManager.cs:405`** — `StopInternal(releaseLock: false)` in
   the Restart path goes through the Kill→Release `finally` block
   which calls `_tunLock.Release()` UNCONDITIONALLY (ignoring the
   `releaseLock` parameter). That's a real production race window
   (Restart's TUN lock is briefly released between StopInternal and
   LaunchProcess), but well outside Task #49's test scope. Flagged
   here for future investigation.

3. **Item (c) effort estimate was bang-on**: ~1.5 hours including the
   seam refactor + 2 tests. The single-file scope kept the change
   inside the brief's 2-hour gate.

4. **Apply on running engine doesn't actually need a Clash API
   success path**: the HTTP-refused-fast on port 65535 IS the
   deterministic shape. Falls back to Restart which is fully testable
   via existing FakeProcessRunner.OnStart factory. This was Agent C's
   "would need ISingBoxApi override" blocker — turns out the test
   shape is fine via the simpler hot-reload-falls-back-to-restart
   path. The future task to test a SUCCESSFUL Clash API hot-reload
   (200 OK) would still need FakeSingBoxApi wiring on the
   running-engine SingBoxManager — but that's a separate test concern
   (pins the success-branch wiring) and not what Agent C's deferred
   item meant.

### Follow-ups spawned

- (Optional, low priority) `SingBoxManager.Restart` releases the TUN
  lock during the Kill→LaunchProcess window. Production race: another
  VPNRouter instance could acquire the lock and start their own
  sing-box in this brief gap, conflicting with the in-flight restart.
  Real-world impact: low (services compete for the lock, only one
  process owns it at a time, but the window IS race-able). Brat-style
  trace would be needed to catch this; not a Task #49 concern.

- (Optional) The new `StartupPipeline.WarmupHttp` static seam could be
  promoted to a ctor parameter on `StartupPipeline` if we ever need
  per-instance HTTP clients. The static-seam pattern matches existing
  precedent (`SingBoxManager.Runner`, `TunAdapterDiagnostics.Runner`)
  and adding a ctor parameter would require re-plumbing both
  `VpnEngine.StartAsync` AND `VpnEngine.ApplyAsync` ctor calls
  (3+ file change). Defer.

### Brief

`plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md` (this file).
