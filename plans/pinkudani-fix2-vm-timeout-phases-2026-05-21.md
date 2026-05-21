# PinkuDani Fix #2 — VM Start timeout phase split — **REFUSE-TO-PROCEED**

**Date**: 2026-05-21
**Status**: BLOCKED — VpnEngine event surface incompatible with the proposed
two-phase pattern. No code change made. No tests added. No commit.

## Triggering brief

`brat` asked for a two-phase Start timeout in
`MainWindowViewModel.ToggleConnectionAsync`:

- **Phase A** (60s) — from `ToggleConnectionAsync` invocation until
  `VpnEngine.SingBoxStarted` event fires. Covers slow firewall rule
  creation, Remove-NetAdapter PowerShell calls, etc.
- **Phase B** (20s) — from `SingBoxStarted` until `VpnEngine.Connected`
  event fires. Covers TUN warm-up (gstatic.com probe).

The brief includes an explicit **refuse-to-proceed clause**:

> If VpnEngine doesn't expose `SingBoxStarted` / `Connected` events with
> usable shape → STOP and report (we'd need to add them, which is out of
> scope here).

That clause fires.

## What I verified (read-only)

### Existing event surface on `VpnEngine` (lines 76–100)

```
public event Action<string>?       StatusChanged;
public event Action<string, int>?  ProcessDetected;
public event Action<int, int>?     RestartAttempted;
public event Action<string>?       Warning;
public event Action<int>?          SingBoxStarted;
public event Action<string>?       AutoFailoverTriggered;
```

**No `Connected` event.** No `TunReady`, no `TunWarmedUp`, no equivalent.
`grep` across `VPNRouter.Core` confirms.

### What `SingBoxStarted` actually signals

Wired in `StartupPipeline.cs:997`:
```
singBox.Started += pid => _host.OnSingBoxStarted(pid);
```

Fires from `SingBoxManager.Started` AFTER `singBox.StartWithJson(configJson)`
returns AND `singBox.IsRunning()` becomes true. That happens BEFORE the
fire-and-forget warmup probe is scheduled. So `SingBoxStarted` ≠ TUN ready,
it means "sing-box process is alive". Suitable for Phase A boundary. OK.

### What we use TODAY as a "Connected" signal

`StartupPipeline.ScheduleWarmupProbe` (lines 1057–1102) fires
`_host.OnStatus($"Connected (PID {pidSnapshot})")` in **both branches**:

- Line 1073 — gstatic.com responded → real TUN ready (success).
- Line 1101 — 15-attempt warmup loop expired → still emits the same
  `Connected (PID ...)` text (failure → "leave the user with internet").

`MainWindowViewModel.OnEngineStatus` (line 3572) keys off the
`status.StartsWith("Connected")` substring to flip `IsConnected = true`.
**That means even today's success/failure distinction is invisible to the
UI.** Both branches set IsConnected=true.

The brief's Phase B requires a `Connected` event that fires ONLY on
genuine TUN ready and NOT on warmup-timeout. The current StatusChanged
emission can't disambiguate.

### Race / structural issue with the proposed pattern (even without Phase B)

`VpnEngine.StartAsync` is already an awaited end-to-end pipeline that
INTERNALLY waits up to 5s for sing-box to come up
(`StartupPipeline.StartSingBoxPhaseAsync` line 1002–1013, throws if not
running). The proposed pattern is:

```csharp
var startTask = _vpnEngine.StartAsync(settings, ct);
var sbStartedTask = startedTcs.Task;
var phaseATimeout = Task.Delay(60_000, ct);
var winner = await Task.WhenAny(sbStartedTask, phaseATimeout, startTask);
```

If sing-box can't start, `startTask` throws BEFORE 60s — it'll be the
winner, and we'll fall out via the existing exception path. Phase A
timer only fires for the ACTUAL slow-pre-start-cleanup case (which is
the PinkuDani trace: 32s of Remove-NetAdapter loops). That's fine.

But for the success path: `startTask` completes successfully AT THE
SAME TIME (or shortly after) `SingBoxStarted` fires. Both are racing.
The brief assumes Phase A boundary is "sing-box started" then we move
to Phase B — but in practice once `startTask` completes, the WHOLE
warmup probe has already been SCHEDULED as fire-and-forget. The probe
runs OUTSIDE `startTask`'s await. So waiting on "Connected" event
post-startTask is the only sensible Phase B — but we don't have that
event.

### The 10s `_engine.IsRunning` poll after StartAsync (lines 3808–3817)

