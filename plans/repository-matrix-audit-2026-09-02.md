# VPNRouter Full Repository Matrix & Swarm Audit

**Date**: 2026-09-02
**Harness Session**: `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Baseline**: `origin/main` (`e58ac75d`), ~198k LOC across 676 files in 7 projects + tools and workflows.
**Audit Methodology**: Two independent swarm iterations (`gemini-swarm` with `gemini-3.8-flash-high` and `opus-swarm` with `claude-opus-4-6-thinking`) per category/subcategory, followed by lead source-verification.

---

## 1. Complete Repository Matrix

| # | Category | Core Subsystems | Files | LOC | Audit Status |
|---|---|---|---:|---:|:---:|
| **1** | **Core Engine & Lifecycle** | `VpnEngine`, `StartupPipeline`, `SingBoxManager`, `HealthMonitor`, `AutoFailoverEngine`, `ConfigGenerator`, `CustomConfigInjector` | 139 | 49,989 | **Complete** (PR #215 split + 2 iterations) |
| **2** | **Protocols, Subscriptions, Driver & Anti-DPI** | `ServerUriParser`, `VlessServersResolver`, `SubscriptionFetcher`, `FreeConfigs`, `SplitTunnelDriver`, `EtwProcessMonitor`, `ZapretManager`, `SlipstreamManager` | 55 | 10,193 | **Complete** (2 iterations) |
| **3** | **Platform Hardening & Storage** | `FirewallManager`, `WindowsDnsHardening`, Unix platform managers, `SettingsLoader`, `UpdateChecker`, `VPNRouterService` | 27 | 7,419 | **Complete** (2 iterations) |
| **4** | **CLI & IPC** | `VPNRouter.CLI` commands, `StateFile`, `ProcessOwnership`, `TunOwnershipLock` | 13 | 1,702 | **Complete** (PR #214 + 2 iterations) |
| **5** | **Desktop GUI (Avalonia)** | `MainWindowViewModel` (7.3k LOC), `FreeConfigsPageViewModel`, Views, Styles | 76 | 31,199 | **Complete** (2 iterations) |
| **6** | **Android Client** | `VpnRouterService.java`, `AndroidApp.axaml.cs`, `AndroidStorage.cs`, `MainActivity.cs` | 42 | 26,109 | **Complete** (2 iterations) |
| **7** | **Test Suite & Tooling** | `VPNRouter.Tests`, `tools/brat-verify.ps1`, GitHub Actions workflows | 348 | 78,242 | **Complete** (2 iterations) |

---

## 2. Master Defect Inventory (Source-Verified)

### Category 1: Core Engine & Lifecycle
- **`VENG-01` (P1 / High)**: `VpnEngine.cs:320-322` — `StartAsync` decouples caller `ct` from `_sessionCts`. Concurrent `Stop()` blocks synchronously on `_lifecycleGate.Wait()` while all 8 startup phases execute to completion.
- **`VENG-02` (P1 / High)**: `VpnEngine.cs:322, 518`, `StartupPipeline.cs:1170` — `StartAsync` has no `catch` block on `StartAsyncInternal` failure. Leaks `_singBox`, `_slipstream`, and leaves `VPNRouter-Block-*` firewall rules orphaned in Windows Firewall.
- **`SEC-1.2-01` (P1 / High)**: `SingBoxManager.CrashDetect.cs:33-38` — `OnProcessExited` ignores event arguments and re-reads instance field `_handle` (nulled by `StopInternal`), causing suppression failure and false `Crashed` events.
- **`SEC-1.2-02` (P1 / High)**: `SingBoxManager.Lifecycle.cs:484-518` — `Restart()` fallback after rejected hot-reload has no `catch` block. If `LaunchProcess` throws, `_tunLock` remains permanently held and `State` is stuck in `Restarting`.
- **`SEC-1.3-01` (P1 / High)**: `HealthMonitor.cs:988-992` — Premature `_restartAttempts = 0` in restart continuation resets attempt counter before health is proven, creating infinite 5s crash loops and starving `FailoverRequested`.
- **`SEC-1.3-04` (P1 / High)**: `AutoFailoverEngine.cs:183, 219, 363` — On failed restart (`committed = false`), candidate is omitted from `_tried`, causing `AutoFailoverEngine` to infinitely retry the same broken server.
- **`SEC-01` (P1 / High)**: `CustomConfigInjector.cs:148, 198` — Full-tunnel mode skips `InjectRouteRules`; pre-existing user direct rules match before `route.final = proxy`, causing uninspected traffic leaks.
- **`SEC-02` (P1 / High)**: `CustomConfigInjector.cs:1641-1649` — Injector does not ensure `action: "hijack-dns"` exists in `route.rules`. Missing hijack rule leaks port 53 traffic to the local gateway/ISP.

### Category 2: Protocols, Subscriptions, Driver & Anti-DPI
- **`ETW-01` (P1 / High)**: `EtwProcessMonitor.cs:38, 53, 84` — `_sessionReady` (`ManualResetEventSlim`) lacks `.Reset()`. Second start/stop cycle deadlocks, stranding the worker thread in `Source.Process()`.
- **`SEC-01` (P1 / High)**: `NaivePairing.cs:89-94` — Fallback Step 3 routes all UDP traffic to any alive server in the global pool, splitting TCP and UDP across different countries/IPs.
- **`EVA-06` & `EVA-07` (P1 / High)**: `RuleSetCacheManager.cs:108, 140` — Unsynchronized writes to static `.tmp` path collide with `IOException`; `Length > 0` validation caches captive portal HTML as binary `.srs` for 7 days, crashing sing-box.
- **`FCP-01` & `FCP-02` (P1 / High)**: `FreeConfigTester.cs:97`, `DeepVerifyProbe.cs:115` — Direct unproxied TCP/DNS probing leaks host IP/DNS; proxies reflecting the host's public IP are marked `Verified`.

### Category 3: Platform Hardening & Storage
- **`SEC-01` (Critical / LPE)**: `AppPaths.cs:243-245`, `VPNRouterService.cs:200` — `TryRestrictWindowsDataDirAcl` grants non-admin users `Modify` rights on `%ProgramData%\VPNRouter\bin`, allowing binary replacement executed by `VPNRouterService` as `NT AUTHORITY\SYSTEM`.
- **`SEC-02` (Critical / Command Injection)**: `VPNRouterService.cs:303-324`, `ZapretManager.cs:296` — Unsanitized `ZapretCustomArgs` from `config.yaml` is interpolated into `.bat` executed under `LocalSystem`.
- **`FW-02` (P1 / High)**: `LinuxFirewallManager.cs:126`, `MacFirewallManager.cs:141` — Rules written to static `/tmp` paths vulnerable to symlink hijacking before execution by root.
- **`FW-03` (P1 / High)**: `LinuxFirewallManager.cs:135`, `MacFirewallManager.cs:178` — Non-zero exit from `sudo nft` / `pfctl` is swallowed, leaving the engine unaware that killswitch failed to arm.
- **`FW-04` (P1 / High)**: `MacDnsHardening.cs:76-77` — Primary service DNS set to `172.19.0.1` permanently bricks Mac DNS upon abrupt termination.

### Category 5: Desktop GUI (VPNRouter.App)
- **`FIND-01` (P1 / High)**: `MainWindowViewModel.SimpleMode.cs:437-566` — `SmpToggleConnectAsync` lacks `IsConnecting = true` pre-probe, allowing rapid button clicks to launch duplicate health probes and concurrent settings saves.
- **`FIND-02` (P1 / High)**: `MainWindowViewModel.Connection.cs:102-111` — Pre-start cleanup `_engine.Stop()` queues `"Stopped"` event that unsets `IsConnecting = false` during tunnel startup, allowing duplicate starts.
- **`FIND-03` (P1 / High)**: `MainWindowViewModel.FreeConfigs.cs:121` — `SelectedServer = target` synchronously fires `ReconnectAsync` concurrently with `ApplyFreeConfigAsync`.
- **`F-01` (P1 / High)**: `FreeConfigsPageViewModel.cs:883-900` — Deep verify replaces entire `ObservableCollection`s on every verified candidate, freezing the UI thread.
- **`F-02` (P1 / High)**: `MainWindowViewModel.Profiles.cs:198-251` — `default.json` deserialization failure marks `_appsLoaded = true`, causing subsequent `SaveSettings()` to permanently wipe user custom group apps on disk.

### Category 6: Android Client (VPNRouter.Android)
- **`AND-V61-01` (P1 / High)**: `VpnRouterService.java:681-691` — Startup failure after `openTun()` leaves `currentPfd` open, leaking kernel TUN file descriptors and wedging Always-on recovery.
- **`AND-CRASH-01` (P1 / High)**: `VpnRouterService.java:1485, 1496` — Mixing `addAllowedApplication` and `addDisallowedApplication` crashes `VpnService.Builder` with `UnsupportedOperationException`.
- **`AND-PERF-01` (P1 / High)**: `MainActivity.cs:750, 782` — Synchronous SAF file I/O inside `OnActivityResult` on the main Looper thread blocks >5s, triggering Android ANR.

### Category 7: Test Suite & Tooling
- **`TST-03` (P1 / High)**: `xunit.runner.json:4-5` — Parallelization disabled due to static state mutation (`AppPaths._dataDir`, `SingBoxFeatures.OverrideAwg`), forcing 4-8x slower sequential test execution.
- **`FIND-01` (P1 / High)**: `verify-release-integrity.yml:229-236, 441` — Missing expected release assets append to `WARNINGS` rather than `ERRORS`, allowing incomplete releases to remain published.
- **`FIND-02` (P1 / High)**: `tools/post-ship-verify.ps1:103-145` — Checksum verification only checks the 2 Windows ZIPs, completely skipping the other 12 platform assets.

---

## 3. Implementation Roadmap & Task Branches

1. **Packet 1 (`dsh/fix-vpnengine-lifecycle`)**:
   - `VENG-01`: Link caller `ct` with `_sessionCts` via `CreateLinkedTokenSource(ct)`.
   - `VENG-02`: Add `catch (Exception ex) when (!IsRunning) { TeardownInternal(); throw; }` in `StartAsync`.
   - Unit tests covering rapid cancellation and teardown cleanup on startup failure.
2. **Packet 2 (`dsh/fix-singbox-and-failover`)**:
   - `SEC-1.2-01`: Pass `exitCode` to `OnProcessExited(code)`.
   - `SEC-1.2-02`: Add `catch` block to `Restart()` with `_tunLock.Release()`.
   - `SEC-1.3-01`: Remove premature `_restartAttempts = 0` in `HealthMonitor`.
   - `SEC-1.3-04`: Add failed candidate to `_tried` in `AutoFailoverEngine`.
3. **Packet 3 (`dsh/fix-custom-config-injector`)**:
   - `SEC-01`: Strip non-private direct rules in Full Tunnel mode.
   - `SEC-02`: Ensure `action: "hijack-dns"` exists in custom route rules.
4. **Packet 4 (`dsh/fix-etw-and-naive-pairing`)**:
   - `ETW-01`: Add `_sessionReady.Reset()` in `EtwProcessMonitor.Start()`.
   - `SEC-01`: Remove global fallback in `NaivePairing` Step 3.
5. **Packet 5 (`dsh/fix-ruleset-cache-and-lpe`)**:
   - `EVA-06` & `EVA-07`: Add per-file `SemaphoreSlim`, unique temp paths, and non-HTML validation in `RuleSetCacheManager`.
   - `SEC-01`: Restrict `BinDir` ACL to `ReadAndExecute` in `AppPaths.cs`.
6. **Packet 6 (`dsh/fix-desktop-gui-races`)**:
   - `FIND-01`: Set `IsConnecting = true` before probe in `SimpleMode`.
   - `FIND-02`: Guard against `IsConnecting` in `OnEngineStatus("Stopped")`.
   - `F-02`: Guard `_appsLoaded` and `_settings.CustomGroupApps` against missing profiles.
