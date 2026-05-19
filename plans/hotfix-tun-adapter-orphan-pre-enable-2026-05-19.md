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
## Audit findings (Agent 3)

Read-only audit conducted 2026-05-19 against pre-fix HEAD (commit
`d7bc3b5`). Brief above describes the bug + Agent 1's intended fix;
this section records the surface map, edge cases, and back-port shape.

### A. All sing-box launch surfaces (Windows)

Every code path that ends in `sing-box.exe run -c <config>` being
spawned on Windows. **Cleanup wired** means PreStartCleanupAsync or
equivalent runs before LaunchProcess; **NEEDS FIX** means the path
currently misses it and is what Agent 1's patch addresses.

| # | Path | Entry point | Cleanup wired? |
|---|---|---|---|
| 1 | User Start (App, CLI, Service) | `VpnEngine.StartAsync` → `StartupPipeline.ExecuteAsync` (`ColdStart`) → Phase 6 `PreStartTunCleanupAsync` → `SingBoxManager.StartWithJson` → `LaunchProcess` | YES via pipeline Phase 6 (`StartupPipeline.cs:914`); LaunchProcess still has redundant `EnsureAdapterEnabledOrAbsent` but Agent 1 replaces that |
| 2 | Hot-reload Apply (config changed, no restart needed) | `VpnEngine.ApplyAsync` → `StartupPipeline.ExecuteAsync` (`HotReload`) → returns JSON → `SingBoxManager.TryReloadConfigJson` → Clash API `PUT /configs` (no LaunchProcess) | N/A — sing-box not relaunched |
| 3 | **Apply with forceRestart** (RoutingMode flip, TUN fingerprint flip, process-list change) | `VpnEngine.ApplyAsync` → `StartupPipeline.ExecuteAsync` (`HotReload`, Phase 6 SKIPPED) → returns JSON → `SingBoxManager.ReloadConfigJson(json, forceRestart=true)` → `Restart()` → `LaunchProcess` | **NEEDS FIX (Agent 1 covers via LaunchProcess swap)** |
| 4 | **Apply with hot-reload failure** (Clash API down, dead sing-box, etc.) | `VpnEngine.ApplyAsync` → `_singBox.ReloadConfigJson(json, false)` → `TryHotReload` returns false → `Restart()` → `LaunchProcess` | **NEEDS FIX (Agent 1 covers via LaunchProcess swap)** |
| 5 | **HealthMonitor crash recovery** | `OnSingBoxCrashed` → `AttemptRestart` → `Task.Delay` → `TryHotReloadViaApi` returns false → `_singBox.Restart()` → `LaunchProcess` | **NEEDS FIX (Agent 1 covers via LaunchProcess swap)** |
| 6 | **HealthMonitor health-tick recovery** (`_shouldBeRunning` path) | `OnHealthTick` → not healthy AND should-be-running → `AttemptRestart` (same path as #5) | **NEEDS FIX (Agent 1 covers via LaunchProcess swap)** |
| 7 | **HealthMonitor debounced process-list change with hot-reload failure** | `OnDebounceElapsed` → `TryHotReloadViaApi` false AND cooldown elapsed → `_singBox.Restart()` → `LaunchProcess` | **NEEDS FIX (Agent 1 covers via LaunchProcess swap)** |
| 8 | AutoFailoverEngine restart-delegate (post-start probe path) | `WireFailoverWithStop` lambda → `_engine.Stop()` → `_engine.StartAsync(...)` → back to surface #1 | YES — re-enters StartAsync (pipeline Phase 6 runs) |
| 9 | AutoFailoverEngine restart-delegate (pre-start F-E probe path) | `WireFailover` lambda → `_engine.StartAsync(...)` without Stop (sing-box not yet up) → surface #1 | YES — re-enters StartAsync (pipeline Phase 6 runs) |
| 10 | Service mode bootstrap | `VPNRouterService.ExecuteAsync` → `ResilientStarter.StartWithBackoffAsync(startFn: ct => _engine.StartAsync(...))` → surface #1 | YES via pipeline Phase 6 |
| 11 | CLI `start` command | `StartCommand.Execute` → `engine.StartAsync` → surface #1 | YES via pipeline Phase 6 |
| 12 | App Connect button / quick-connect | `MainWindowViewModel` → `_engine.StartAsync` → surface #1 | YES via pipeline Phase 6 |
| 13 | Service watcher → file watcher Apply | `_engine.ApplyAsync` → surfaces #2 / #3 / #4 | Depends on path |

**Non-TUN launch paths** (audited separately, all confirmed safe):

* `FreeConfigDeepVerifier.VerifyOneAsync` — spawns a temporary sing-box
  for free-config probe with **SOCKS inbound only, no TUN inbound**
  (see `BuildSingleOutboundConfig` at FreeConfigDeepVerifier.cs:336).
  No wintun adapter created — outside this hotfix's blast radius.
* `VlessDeepVerifier.VerifyOneAsync` — same shape, SOCKS-only spawn
  (VlessDeepVerifier.cs:115-204). No TUN, no conflict.

**Critical finding**: surface #3 (Apply with structural change forcing
restart) bypasses StartupPipeline Phase 6 because the pipeline runs
in `HotReload` mode (Phase 6 only runs in ColdStart / AutoFailover).
The structural-change detection happens AFTER the pipeline returns,
inside `ApplyAsync`, then `_singBox.ReloadConfigJson(json, forceRestart=true)`
funnels into `Restart()` → `LaunchProcess` — so the only chokepoint
for surfaces #3 / #4 / #5 / #6 / #7 is exactly the `LaunchProcess`
call site Agent 1 is replacing. Confirms Agent 1's design choice:
patching `LaunchProcess` is the correct single-chokepoint fix.

