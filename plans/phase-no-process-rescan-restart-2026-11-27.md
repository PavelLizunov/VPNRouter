# Phase: Stop background process rescans from restarting sing-box and holding the kill switch

Date: 2026-11-27
Branch: `fix/no-process-rescan-restart-20261127`
Base: `origin/main`

## Intent

Fix the `v2.51.1-r5` P0 regression where background process-list changes repeatedly restarted `sing-box` and engaged the kill switch mid-session, kicking users out of active realtime sessions (reproduced in VALORANT with `ConnectionTimeout` at 20:44, 20:45:56 -> 20:46:08, and 20:48; `v2.51.1-r4` did not drop the tunnel).

## Root Cause

1. In `v2.51.1-r4`, `HealthMonitor.RescanProcesses()` sent `PUT /configs`, which `sing-box 1.14.4` answered with HTTP 204 as a no-op without stopping the process or arming firewall block rules.
2. In `v2.51.1-r5` (PR #746), `RescanProcesses()` switched to `ApplyConfigWithRestart(configJson)` -> `ApplyConfigWithProtection()`, which arms `_firewall.EnableBlockRules()`, stops and restarts `sing-box` (tearing down `VPNRouter-TUN` and dropping all active TCP/UDP connections, both tunneled and direct), and leaves `_deferredBlockRuleDisable = 1` until the next periodic `OnHealthTick` (up to `HealthCheckInterval` = 30s) even after `ConfigApplyState.Confirmed`.
3. Three additional triggers caused constant background rescans on Windows:
   - `StartupPipeline.StartMonitorsPhase` passes ETW `e.ProcessName` without `.exe` to `HealthMonitor.OnNewProcessDetected`, while Windows `_lastScan.ProcessNames` stores `.exe` suffixes; the already-tracked check always missed.
   - `HealthMonitor.RescanProcesses()` ran and restarted even in routing modes that do not consume dynamic profile scans (`full`, `exclude`, or explicit `RoutingAppsInclude`).
   - `RescanProcesses()` compared exact set equality, so transient child processes (`include_children: true`) exiting also triggered a full restart.

## Scope

- `VPNRouter.Core/Services/HealthMonitor.cs`:
  - Never restart a live, healthy `sing-box` runtime or arm the kill switch from background `RescanProcesses()`. Update `_lastScan` monotonically in memory only when dynamic profile scanning is active so new processes apply on next explicit Apply, reconnect, or recovery.
  - Skip debounce and WMI rescanning entirely when routing mode does not use dynamic profile scans (`full`, `exclude`, or explicit `RoutingAppsInclude`).
  - Match both bare and `.exe`-suffixed process names in `OnNewProcessDetected`.
  - In `ApplyConfigWithProtection`, lift the kill switch and reapply Unix DNS immediately when a legitimate restart (crash recovery or StrictDns failover) reaches `ConfigApplyState.Confirmed`, falling back to `_deferredBlockRuleDisable = 1` only when release fails or the restart is `StartedUnconfirmed`.
- `VPNRouter.Tests/HealthMonitorControlledReloadTests.cs`:
  - Add regression tests for zero-restart process rescans, monotonic retention on child exit, mode gating (`full` / `exclude` / explicit `RoutingAppsInclude`), Windows `.exe` suffix matching in `OnNewProcessDetected`, and immediate kill-switch release on `Confirmed` restarts while keeping all `NotStarted` and `StartedUnconfirmed` fail-closed tests.
- `plans/OPEN-DEFECTS.md`: record the `v2.51.1-r5` P0 regression and its resolution in source.

## Invariants

- Explicit user Apply (`VpnEngine.ApplyAsync`), crash recovery (`RunScheduledRestart`), and StrictDns failover (`ReconcileStrictDnsFailover`) keep restart-only verified apply and fail-closed kill-switch semantics on `NotStarted` and `StartedUnconfirmed`.
- Process-name casing is preserved and deduplicated with `StringComparer.OrdinalIgnoreCase`.
- No local builds or SDK installs on this workstation; verification runs in GitHub CI on the PR head SHA.

## Verification

- GitHub CI on PR against `main` (Windows/macOS/Linux test suites and Android compile checks).
- Manual owner/installed verification: play a 10+ minute VALORANT match with VPN enabled and verify no unexpected `sing-box` restarts or `ConnectionTimeout` kicks.

## Observed Outcomes

- Pending PR creation and GitHub CI run.
