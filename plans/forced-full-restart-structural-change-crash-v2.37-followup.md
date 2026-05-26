# Forced-full-restart on structural change → sing-box crash exit -1

**Status**: identified post-v2.37.0-r51, candidate for v2.37.x or v2.38.0 backlog.
NOT a blocker for v2.37.0 stable cut.

## Symptom (user report)

ekko (Z:\ekko) — "split mode у меня не работал нормально потом заработал".
Timeline from logs:

- 08:26: VPN started, split mode, 91 process_names — **stable for 9.5h**
- 18:17:39: user opened Network tab (tab=2)
- 18:17:44: Apply flipped `routing_mode = full` → `[StartupPipeline] Full-tunnel
  mode — ignoring ActiveProfile`
- 18:17:44: `VpnEngine.Apply` saw `Process list change detected (+0/-91)` → 
  `Forced full restart (structural change)` → `SingBoxManager.Restart` → 
  **sing-box crashed exit -1** during Stop/Launch race
- **80-minute outage** (full tunnel + crashed sing-box, no internet)
- 19:37:29: user flipped routing_mode back to split → same path → another 
  crash exit -1 → HealthMonitor restart loop (5/10/20s backoff) → recovered
- 20:42+: stable split mode again

User perceives: "split не работал → потом заработал". Reality: split-mode itself
fine for 9.5h before AND stable after; bug is in the transition flip.

## Root cause hypothesis

`VpnEngine.Apply` (VpnEngine.cs) on `routing_mode` change calls 
`SingBoxManager.Restart` which kills + relaunches sing-box. During Stop, TUN
adapter teardown races with the Launch step's `LaunchProcess` re-acquisition →
sing-box exit -1.

v2.35.3 Task #53 hardened the standalone `SingBoxManager.Restart` TUN-lock 
release path, but the `Forced full restart (structural change)` ESCALATION 
path in `VpnEngine.Apply` (line ~1367 in MainWindowViewModel.ApplyPendingChangesInternalAsync 
→ VpnEngine.Apply → ConfigGenerator.Generate → SingBoxManager.ReloadConfig 
→ Restart) takes a different code path that didn't get the same fix.

The fact that two consecutive flips (18:17 + 19:37) both crashed with the 
exact same `exit -1` signature suggests deterministic race, not environmental
flakiness.

## Reproduction (estimated)

1. Start VPN in split mode with a real process catalogue (~90 process_names)
2. Wait until `[VpnEngine] Connected` 
3. In Network settings, flip routing mode toggle (split → full OR full → split)
4. Watch logs for `Forced full restart (structural change)` + 
   `[ERR] [SingBoxManager] sing-box crashed (exit code: -1)`

Expected after fix: clean transition without crash, OR if crash unavoidable,
recovery in <3s instead of HealthMonitor's 5/10/20s backoff cascade.

## Proposed fixes (ranked)

1. **Best**: detect routing_mode flip BEFORE Restart, do explicit Stop → wait 
   for TUN teardown (use existing TunLock mechanism + `WaitForTunAdapterRemoved` 
   from TunAdapterDiagnostics) → then LaunchProcess. Don't rely on 
   `EnableRaisingEvents=false` + Kill alone for structural change.

2. **Acceptable**: tighten HealthMonitor backoff for known-recoverable crashes 
   (exit -1 within 50ms of intentional Stop). Drop first retry to 1s instead 
   of 5s.

3. **Minimal**: UI hint in Network tab: "Changing routing mode will briefly 
   disconnect VPN. For best results, Stop VPN first → change → Start." But 
   this is user-education, not engineering fix.

## Don't ship without

- Repro test in VPNRouter.Tests (use `IProcessRunner` seam from 2.35.x Phase 4 
  migration to inject controlled Stop/Start sequence; assert no double-Restart
  cascade)
- Live test: split → full → split → full flip cycle, 5 rounds, no crash 
  required
- Brat regression: ekko's exact scenario (Germany VLESS sub, 9 profiles 
  merged, 90 processes) — capture full log timeline

## NOT in scope

- TUN-orphan crash path (separate signature: "configure tun interface: device 
  is not ready") — that's the AutoFailover-handled crash, already working in 
  v2.35.3+. Don't conflate.
- AutoFailover delegate cancellation handling — that's a different issue with 
  `StartupPipeline.ResolveProfileAndServersAsync` getting OperationCanceled 
  during in-flight Stop. Currently benign, no user-visible impact.

## File pointers

- `VPNRouter.Core/Services/VpnEngine.cs` — `Apply` method, look for 
  "Process list change detected" log line, `Forced full restart` branch
- `VPNRouter.Core/Services/SingBoxManager.cs` — `Restart` method, the 
  Stop→LaunchProcess sequence
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — 
  `ApplyPendingChangesInternalAsync` calls SaveSettings → Reload → 
  VpnEngine.Apply path
- `Z:\ekko\vpnrouter20260525_002.log` lines 2541–2570 (first crash), 
  lines 2948–2970 (second crash) — concrete repro evidence
