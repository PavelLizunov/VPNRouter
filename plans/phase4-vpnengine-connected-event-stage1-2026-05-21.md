# Task #41 Stage 1 — VpnEngine.Connected event (Core)

**Date**: 2026-05-21
**Owner**: PinkuDani session
**Branch**: main (direct commit)
**Predecessor brief**: `plans/phase2G-vpnengine-startasync-seam-2026-05-21.md`
(Agent B's correctly-refused attempt at the App-side two-phase VM timer)
**Effort**: ~1.5 hours
**Risk**: LOW — additive interface method + 1 new call site + 1 new test file
**Blast radius**: 2 production files (`VpnEngine.cs`, `StartupPipeline.cs`)
+ 2 test files updated (host adapters) + 1 new test file
**Rollback**: `git revert <commit>` — clean revert, event is additive.

## Why

A prior "Fix #2" attempt wanted to split the App-side 30-second VM Start
timer into Phase A (start budget — wait for sing-box to come up) +
Phase B (TUN warm-up budget — wait for confirmed routability). The spawn
agent correctly REFUSED that approach because:

> `VpnEngine.Connected` event does not exist. The existing
> `"Connected (PID N)"` string emission via `StatusChanged` is ambiguous —
> `StartupPipeline.ScheduleWarmupProbe` emits the SAME text in BOTH
> success branch (line ~1088, gstatic probe succeeded) AND failure
> branch (line ~1120, 15-attempt loop expired). Phase B sniffing would
> accept warmup failure as success.

Stage 1 (this change) closes that gap. A typed `Connected(int pid)` event
fires ONLY when the warmup probe's success branch runs. Stage 2 (App-side
two-phase VM timer in `MainWindowViewModel`) can now subscribe to this
event for an unambiguous Phase B completion signal.

## What

### A. Event field added to VpnEngine

`VPNRouter.Core/Services/VpnEngine.cs:95`:

```csharp
/// <summary>
/// Fires EXACTLY ONCE per successful Start lifecycle when the TUN warmup
/// probe (StartupPipeline.ScheduleWarmupProbe) confirms gstatic.com is
/// reachable through the tunnel. Payload is the sing-box PID.
/// ...
/// </summary>
public event Action<int>? Connected;
```

Placed alphabetically near `SingBoxStarted` (its sibling event). Ctor
signature is **NOT** changed — adds a new field only.

### B. Wiring chosen: IStartupHost callback (Option 1)

The brief proposed three architectures (`IStartupHost` callback, event,
direct delegate). I picked the IStartupHost callback because it matches
the existing channel for every other engine event (`OnSingBoxStarted`,
`OnAutoFailoverTriggered`, `OnProcessDetected`, etc.). Single chokepoint
in `IStartupHost` for any future engine event, single chokepoint in
`VpnEngineStartupHost` for the public-event raise.

**Interface addition** (`StartupPipeline.cs:186`):

```csharp
/// <summary>
/// Task #41 Stage 1 (2026-05-21) — forward the "TUN warmup probe
/// confirmed reachability" notification. ...
/// </summary>
void OnConnected(int pid);
```

**VpnEngine-side implementation** (`VpnEngine.cs:838`):

```csharp
public void OnConnected(int pid) => _engine.Connected?.Invoke(pid);
```

### C. Fire site: success branch only

`StartupPipeline.cs:1100` — immediately after the existing
`OnStatus($"Connected (PID {pidSnapshot})")` in the success branch:

```csharp
try { _host.OnConnected(pidSnapshot); }
catch (Exception ex)
{
    _host.Logger?.Warning(ex,
        "[StartupPipeline] OnConnected callback threw (non-fatal)");
}
```

Mirrors the BR-7 deferred-lockdown pattern that already lives in this
branch — try/catch + Warning log so a subscriber's exception cannot
break the warmup-success state transition.

`StartupPipeline.cs:1138` — failure branch gets a comment block calling
out the invariant in case a future refactor tries to "fix the
asymmetry":

```csharp
// Task #41 Stage 1 (PinkuDani 2026-05-21) — INTENTIONALLY NOT
// calling _host.OnConnected here. The OnStatus string above is
// ambiguous (it's emitted on both branches for back-compat with
// pre-#41 consumers that scan StatusChanged for "Connected (PID");
// the typed OnConnected event must stay silent so App-side
// consumers can distinguish actual TUN-ready from "warmup loop
// expired but we let sing-box live anyway." Do NOT add a call
// here without first migrating Stage 2 off the
// success-branch-only invariant.
```

### D. Test wiring

`VPNRouter.Tests/VpnEngineConnectedEventTests.cs` — new file, 4 tests:

1. **`Connected_SuccessBranchOnly_FiresViaHostAdapter`** — constructs
   `VpnEngine.VpnEngineStartupHost` via reflection (it's a private
   nested type), calls `OnConnected(31415)`, asserts the engine's
   public `Connected` event fires exactly once with the supplied PID.

2. **`Connected_FailureBranchSilent_SourcePin`** — defence-pin test
   that reads `StartupPipeline.cs` via `File.ReadAllText` and asserts:
   * total `_host.OnConnected(` call sites in the file = exactly 1.
   * within the `ScheduleWarmupProbe` method, the section AFTER
     "TUN warm-up failed after" (the failure branch) does NOT contain
     `_host.OnConnected(`.
   * the failure branch DOES still contain the back-compat
     `OnStatus($"Connected (PID {pidSnapshot})` string emission.

3. **`Connected_FiresOncePerLifecycle_TwoCallsTwoEvents`** —
   `OnConnected(11111)` + `OnConnected(22222)` on the SAME host
   adapter produces 2 event raises with the correct PIDs. Pins
   "no de-dup at engine level" — the host is a passive forwarder.