This is the CURRENT Phase B equivalent. It polls `IsRunning` (which
checks process liveness, not TUN ready) for up to 10s. If false after
10s, leaves state to `OnEngineStatus`. This already handles the
"sing-box up but TUN not ready" case in a way — it just doesn't fail-
hard, it relies on the eventual `StatusChanged` "Connected" string to
flip the UI. That's not bullet-proof but it doesn't fire a Stop.

The brief's Phase B EXPLICITLY wants to fire a `Stop` on Phase B
timeout. To do that safely we need a positive "TUN ready" signal, not
just "sing-box alive 10s".

## Why I can't safely proceed

Three converging blockers:

1. **No `Connected` / `TunReady` event on `VpnEngine`** — the brief's
   refuse-to-proceed clause covers this exactly.
2. **The existing "Connected (PID ...)" StatusChanged message is
   ambiguous** — fires on both warmup success AND warmup expiry, so
   sniffing it for Phase B success would also accept a failure.
3. **Implementing Phase B without a positive TUN-ready signal means we'd
   add a 20s "kill VPN if not connected" timer that triggers Stop on
   slow TUN regardless of whether the connection is actually working.**
   That regresses the existing behavior at line 3842 ("leave state to
   OnEngineStatus") which is INTENTIONALLY lenient because the warmup
   probe takes up to 15s on slow hosts.

The fix as written requires a Core-layer addition: a real `Connected`
event (or `TunWarmedUp(bool success)` event) raised by
`StartupPipeline.ScheduleWarmupProbe` ONLY in the success branch. The
brief excluded that work from this task's scope.

## What WOULD be safe to ship from this brief

A narrower variant that doesn't conflict with the refuse clause:

- **Extend the existing single timer from 30s → 60s** in
  `MainWindowViewModel.ToggleConnectionAsync` line 3776. That alone
  rescues the PinkuDani scenario where 32s of Remove-NetAdapter
  exceeded the 30s budget. The current StartAsync internal timeline:
  Resolve+Scan+Generate+Firewall ~10s, PreStartCleanup ~5-30s
  (PowerShell-heavy), StartSingBox ~5s, monitors ~1s → ~50s wall in
  worst case. 60s gives the same comfort margin the brief asks for
  with zero structural risk.
- Update the error log + status text to reference 60s.
- Add 1–2 tests around the existing CancellationTokenSource lifetime
  (e.g. that the CTS isn't observed twice on race).

I did **not** ship this narrower variant either — the brief was
specific about the two-phase pattern, and the user (brat) signed off
on that, not on a budget bump. Decision on what to do next is the
user's.

## Recommended path forward

Two-stage plan, neither of which I'm authorized to do in this session:

**Stage 1 (Core, separate brief)**:
- Add `public event Action<int>? Connected` to `VpnEngine`.
  Signature: pid of the sing-box that's now confirmed routing.
- Wire it from `StartupPipeline.ScheduleWarmupProbe` line 1073 (success
  branch only). Optionally pass through to the `VpnEngineStartupHost`
  callback `OnTunReady(int pid)` and have the host raise the event.
- Update `MainWindowViewModel.OnEngineStatus` to subscribe to
  `_engine.Connected` directly instead of sniffing the `StatusChanged`
  string. Keep the string sniff as a fallback for back-compat.
- Tests: characterize that `Connected` fires exactly once per Start
  cycle and only on success.

**Stage 2 (App, this brief retargeted after Stage 1)**:
- Phase A/B split as originally specified, now feasible.

## Files I would touch in Stage 1 (for the next agent)

| File | Edit |
|---|---|
| `VPNRouter.Core/Services/VpnEngine.cs` | Add `public event Action<int>? Connected;` near line 92 with comment. |
| `VPNRouter.Core/Services/VpnEngine.cs` | Add `OnConnected(int pid)` callback near line 811 in `VpnEngineStartupHost`. |
| `VPNRouter.Core/Services/StartupPipeline.cs` | Add `void OnConnected(int pid);` to `IStartupHost` near line 181, call it from `ScheduleWarmupProbe` line 1073 (success branch only — NOT 1101). |

## Files coordinated with other agents

- TunAdapterDiagnostics.cs — parallel agent (Fix #1 + #4). Not touched
  by this brief.
- SingBoxManager.cs — parallel agent. Not touched.

## What this session changed in the repo

Nothing under `VPNRouter.*/`. Only this brief was added under
`plans/pinkudani-fix2-vm-timeout-phases-2026-05-21.md`. No commit
was created.
