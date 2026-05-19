# Hotfix — TUN adapter pre-enable causing auto-restart loop FATAL

**Date**: 2026-05-19
**Priority**: P0 (user-reported, current stable v2.32.2 affected, ship target v2.35.0-r4 OR backport)
**Risk**: MEDIUM (touches every sing-box launch path including Service/CLI)
**User**: `Z:/alicemoren1991`, week-long broken state, factory reset didn't help

## Symptom

User connects VPN → "connected" status shows → drops within 1-15 seconds → HealthMonitor restarts → same crash repeats 5-6 times → finally "connected" stuck but no traffic flows.

## Root cause

`SingBoxManager.LaunchProcess` (line 597, added in v2.31.9-r4) calls
`TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent(...)` which **pre-enables**
the `VPNRouter-TUN` adapter via `netsh interface set interface admin=enabled`.

Pre-enable was a workaround for a DIFFERENT FATAL ("device not ready for use")
that v2.31.5 r5's `DisableOrphanedAdapter` introduced — disabled adapters
from previous sessions would block new sing-box opens. But sing-box 1.13.x
doesn't OPEN existing adapters, it CREATES them. Re-enabling the disabled
adapter just restores its name reservation. Sing-box's wintun call hits:

```
FATAL start service: start inbound/tun[tun-in]:
configure tun interface: Cannot create a file when that file already exists.
```

The proper cleanup function `TunAdapterDiagnostics.PreStartCleanupAsync(...)`
(added in v2.32.x Bug-r9-H) does the right dance: disable → remove device
record via `powershell Remove-NetAdapter`. But it's ONLY called from
`VpnEngine.StartAsync` via `StartupPipeline`. Auto-restart paths (`Apply`,
`HealthMonitor` crash recovery, `Restart`) skip it and hit the pre-enable
trap instead.

User log evidence (`Z:/alicemoren1991/vpnrouter20260519.log`):
```
20:03:53.820  [TunDiag] StopInternal.early: disabled orphaned adapter 'VPNRouter-TUN'
20:03:55.087  [TunDiag] LaunchProcess: pre-enabled adapter 'VPNRouter-TUN' (was disabled)
20:03:55.352  [WRN] [sing-box] FATAL configure tun interface: Cannot create a file when that file already exists.
20:03:55.721  [TunDiag] OnProcessExited: disabled orphaned adapter 'VPNRouter-TUN'
... 6 more iterations ...
```

Pre-start cleanup at `VpnEngine.StartAsync` reports "no stale TUN adapters
found" while `DisableOrphanedAdapter` (which uses a different code path —
direct netsh call by known name, not enumeration) DOES find and act on
the adapter. This discrepancy is a sub-bug: the enumeration parser
(`ExtractStaleAdapterNames`) sometimes misses the adapter that's there;
the direct-by-name path always works. We'll cover this with a
defence-in-depth approach.

## Why now (week-long onset)

`Z:/alicemoren1991/vpnrouter20260514.log` is 3 KB (~no activity), then
2026-05-15+ logs balloon. Patch Tuesday was 2026-05-14. Likely Windows
Update changed wintun driver state-machine timing OR the network stack's
adapter teardown latency, breaking our pre-enable workaround that
previously "happened to work" by racing with Windows cleanup.

## Fix strategy

**Two-pronged code change + comprehensive tests + audit**:

### Code (Agent 1)

1. **Replace pre-enable with full cleanup in `LaunchProcess`** —
   `SingBoxManager.cs:597`. Replace `EnsureAdapterEnabledOrAbsent` call with
   synchronous wrapper around `PreStartCleanupAsync`. Pros: every launch
   path (user Start, Apply restart, HealthMonitor crash recovery, manual
   Restart) gets the same disable → remove → settle treatment.

2. **Strengthen `OnProcessExited` cleanup** — `SingBoxManager.cs:792`.
   Currently only `DisableOrphanedAdapter`. Add async `TryRemoveAdapterAsync`
   chained after — so the device record is gone by the time HealthMonitor's
   restart attempt fires (~5-10 s later). The settle delay already in
   `Restart()` (`Thread.Sleep(750)`) covers timing.

3. **Strengthen `StopInternal.early` cleanup** — similar treatment, line
   ~234. Disable + remove on the graceful Stop path.

4. **Retire `EnsureAdapterEnabledOrAbsent` OR demote to fallback** — the
   function is mis-purposed for current sing-box behavior. Either delete
   (with grep proof of no other callers) OR document as legacy with
   "DO NOT use in launch path" header.