4. **`Connected_NullSubscription_DoesNotThrow`** — invoking
   `OnConnected` on a host whose engine has NO `Connected` subscriber
   is a clean no-op (no NRE). Pins the C# `event?.Invoke()` idiom
   stays in place against a future refactor.

### Test-host updates (compile-clean side-effect)

Two existing test classes that implement `StartupHostInternal` get the
new `OnConnected` method:

* `VPNRouter.Tests/StartupPipelineTests.cs::TestStartupHost` — adds a
  `ConnectedPids` recorder list so future pipeline tests can drive +
  pin the success branch through the full pipeline once an IHttpClient
  seam lands. Currently unused (Stage 1 doesn't drive the pipeline
  end-to-end), but the recording is free.
* `VPNRouter.Tests/VpnEngineLifecycleTests.cs::HotReloadTestHost` —
  no-op `OnConnected(int pid) { }` (HotReload mode never reaches phase 7
  where the warmup probe is scheduled).

## Why source-pin for the failure-branch-silent invariant

The cleanest invariant test would be: spin the full `ScheduleWarmupProbe`
through a failure branch (`http.GetStringAsync` always throws) and pin
that the engine's `Connected` event NEVER fires. That needs an
`IHttpClient` seam injected into `StartupPipeline`, which `ScheduleWarmupProbe`
does NOT have today — it instantiates `new HttpClient` inline (line 1078).

Same gap is already documented in `VpnEngineLifecycleTests.cs` file
header ("End-to-end 'warmup probe succeeds → EnableLockdownIfConfigured
fires' coverage" deferred to a future seam). The source-pin pattern is
the equivalent defence used by `VpnEngineApplyEscalationTests` and by
`VpnEngineLifecycleTests.Apply_HotReloadPipeline_DoesNotTouchDnsHardening_SourcePin`
— it's an established idiom in this codebase.

When the `IHttpClient` seam lands (Stage 1.5 / Phase 5 candidate),
Test 1 can be upgraded from "drive the adapter directly" to "drive
the full warmup loop" and Test 2 can be upgraded from source-pin to
an actual invocation test.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → **0 errors, 0 warnings**
  (incremental).
- [x] Full test suite (excluding `PageScreenshotTests`, `HeadlessGuiTests`,
  `VisualDiffTests`): **1339 passed / 4 skipped / 0 failed**. Baseline
  1335 + 4 new tests = 1339. No regressions.
- [x] All 4 new `VpnEngineConnectedEventTests` green (`216 ms`).
- [x] Existing `VpnEngineLifecycleTests` + `StartupPipelineTests` stay green
  (the touched test-host stubs got the new method, no behavioural
  change).
- [x] Brief above filled with Why / What / Outcome.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/VpnEngine.cs` | +29 LOC. Added `Connected` event field + `OnConnected` forwarder on the `VpnEngineStartupHost` nested adapter. |
| `VPNRouter.Core/Services/StartupPipeline.cs` | +37 LOC. Added `OnConnected` to `IStartupHost` interface; added try/catch wrapped call site at the success branch in `ScheduleWarmupProbe`; added clarifying comment block at the failure branch. |
| `VPNRouter.Tests/StartupPipelineTests.cs` | +5 LOC. Added `OnConnected` recorder to `TestStartupHost`. |
| `VPNRouter.Tests/VpnEngineLifecycleTests.cs` | +1 LOC. Added no-op `OnConnected` to `HotReloadTestHost`. |
| `VPNRouter.Tests/VpnEngineConnectedEventTests.cs` | +291 LOC (new). 4 characterization tests. |
| `plans/phase4-vpnengine-connected-event-stage1-2026-05-21.md` | This brief. |

### Build + test numbers

* Build: **0 errors / 0 warnings** (incremental rebuild post-edit).
* Full test suite (excluding GUI/Visual): **1339 passed / 4 skipped /
  0 failed** vs **1335 baseline** = +4 new tests, no regressions.
* New tests alone: **4 passed / 0 failed / 216 ms**.

### Surprises

* `ScheduleWarmupProbe`'s success branch ALREADY had the BR-7 deferred-
  lockdown call wrapped in try/catch with a Warning logger. I mirrored
  that pattern for the new `_host.OnConnected(pidSnapshot)` call —
  cheaper than dealing with a subscriber exception breaking warmup
  state.
* The `IStartupHost` interface was already a natural chokepoint. Three
  proposed architectures in the brief (callback / event / delegate),
  but the existing 8 other engine events all flow through `IStartupHost`,
  so picking a different channel would have been an unnecessary
  asymmetry. Hindsight: this should've been the obvious first choice.
* xUnit test discovery saw the new file immediately without any
  csproj-level changes — `Get-ChildItem VPNRouter.Tests\*Tests.cs`
  auto-discovery (per `VPNRouter.Tests/CLAUDE.md`) just works.

### Stage 2 status

**Unblocked.** Stage 2's `MainWindowViewModel` change can now subscribe
to `_vpnEngine.Connected` to drive the Phase B (TUN warm-up) timer
transition. Recommended Stage 2 pattern (out of scope for this brief):

```csharp
_vpnEngine.Connected += pid =>
{
    if (_phaseB?.IsRunning == true)
    {
        _phaseB.Cancel();   // success — TUN really up
        Dispatcher.UIThread.Post(() => Status = "Connected");
    }
};
```

The Phase A timer continues to use `SingBoxStarted` (existing event) to
detect "sing-box came up." Phase B's failure path stays on the existing
30-second user-facing total budget; the new event accelerates the
**success** path by not having to wait for the OnStatus string parsing.

### Follow-ups spawned

None. Stage 2 is a separate ticket per the brief.

### Brief

`plans/phase4-vpnengine-connected-event-stage1-2026-05-21.md` (this file).
