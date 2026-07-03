# W0 hang detection + recovery — verified design (2026-07-03)

Status: DESIGN, code-verified 2026-07-03 (Fable 5). Resolves the W0 fork from
`plans/goal-true-split-tunnel-2026-07-03.md` W0.1/W0.3: how to handle the
Windows "sing-box alive but not serving" hang, and whether exclude-mode DNS
hygiene in the crash window still needs a fix.

Supersedes the hang-related claims in
`plans/true-split-tunnel-research-2026-07-03.md` §1.1/§7 — see "Corrections"
below. Every claim here is cited against current code (HEAD bdb0d234).

---

## 1. Verified findings

### 1.1 Windows never detects the hang (research doc was wrong)

- `OnHealthTick` health signal = `_singBox.IsHealthy()`
  (`VPNRouter.Core/Services/HealthMonitor.cs:402`).
- On Windows `IsHealthy()` is process-liveness only: handle non-null, not
  exited, snapshot readable (`SingBoxManager.Health.cs:33-52`). The Clash-API
  check is **macOS-only** (`Health.cs:35-36`). Confirmed by three in-code
  comments: `HealthMonitor.cs:408-410`, `:498-500`, `:537-539`.
- Therefore a hung sing-box (process alive, Clash API dead, TUN black-holing
  ALL traffic incl. split-excluded apps) reads `isHealthy=true`; the restart
  branch `if (!isHealthy && _vpnWasRunning && !_isStopping)`
  (`HealthMonitor.cs:443`) **never fires**. The
  `HealthProbeRestartFailThreshold=2` streak (`HealthMonitor.cs:57`,
  `ShouldRestartAfterHealthProbeFailure` `:710-723`) only counts
  process-DEATH ticks on Windows.
- Second, independent blocker: even if a hang were detected, the scheduled
  AttemptRestart continuation skips at `if (_singBox.IsRunning())`
  (`HealthMonitor.cs:815`) — on Windows `IsRunning()` is
  `State==Running && !HasExited` (`Health.cs:29-30`), true for a hung process.
- On macOS both gates are API-based (`Health.cs:26-27, 35-36`), so macOS
  ALREADY does kill-on-hang after 2 ticks: `IsHealthy=false` → streak →
  AttemptRestart → `IsRunning()` (=Clash probe) false → hot-reload fails →
  `ReloadConfigJson(forceRestart:true)` → `Restart()` kills the hung process
  (`SingBoxManager.Lifecycle.cs:406-464`). **Windows is the outlier, not the
  design.**

**Corrections to `true-split-tunnel-research-2026-07-03.md`:**
- §1.1 line 37 "Until HealthMonitor probe threshold (2 failed probes)
  triggers a kill+restart" — FALSE on Windows (never triggers). True on macOS
  only.
- §7 line 320 "HealthMonitor already detects via probes and kills — tighten"
  — FALSE on Windows: there is nothing to tighten; detection must be ADDED.

**Corrections to the task-prompt grounding (minor):**
- "No public kill-only primitive" — `Stop()` (`Lifecycle.cs:75-85`) IS public,
  kill-only, and leaves the manager restartable. But it releases the TUN
  ownership lock (`StopInternal(releaseLock:true)`), and the crash-recovery
  relaunch (`Restart()` → `LaunchProcess`) never re-acquires it (only
  `StartWithJson` calls `_tunLock.TryAcquire()`, `Lifecycle.cs:34`). Using
  `Stop()` for hang-kill would leave the recovered sing-box running WITHOUT
  lock ownership → a second VPNRouter instance (Windows Service) could acquire
  and spawn a competing sing-box (adapter fight). So a new primitive is still
  needed — but it is a 1-line wrapper over existing `StopInternal(releaseLock:false)`,
  the exact state a real crash leaves (crash also keeps the lock held).
- "~3s probe every tick" cost — wrong twice. `ClashApiResponds()` →
  `GetVersionAsync` has a **1s** internal deadline (`ClashSingBoxApi.cs:47,
  195-196` `PingDeadline`), and that is the FAILURE cap; when serving, a
  localhost GET /version is ms-scale. Also, when DnsLeakLockdown is on, this
  exact per-tick probe ALREADY runs today (`HealthMonitor.cs:505-509`).