### B. Cross-platform leak verdict — GREEN

All Windows-only operations in the affected files are guarded:

* `TunAdapterDiagnostics.LogAdapterState` — `[SupportedOSPlatform("windows")]` + `if (!OperatingSystem.IsWindows()) return;`
* `TunAdapterDiagnostics.DisableOrphanedAdapter` — same dual guard
* `TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent` — same dual guard
* `TunAdapterDiagnostics.PreStartCleanupAsync` — same dual guard
* `TunAdapterDiagnostics.TryRemoveAdapterAsync` — `[SupportedOSPlatform("windows")]`
* `SingBoxManager.LaunchProcess` pre-launch call — `if (OperatingSystem.IsWindows())` block
* `SingBoxManager.StopInternal.early` cleanup — `if (OperatingSystem.IsWindows())` block
* `SingBoxManager.OnProcessExited` cleanup — `if (OperatingSystem.IsWindows())` block
* `VpnEngine.Stop` after-stop diagnostic — `if (OperatingSystem.IsWindows())` block
* `StartupPipeline.PreStartTunCleanupAsync` dispatcher — `if (OperatingSystem.IsWindows())` block

Linux + macOS use kernel-managed TUN (`/dev/net/tun` and `utun*`
respectively). Kernel reaps the device on process exit; no
user-space cleanup needed. Agent 1's patch keeps every netsh /
PowerShell / Remove-NetAdapter call behind the same guards — no
Linux/macOS regression risk.

### C. Service mode + Windows Service recovery — GREEN

`VPNRouter.Service` enters surface #10. The Service's `BackgroundService.ExecuteAsync`:

1. Reads YAML, calls `SubscriptionResolver` to refresh subs.
2. Wraps `_engine.StartAsync` in `ResilientStarter.StartWithBackoffAsync`
   (5/10/20/40s backoff between retries on transient failure).
3. Each retry re-enters surface #1 → full pipeline → Phase 6 cleanup runs again.

Windows Service Control Manager (sc.exe) recovery (`restart 3x/60s`)
is configured in `ServiceInstaller`. When SCM auto-restarts the
Service after a crash, the entire `VPNRouter.Service.exe` process
is re-spawned, which re-enters `ExecuteAsync` from a clean state.
Pipeline Phase 6 runs again — TUN cleanup is guaranteed.

Edge case worth noting (NOT a regression of this hotfix, just for the
record): if both Service AND App are installed and the App owns the
TUN lock, the Service enters watcher mode and never spawns sing-box.
If the App crashes mid-VPN, the App's sing-box hangs in disabled-but-
not-removed state. The user then runs Service install + start while
App is dead — Service's Phase 6 cleanup will remove the App's orphan
adapter (whitelist matches `VPNRouter-TUN` regardless of owner). That
is desired behaviour.

### D. AutoFailoverEngine — shared, not separate

AutoFailoverEngine has NO direct sing-box restart path. It mutates
`AppSettings.Vless.ActiveServer` (or `App.ActiveSubscriptionServer`),
persists via `ISettingsStore.Save`, then invokes a caller-provided
restart delegate (`_restart` ctor parameter). Two delegates are wired
in `VpnEngineStartupHost`:

* `WireFailover` (pre-start path) — `restart` = `_engine.StartAsync(...)` only
* `WireFailoverWithStop` (post-start path) — `restart` = `_engine.Stop() + _engine.StartAsync(...)`

Both delegates re-enter `VpnEngine.StartAsync` → StartupPipeline →
Phase 6 → cleanup runs. **No bypass risk.** The
`[AutoFailover] Restart delegate returned false` log line in user
report just means StartAsync threw (typically `ConflictingVpnException`
or `TunOwnershipException`); the restart was attempted via the
correct chokepoint.