5. **Cross-platform guard** — every change wrapped in
   `OperatingSystem.IsWindows()` or `[SupportedOSPlatform("windows")]`.
   Linux/macOS paths unchanged (no wintun → no problem).

### Tests (Agent 2)

1. **Extend `VPNRouter.Tests/TunAdapterReadinessTests.cs`** with new cases:
   - `PreStartCleanup_WithExistingEnabledAdapter_DisablesAndRemoves`
   - `PreStartCleanup_WithExistingDisabledAdapter_RemovesIt`
   - `PreStartCleanup_AdapterMissing_NoOp`
   - `PreStartCleanup_MultipleStaleAdapters_RemovesAll`
   - `PreStartCleanup_RemoveFails_LogsButDoesNotThrow`

2. **New `VPNRouter.Tests/SingBoxManagerRestartTunHandshakeTests.cs`** —
   characterization test: simulate the auto-restart loop without spawning
   sing-box (mock the IProcessRunner / inject a stub `IProcess`). Pin
   that:
   - On each restart cycle, `LaunchProcess` calls cleanup BEFORE process
     start (not pre-enable)
   - After 5 consecutive crashes, HealthMonitor backs off correctly
   - On graceful stop, adapter is removed (not just disabled)

3. **Regression pins for `ExtractStaleAdapterNames`** parser — add
   localized netsh output samples (Russian + English + German Windows)
   to confirm the parser sees adapters regardless of locale.

4. **Behavior assertion**: zero "Cannot create a file" FATALs should
   appear in the test logs after applying the fix in the mocked restart
   loop.

### Audit (Agent 3)

1. Read-only audit of every callsite that touches `VPNRouter-TUN` or
   `sing-box-tun` adapter — confirm we haven't missed a path.
2. Verify all four launch surfaces have cleanup:
   - `VpnEngine.StartAsync` → `StartupPipeline.PreStartCleanupAsync` (already there)
   - `SingBoxManager.Restart` → `LaunchProcess` (needs the fix from Agent 1)
   - `SingBoxManager.ReloadConfig` fallback (which calls Restart internally)
   - `HealthMonitor.AttemptRestart` (which calls SingBoxManager.Restart)
3. Cross-platform: Linux uses `tun` interface (kernel module), macOS uses
   `utun*`. Audit that none of the new cleanup calls leak into those
   platforms.
4. Service mode bootstrap (`VPNRouter.Service`) — different entry from CLI
   and App, may have its own first-launch logic.
5. Update this brief's Outcome with findings + any edge cases needing
   additional Agent 1 patches.

## Verification gate

- [ ] `dotnet build VPNRouter.sln -c Release` 0 errors
- [ ] `dotnet test VPNRouter.Tests` — full regression (~1124+) + new tests green
- [ ] `simplify` skill on diff if >100 LOC
- [ ] `security-review` skill on diff (PowerShell + netsh invocations =
  process exec; admin-level operations on network adapters; touches
  privileged code path)
- [ ] Audit report shows no missed call sites
- [ ] MCP smoke test: cold-start VPNRouter on this VM, connect+disconnect+reconnect
  3 times, confirm no FATAL "Cannot create a file" in vpnrouter*.log
- [ ] User-facing recovery instruction in release notes for alicemoren1991-class
  stuck users (PowerShell snippet to manually clean state before update)

## Ship target

`v2.35.0-r4` (carries Phase 6 work + this hotfix), OR if user pressure
demands a fast back-port to current stable, `v2.32.3` cherry-pick of just
the SingBoxManager + TunAdapterDiagnostics changes.

## Rollback

`git revert <commit>` — single self-contained patch. Pre-revert reproduces
the bug (verified by user). Post-revert, the v2.31.9-r4 "device not ready
for use" FATAL might come back; we accept that as the smaller-blast-radius
failure since user can `Stop → Start` to recover (v2.31.9-r4 fixed
auto-restart but introduced the worse "stuck loop" case).

## Wave 38b — full auto-heal (no user intervention required)

User feedback (2026-05-19 post-diagnosis): the brief's recommended user
PowerShell snippet (Stop-Process + Remove-NetAdapter + Restart-Computer)
is unacceptable as a recovery path. **The app must self-heal.**

Wave 38a (Agent 1) covers:
- Normal cleanup via `PreStartCleanupAsync` on every launch path
- Defence-in-depth direct-by-name disable + remove (catches adapters the
  enumeration misses)