### 1.2 Crash already self-heals, including DNS, including EXCLUDE mode

- Crash → wintun adapter dies with the process; `OnProcessExited` additionally
  force-disables + removes the adapter (`SingBoxManager.CrashDetect.cs:144-179`,
  netsh disable "frees the kernel handle so Windows drops the routes
  immediately", `:126-128`). Excluded apps get physical routes back in ~1-2s.
- `OnSingBoxCrashed` (`HealthMonitor.cs:679-708`): enables kill-switch block
  rules (fail-closed for routed apps), then lifts the DNS lockdown immediately
  via `_dnsHardening.ReconcileLockdownForHealth(false, ...)` (`:703`).

**Q2 answer — the v2.42.0 DnsLeakLockdown reconcile DOES cover EXCLUDE mode.
No W0 DNS-hygiene fix is needed.** Verified gating chain:
- Tick reconcile gate: `if (_appSettings?.App?.DnsLeakLockdown == true)` —
  the ONLY gate (`HealthMonitor.cs:505`). No RoutingMode / RoutingAppsMode /
  ConfigMode check.
- Crash-hook lift: unconditional call (`HealthMonitor.cs:703`); the reconciler
  self-gates on the flag only (`WindowsDnsHardening.cs:181-185`,
  `DnsLockdownPolicy.Decide` `DnsLockdownPolicy.cs:53-59` — inputs are
  settingEnabled/tunnelServing/currentlyEffective, nothing mode-shaped).
- Arm site: `StartupPipeline.cs:1318` `EnableLockdownIfConfigured` →
  `ReconcileLockdownForHealth(true, ...)` (`WindowsDnsHardening.cs:158-166`)
  — also mode-agnostic.
- `StrictDnsIsSoleDriver` (`HealthMonitor.cs:576-584`) — which DOES exclude
  exclude/full/custom modes — gates ONLY `ReconcileStrictDnsFailover`
  (`:613`), the dns.final failover. **Separate feature, separate policy class
  (`StrictDnsFailoverPolicy`), not shared with the lockdown reconcile.**

Consequence for the goal plan: **W0.3 ("scope Wave-39 DNS-lockdown in
exclude-split / disable in crash window") is ALREADY IMPLEMENTED by v2.42.0**
— crash lifts instantly (`HealthMonitor.cs:703`), outage lifts within one tick
(`:505-509`, `serving = isHealthy && ClashApiResponds()` — a HANG also drives
this false because the probe fails even though `isHealthy` is true). Mark
W0.3 done; nothing to code.

### 1.3 Q1 — is "alive but hung" real?

Evidence FOR treating it as real:
- The failure mode is designed-for elsewhere in this codebase: strict mode
  exists specifically to catch "'alive but stuck' sing-box where Clash API
  stops responding" (`HealthMonitor.cs:266-269`); macOS actively handles it
  (`Health.cs:35-36`); the deferred kill-switch lift has a wedged-API fallback
  (`HealthMonitor.cs:169-175`); `ISingBoxApi.GetConnectionsAsync` doc plans a
  "silently dead" check (`ISingBoxApi.cs:71-77`).
- Go-runtime reasoning: an unrecovered panic in ANY goroutine kills the whole
  process (crash, not hang) — so panics self-classify into the handled bucket.
  What's LEFT as hang: Go mutex/channel deadlocks (a recurring sing-box bug
  class upstream), wintun ring-buffer stalls, API-listener starvation. These
  keep the process alive.
- Field archive (plans/, memory): dozens of documented CRASH incidents (exit 1
  FATALs, -1 TerminateProcess, 1073807364 sleep/wake, TUN-orphan loops) and
  **zero confirmed Windows alive-but-wedged incidents**. WSAENOBUFS/AWG storms
  are dataplane degradation with a healthy control plane — not this bucket
  (and a local restart is not obviously the right medicine there; that is the
  G4-failover domain).

Verdict: hang is **plausible-but-unobserved** on Windows; crash dominates.
That argues against building anything heavy — but NOT for option (3), because:
(a) today's hang cost is unbounded and invisible (UI says connected, all
traffic incl. excluded black-holes forever — the exact W0 trigger);
(b) the W0 acceptance criterion explicitly includes the hang case
(goal plan lines 36-39, SIGSTOP emulation);
(c) W1 is 2-4 weeks away and spike-GATED (W1.0 can FAIL → W0 becomes the only
mitigation);
(d) the fix below is ~40 lines reusing already-hardened machinery, and the
probe cost is ms-scale (1.2 above).