### E. Test regression candidates (after Agent 1's `[Obsolete]` marking)

Existing file `VPNRouter.Tests/TunAdapterReadinessTests.cs` contains
13 tests. **4 will produce CS0618 compiler warnings** but should NOT
fail unless `TreatWarningsAsErrors=true` is set on the test project:

1. `EnsureAdapterEnabledOrAbsent_NonWindows_NoOp` (line 28)
2. `EnsureAdapterEnabledOrAbsent_EmptyInterfaceName_NoOp` (line 40)
3. `EnsureAdapterEnabledOrAbsent_NullInterfaceName_NoOp` (line 49)
4. `EnsureAdapterEnabledOrAbsent_NonExistentAdapter_NoThrow` (line 58)

**Verification needed**: check `VPNRouter.Tests/VPNRouter.Tests.csproj`
for `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` or
`<NoWarn>` of CS0618. If TWAE is on, Agent 1 should either:
* Suppress with `#pragma warning disable CS0618` around the 4 callsites
  (preferred — pins legacy contract while migration is incomplete), or
* Delete the 4 tests outright (cleaner — but loses the non-throw
  pin during the transition).

The other 9 tests in the file pin parser behaviour (`ExtractStaleAdapterNames`,
`PreStartCleanupAsync`, `DisableOrphanedAdapter`) and `DefaultTunInterfaceName`
reflection — all unaffected by the obsolete marking and still pass.

No other test files reference `EnsureAdapterEnabledOrAbsent` —
verified via Grep across `VPNRouter.Tests/`.

### F. Edge cases NOT addressed by Agent 1's current scope

1. **Apply path Phase 6 skip** — surface #3 / #4 above. The fix via
   LaunchProcess covers it transitively (every restart goes through
   LaunchProcess), but the architectural inconsistency remains:
   `StartupPipeline.HotReload` skips Phase 6 even when the caller
   will end up forcing a restart. **Recommendation**: future
   cleanup ticket — add a Phase 6.5 inside `VpnEngine.ApplyAsync`
   that runs `PreStartCleanupAsync` BEFORE `ReloadConfigJson(...,
   forceRestart=true)` is invoked. Belt-and-suspenders; not blocking
   this hotfix.

2. **Sync wrapper on sync-only seam** — Agent 1's
   `LaunchProcess` patch wraps the async `PreStartCleanupAsync` in a
   `.GetAwaiter().GetResult()`. Bounded by the netsh 5s + PowerShell
   10s timeouts inside, so worst case adds ~15s to the launch path.
   On a saturated threadpool this could deadlock — though `LaunchProcess`
   isn't called from an async context (`Start` / `Restart` are sync).
   **Not a defect**, but flag for the review.

3. **`StopInternal` graceful path missed by brief** — the brief
   item 3 only mentions `StopInternal.early` (the post-crash branch
   at line ~234). The "regular" Stop path at lines 245-263
   (`_process.Kill` + `WaitForExit`) currently has NO adapter cleanup
   in the `finally` block. Because `EnableRaisingEvents = false` is
   set first, `OnProcessExited` won't fire — so the adapter is left
   in whatever state sing-box left it (typically the device record
   still exists). Agent 1's commit message ("Stop returns with the
   adapter gone (or going)") implies this is included; confirm
   during integration that the graceful-stop branch also chains
   `TryRemoveAdapterAsync`.

4. **Concurrent in-flight cleanups** — Agent 1's fire-and-forget
   `Task.Run(() => TryRemoveAdapterAsync(...))` in `OnProcessExited`
   and the synchronous `PreStartCleanupAsync` in the next
   `LaunchProcess` can overlap if HealthMonitor's backoff is short
   (5s after a crash). The PowerShell `Remove-NetAdapter` is
   idempotent — second call gets "adapter not found", treated as
   success. Worth a single in-method `Interlocked.CompareExchange`
   gate IF the cleanup races become observable in logs. Not
   required for the hotfix.