- Async TryRemoveAdapter after every Stop / OnProcessExited

For users in alicemoren1991's state (wintun driver state-machine stuck
to the point where `Remove-NetAdapter` PowerShell cmdlet returns
success but the device record still occupies the name slot), Wave 38a
may not be enough on the first restart. Wave 38b adds escalation tiers:

### Tier 1 (Wave 38a — every launch)
- `netsh interface set interface name="VPNRouter-TUN" admin=disabled` (frees handle)
- `powershell Remove-NetAdapter -Name "VPNRouter-TUN" -Confirm:$false` (deletes record)
- Optional 750ms settle delay (already present)

### Tier 2 (Wave 38b — after 2 consecutive "Cannot create a file" FATALs)
- `pnputil /enum-devices /class Net` to identify the wintun device instance ID
- `pnputil /remove-device <InstanceId>` to force driver-level removal
  (this kills the device record even when `Remove-NetAdapter` "succeeded"
  but the kernel still holds the name)
- Re-trigger `PreStartCleanupAsync` after pnputil completes
- Log `[TunDiag] escalated to pnputil device-level removal`

### Tier 3 (Wave 38b — after 4 consecutive FATALs)
- Restart the wintun driver service entirely:
  ```
  Stop-Service WintunService -Force  (if exists)
  pnputil /restart-device <InstanceId>
  ```
  OR if no wintun service: `Restart-Service NetSetupSvc` (Windows
  Network Setup Service — owner of adapter lifecycle).
- This is a more invasive step (briefly disrupts ALL VPN adapters on
  the system, including coexisting WireGuard / AmneziaWG), but bounded
  to ~2 seconds + already in a recovery state where nothing else is
  working anyway.

### Tier 4 (Wave 38b — after 5 consecutive FATALs, ~all 5 HealthMonitor restart attempts exhausted)
- Show UI toast: "Сетевой адаптер VPN застрял. Запускаю восстановление…"
  (3-second show, no user action required)
- Schedule `pnputil /scan-devices` — forces Windows to re-enumerate
  the wintun driver, which usually unsticks the stuck state.
- If user is on Service mode, also `Restart-Service VPNRouter` so the
  Service host process gets a fresh state.

### Tier 5 (last resort — only if Tiers 1-4 all fail across 2+ launch sessions)
- Persistent toast / banner: "VPN-адаптер не отвечает. Откройте Диспетчер
  устройств → Сетевые адаптеры → удалите 'Wintun Userspace Tunnel' или
  перезагрузите систему."
- Track this state in `state.json` so subsequent app launches show it
  immediately without waiting for another 5-FATAL cycle.

### State tracking

Add `WintunStuckLevel: int 0-5` field to `RunState` (`VPNRouter.CLI/Commands/RunState.cs`):
- Starts at 0
- Increments on every "Cannot create a file" FATAL detected
- Resets to 0 on first successful sing-box.start (`sing-box started`)
- Stays at 5 between launches (per Tier 5 persistence)

`LaunchFailureCounter` already exists for a different purpose; the
wintun-stuck counter is orthogonal — track separately.

### Files Wave 38b will touch

- `VPNRouter.Core/Services/TunAdapterDiagnostics.cs` — add
  `EscalateRemovalAsync`, `RunPnputilAsync` helpers
- `VPNRouter.Core/Services/SingBoxManager.cs` — increment
  WintunStuckLevel on FATAL detection, call escalation tier based on level
- `VPNRouter.Core/Services/WintunStuckCounter.cs` — NEW file, mirrors
  `LaunchFailureCounter` shape; persists to `wintun-stuck.json`
- `VPNRouter.App/ViewModels/MainWindowViewModel*.cs` — toast trigger
  for Tier 4 + Tier 5 banner state
- `VPNRouter.Tests/WintunStuckEscalationTests.cs` — NEW. Test each
  tier triggers at the right count.

### Wave 38b NOT in initial hotfix

The initial v2.35.0-r4 hotfix ships Wave 38a only (3 agents currently
in flight). Wave 38b is the follow-up: once 38a lands, retest user
scenario via Z:/alicemoren1991 logs (if user updates) or VM
reproduction (deliberately corrupt wintun state via
`pnputil /remove-device` mid-session, then launch).

If 38a alone resolves user's situation → 38b can be deferred /
descoped. If user reports "still broken after update" → 38b ships as
v2.35.0-r5.