## 2. Fork resolution — ranked

**RECOMMENDED: option (1'): per-tick control-plane probe, latch-gated,
threshold 2 — then KILL-FIRST and reuse the crash flow verbatim.** This is
"option 1" with the two false-positive traps removed and the recovery route
changed from "new restart path" to "convert hang into a crash".

Key insight: after a kill, the world is indistinguishable from a crash
(process dead, TUN lock still held, adapter cleanup fired), so **every
load-bearing race fix downstream applies unchanged** — backoff, MaxRestartAttempts
ceiling + G4 failover, deferred-block-rule lift, TUN-orphan recovery, CTS
swap discipline, `IsRunning()` skip (now correctly false). No new restart
path, no hang flag threading through AttemptRestart.

Rejected:
- **(2) TCP canary through the TUN** — conflates exit-server/network failure
  with local wedge → false kills on server blips (kill doesn't fix a dead
  server; G4 failover owns that); needs an interface-bound socket + a target
  host (new moving parts + privacy surface); detection latency is still
  tick-bound so it buys nothing the probe doesn't. Revisit only if field data
  shows dataplane-only wedges (API alive, forwarding dead) actually occur —
  the one class the control-plane probe cannot see. W0.2 stays "optional, not
  now".
- **(3) defer everything to W1** — half right: the DNS/crash half of W0 is
  indeed already done (1.2). But the hang black-hole is the single W0 case
  with unbounded user cost, the goal plan's acceptance names it, and W1 is
  gated. Closing it costs ~40 lines.

## 3. Minimal implementation

### 3.1 `SingBoxManager` — new kill-only primitive (~10 lines)

`SingBoxManager.Lifecycle.cs`, next to `Stop()`:

```csharp
/// <summary>
/// W0.1 (2026-07-03): kill an alive-but-wedged sing-box and leave the world
/// looking like a CRASH: process dead, TUN ownership lock still HELD,
/// manager restartable. HealthMonitor then drives its normal crash recovery
/// (block rules -> backoff -> ReloadConfigJson(forceRestart) -> Restart),
/// which assumes the lock is held — public Stop() would release it and the
/// relaunch path never re-acquires (only StartWithJson does). StopInternal
/// provides the load-bearing SuppressExitedEvent-before-Kill +
/// _stopInProgress window, so the late OS Exited callback cannot fire a
/// second (false) Crashed on top of the synthetic crash flow.
/// </summary>
public void KillWedgedForRecovery()
{
    if (Volatile.Read(ref _disposed) != 0) return;
    _logger.Warning("[SingBoxManager] Killing wedged sing-box (PID {Pid}) — keeping TUN lock for crash-parity recovery", Pid);
    StopInternal(releaseLock: false);
}
```

What `StopInternal(releaseLock:false)` already gives us, for free:
- B2 concurrent-stop guard (`Lifecycle.cs:97-101`) — safe vs a user Stop
  racing the same instant.
- `_stopInProgress=true` across the kill (`:111`, cleared `:397`) +
  `SuppressExitedEvent()` before `Kill(entireProcessTree:true)` (`:317-318`)
  + the exit-code discriminator (`CrashDetect.cs:64`) — no false `Crashed`
  event, the v2.41.2-r4 suppression contract intact.
- 5s bounded wait (`:321-324`), handle dispose/null, `State=Stopped`.
- Windows adapter teardown: sync `DisableOrphanedAdapter` ("frees the kernel
  handle so Windows drops the routes immediately") + async
  `TryRemoveAdapterAsync` (`Lifecycle.cs:360-386`) — this IS the W0.1
  "adapter dies -> OS restores NIC routes" requirement. The goal plan's
  optional `GetIfTable2`/`DeleteIpForwardEntry2` zombie assert is NOT added:
  the existing 3-layer ladder (StopInternal cleanup → `PreStartCleanupAsync`
  at `Lifecycle.cs:638-651` → TUN-orphan netsh recovery
  `HealthMonitor.cs:954-1000`) already covers the zombie case.
  <!-- ponytail: skip the P/Invoke assert; existing cleanup ladder covers it.
       Add only if a field log shows routes surviving a netsh disable. -->

### 3.2 `HealthMonitor` — wedge detection (~30 lines)

New state (near `_unhealthyHealthProbeStreak`, `HealthMonitor.cs:58`):

```csharp
// W0.1 (2026-07-03): Windows hang detection. On Windows IsHealthy() is
// process-liveness only, so "alive but Clash API dead" (wedged) black-holes
// ALL traffic incl. split-excluded apps and is otherwise never detected
// (macOS folds the API into IsHealthy and already kills on hang).
// _wedgeServingConfirmed latches after the FIRST successful serving probe of
// each sing-box lifecycle: without it we would false-kill during TUN warm-up
// (~16s; strict-mode 2 ticks = 10s < warm-up) and restart-storm custom
// configs whose clash_api listens on a non-default port (HealthMonitor's
// probe targets 127.0.0.1:9090; generated configs always emit that —
// ConfigGenerator.cs:154 + VPNConfig.cs:823 — custom ones may not).
private int _wedgeNotServingStreak;
private bool _wedgeServingConfirmed;
private const int WedgeKillThreshold = 2; // house style: HealthProbeRestartFailThreshold / StrictDnsFailThreshold
```

`OnHealthTick`, immediately after `var isHealthy = _singBox.IsHealthy();`
(`:402`), plus a memoized probe shared by the whole tick:

```csharp
// One Clash probe max per tick, shared by wedge detection, the deferred
// kill-switch lift (:418) and the DnsLeakLockdown reconcile (:507).
bool? servingProbe = null;
bool Serving() => servingProbe ??= ClashApiResponds();

if (OperatingSystem.IsWindows() && isHealthy && !_isStopping
    && (_vpnWasRunning || _shouldBeRunning))
{
    if (Serving())
    {
        _wedgeServingConfirmed = true;
        _wedgeNotServingStreak = 0;
    }
    else if (_wedgeServingConfirmed
             && ++_wedgeNotServingStreak >= WedgeKillThreshold)
    {
        _wedgeNotServingStreak = 0;
        _wedgeServingConfirmed = false;
        _logger.Warning("[HealthMonitor] sing-box alive but Clash API dead for {N} consecutive probes — killing wedged process (crash-parity recovery)",
            WedgeKillThreshold);
        try { _singBox.KillWedgedForRecovery(); }
        catch (Exception killEx) { _logger.Error(killEx, "[HealthMonitor] Wedge kill failed"); }
        OnSingBoxCrashed(this, EventArgs.Empty); // block rules + DNS fail-open + VpnStopped + AttemptRestart
        return; // synthetic crash consumed this tick; finally still clears the re-entry guard
    }
    else if (_wedgeServingConfirmed)
    {
        _logger.Warning("[HealthMonitor] sing-box alive but Clash API not responding ({N}/{Max}) — wedge suspected",
            _wedgeNotServingStreak, WedgeKillThreshold);
    }
}
```

And two mechanical substitutions inside the same tick body so the probe runs
at most once: `:418` `var clashServing = ClashApiResponds();` →
`var clashServing = Serving();` and `:507`
`bool serving = isHealthy && ClashApiResponds();` →
`bool serving = isHealthy && Serving();`. (Semantics unchanged: when
`isHealthy` is false the wedge block is skipped and `:507` short-circuits
without probing, exactly as today — `HealthMonitorDnsLockdownTests` stays
green.)

Resets: `_wedgeNotServingStreak = 0; _wedgeServingConfirmed = false;` in
`Start()` (next to `:260`) and in `OnSingBoxCrashed` (next to `:684`) — a
fresh process must re-earn the latch before wedge-kills arm.

Why `OnSingBoxCrashed(this, EventArgs.Empty)` directly is correct:
- HealthMonitor is the ONLY production subscriber of `SingBoxManager.Crashed`
  (`HealthMonitor.cs:235`; repo grep confirms no others) — no listener is
  bypassed.
- Its body is exactly the desired sequence (`:679-708`): `_isStopping`
  re-check (aborts if a user Stop won the race meanwhile), `_vpnWasRunning=false`,
  `VpnStopped` (UI shows reconnecting), `EnableBlockRules` (routed apps stay
  fail-closed once the TUN dies), `ReconcileLockdownForHealth(false)` (DNS
  fail-open for excluded apps), `AttemptRestart` (backoff 5/10/20/40/80s, G4
  ceiling `:738-756`).
- The continuation then works UNMODIFIED: `IsRunning()` `:815` is now false
  (killed) → scan → hot-reload fails fast on the dead process →
  `ReloadConfigJson(configJson, forceRestart:true)` `:874` → `Restart()` →
  `StopInternal(releaseLock:false)` no-ops on the null handle →
  `LaunchProcess` under the still-held TUN lock → deferred block-rule lift
  arms (`:912`) and lifts only on confirmed serving (`:415-435`).

### 3.3 Optional but recommended: `WedgeKillPolicy` (~12 lines, CI coverage)

House precedent: `DnsLockdownPolicy` / `StrictDnsFailoverPolicy` exist
precisely so the decision matrix is unit-testable on Linux CI (the wedge
integration tests below are Windows-only). Pure static
`WedgeKillPolicy.ShouldKill(bool processHealthy, bool serving,
bool servingConfirmedOnce, int notServingStreak)` mirroring the inline
condition, called from the tick. Skip if reviewers prefer fewer files — the
inline logic is the spec.

### 3.4 Explicitly NOT in scope

- No change to `IsHealthy()` semantics (only production caller is
  `HealthMonitor.cs:402`, but `SingBoxManagerStateMachineTests:224` pins it,
  Android overrides it, and redefining it would false-kill during warm-up —
  the latch approach is strictly safer).
- No Linux wedge-kill: Linux `IsHealthy()` is wrapper-handle-based and
  already-false in pkexec mode (`Health.cs:38` — the pkexec wrapper exits
  immediately), so the `isHealthy` gate never opens there; capability-mode
  Linux could be added later behind the same gate if wanted. W0 is
  Windows-scoped.
- No W0.2 canary, no `GetConnectionsAsync` dataplane check, no
  `GetIfTable2` assert (see 3.1 note).
- W0.3 — nothing to do (already shipped in v2.42.0, see 1.2).

## 4. Latency (Q4)

| Case | Excluded apps back in | Notes |
|---|---|---|
| Crash (today, unchanged) | ~1-2s | adapter dies with process; routes restored; DNS lockdown lifted at `HealthMonitor.cs:703` within ~ms + netsh (~1s) |
| Hang, strict mode (5s ticks) | **~11-15s** | 2 ticks x (5s + <=1s probe) + kill (usually ~0.1s, cap 5s) + route restore ~1-2s |
| Hang, normal mode (30s ticks) | ~62-70s | same formula; StrictMode is the existing user-facing knob for faster detection |
| Hang, today | infinite | undetected; UI shows connected |

Routed apps after a hang-kill: fail-closed behind block rules until the
relaunch confirms serving (deferred lift, `:415-435`) — same contract as a
crash. Residual black-hole = the detection window itself (2 ticks),
irreducible in W0; W1 removes it for excluded apps by construction.

## 5. Test plan

Guard (must stay green — run explicitly before ship):
`HealthMonitorRecoveryGapTests` (branch semantics + the 2-consecutive-failure
pin), `HealthMonitorDnsLockdownTests` (tick reconcile + crash lift),
`HealthMonitorTimerRaceTests`, `HealthMonitorStopVsRestartRaceTests`,
`HealthMonitorFailoverTriggerTests`, `HealthMonitorTunOrphanRestartTests`,
`HealthMonitorStrictDnsFailoverTests`, `HealthMonitorStartIdempotencyTests`,
`SingBoxManagerReconnectStopSuppressionTests`,
`SingBoxManagerStateMachineTests`, `SingBoxManagerProcessRunnerTests`.

New: `HealthMonitorWedgeKillTests.cs` — recipe proven by
`SingBoxManagerProcessRunnerTests`: `FakeProcessRunner` + `FakeProcessHandle`
via the `runner:` ctor seam (`SingBoxManager.cs:217`) gives a live-handle
manager (`IsHealthy()==true`); `FakeSingBoxApi { TunnelHealthy=false }` via
the `api:` seam gives a dead probe; `NullWindowsDnsHardening` + stub firewall
capture effects; reflection `InvokeOnHealthTick` (established pattern).
Windows-only (`if (!OperatingSystem.IsWindows()) return;` — same as the
ProcessRunner suite; dev-box ProgramData baseline caveat applies).

1. `Wedge_TwoDeadProbes_AfterConfirmedServing_KillsAndRunsCrashFlow` —
   tick1 api-healthy (latch), tick2+tick3 api-dead → assert
   `FakeProcessHandle.KillCallCount>=1`, block rules enabled,
   `RestartAttempted==1`, dns reconcile saw `serving=false`, `VpnStopped`
   fired.
2. `Wedge_NeverServed_NeverKills` — api dead from birth (custom-port config
   class), 5 ticks → `KillCallCount==0`, `RestartAttempted==0`.
3. `Wedge_SingleBlip_ResetsStreak` — dead, alive, dead, alive → no kill;
   latch stays armed.
4. `Wedge_IsStopping_Gate` — `_isStopping=true` → no kill (and
   `OnSingBoxCrashed`'s own gate as backstop).
5. `Wedge_CountersReset_OnCrash` — after synthetic crash the latch is false
   until the next serving probe.

Plus `WedgeKillPolicyTests` (if 3.3 lands) for Linux-CI coverage of the
matrix, and one MCP/live check per rule #1a: connect on the test VM,
`Suspend-Process` on sing-box.exe (PowerShell `Debug-Process`-style SIGSTOP
emulation, per the W0 acceptance), verify excluded app recovers within the
strict-mode budget and the log shows the wedge WARN pair + crash-parity
recovery lines.

## 6. Residual risks (top 3 first)

1. **False kill on a >2-tick control-plane stall that would have recovered**
   (GC pause storms, extreme CPU starvation). Cost: one TUN bounce (~20s for
   routed apps) — bounded, visible in logs via the two WARN lines. Mitigants:
   latch + threshold 2 + 1s probe cap; to false-kill in normal mode the API
   must be dead-to-probes across >=31s.
2. **Repeated-wedge cycling doesn't accumulate toward the G4 ceiling**: after
   each successful relaunch a tick observes process-alive →
   `_restartAttempts=0` reset (`HealthMonitor.cs:472-484`), so a
   pathologically re-wedging sing-box cycles kill/restart ~once per
   2 ticks + backoff instead of tripping FailoverRequested. Same semantics as
   an existing slow-crash-loop; visible (VpnStopped/VpnStarted churn), not
   silent. Accepted for W0; W1 obsoletes.
3. **Dataplane-only wedge is invisible** (API answers, forwarding dead — e.g.
   WSAENOBUFS storms): deliberately out of scope; killing local sing-box is
   not established as the right medicine there, and a through-TUN canary
   can't distinguish it from a dead exit server. Collect field signal first.
4. Kill can't reap a process stuck in an uninterruptible kernel wait
   (wintun driver bug): `WaitForExitAsync` caps at 5s, relaunch may hit
   ERROR_FILE_EXISTS → existing TUN-orphan ladder recovers
   (`CrashDetect.cs:256-317` + `HealthMonitor.cs:954-1000`).
5. Debounce-restart racing a wedge tick: bounded double-bounce, self-heals
   via the `IsRunning()` skip + B2/_restartInProgress suppression; no storm
   (verified interleavings in §3.2 rationale).