5. **No sub-bug fix for `ExtractStaleAdapterNames` parser miss** —
   The brief notes that PreStartCleanup reported "no stale TUN
   adapters found" while DisableOrphanedAdapter (direct-by-name)
   succeeded on the same machine. Agent 1's defence-in-depth approach
   (per commit message: "After enumeration the function now
   unconditionally calls Disable + Remove on the default name unless
   enumeration already handled it") covers this. Good defensive
   choice.

6. **HealthMonitor.AttemptRestart sets `_vpnWasRunning = true`
   prematurely** — pre-existing issue noted in CLAUDE.md "Known
   Issues #6". Not new in this hotfix, but worth flagging because
   in the brief's auto-restart-loop scenario, the
   premature flag could cause spurious VpnStarted events between
   FATAL crashes. Out of scope for this hotfix; future cleanup.

### G. Recommended back-port shape

**Ship target: `v2.35.0-r4` (current rolling candidate)**, NOT
cherry-pick to v2.32.x.

Rationale:
* v2.35.0-r3 is already the active rolling candidate (Phase 6 ship
  cycle). Adding the hotfix here keeps a single in-flight prerelease
  visible per the rolling-rN policy.
* Surface #1 (the user's primary launch path) and surfaces #3-#7
  (the auto-restart loop) are ALL on v2.35.0 via the StartupPipeline
  / SingBoxManager paths described above. The fix lands once and
  protects every entry point.
* v2.32.x is the previous stable. The user (alicemoren1991) is
  presumably on it. Auto-update v2.32.x → v2.35.0 (when v2.35.0 ships
  stable) covers them. Cherry-pick to v2.32.3 would require:
  (a) a separate prerelease cycle, (b) parallel CI for Mac/Linux,
  (c) updated update-check semver gating. Adds 2-3 days of work for
  a user count of N=1.
* **If user pressure escalates** (multiple users reporting same
  symptom), revisit: cherry-pick `SingBoxManager.cs` +
  `TunAdapterDiagnostics.cs` deltas to a `v2.32.3-r1` branch off
  tag `v2.32.2`, ship as hotfix prerelease, promote to stable in
  parallel with the v2.35.x cycle.

Live update gate (per cut-stable skill `plans/cut-stable-checklist.md`):
* Install v2.32.2 in a clean temp dir.
* Trigger auto-update to v2.35.0-r4 (when shipped).
* Verify update.log shows new ProductVersion + smoke launch succeeds.
* Confirm no FATAL "Cannot create a file" in the post-update first VPN cycle.
* If gate FAILS — DO NOT cut stable until reproduced + fixed.

### H. User self-help PowerShell snippet — correct with one caveat

The brief mentions a self-help PowerShell snippet for stuck users
(Stop-Process + Remove-NetAdapter + Restart-Computer). The exact text
isn't quoted, but the correct sequence is:

```powershell
# Run as Administrator
Stop-Service -Name VPNRouter -Force -ErrorAction SilentlyContinue
Stop-Process -Name VPNRouter.App,VPNRouter.Service,VPNRouter.CLI,sing-box -Force -ErrorAction SilentlyContinue
Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false
Get-NetAdapter -Name 'sing-box-tun*' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false
# Optional but recommended: reboot to fully clear wintun driver state
Restart-Computer -Force
```

**Caveats**:
1. **MUST stop the VPNRouter Service first** (`Stop-Service`).
   Service auto-restart (sc.exe failure recovery 3x/60s) will respawn
   sing-box BEFORE the Remove-NetAdapter call can complete, leaving
   the adapter in the same broken state. The brief did not include
   this step.
2. The `sing-box-tun*` wildcard handles cases where the InterfaceName
   wasn't honoured (sing-box fallback naming).
3. Reboot is optional but strongly recommended — Windows wintun
   driver internal state isn't always cleared by Remove-NetAdapter
   alone (kernel-cached device records can survive). Patch Tuesday
   change in May 2026 suggests wintun teardown latency has shifted.
4. After reboot, the user should be able to launch a fixed VPNRouter
   build (v2.35.0-r4 or later) cleanly. If they're still on the
   broken v2.32.2, they will hit the same loop again — need to
   update FIRST, then run this script if needed.

Suggest packaging this as a standalone `repair-tun.cmd` script in
`packaging/windows/` alongside `repair.cmd` (which currently handles
full reinstall but doesn't address the TUN adapter specifically).
Distributable via `vpn.ninitux.com/repair-tun.cmd` for stuck users —
mirrors the v2.31.8-r10 repair.cmd pattern that rescued v2.31.7
users from the helper.cmd CMD-parser bug.

### Summary highest-priority items

For the integrator:

1. Confirm Agent 1's commit covers the **graceful-stop branch** of
   `StopInternal` (lines 245-263), not only `StopInternal.early`.
   See finding F.3 above.
2. Confirm `VPNRouter.Tests` does NOT have `TreatWarningsAsErrors=true`
   on test build — or that Agent 2's commit includes `#pragma warning
   disable CS0618` around the 4 `EnsureAdapterEnabledOrAbsent` test
   call sites. See finding E above.
3. Decide on v2.35.0-r4 vs v2.32.3 cherry-pick — see finding G.
   Recommendation: ship v2.35.0-r4, defer cherry-pick unless user
   pressure escalates.
4. Add **`Stop-Service`** as the first step in any user-facing repair
   snippet — see finding H caveat 1.
