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
