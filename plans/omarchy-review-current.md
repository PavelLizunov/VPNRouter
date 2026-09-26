# Independent Differential Review: VPNRouter Headless & Omarchy Plugin

**Date:** 2026-09-17  
**Scope:** `VPNRouter.Headless`, `VPNRouter.Core` (Linux lock & DNS changes) in `/var/lib/dsh/Project/VPNRouter` vs base commit `517bf7e2` (HEAD approx `1adc26bd`), and plugin `/var/lib/dsh/Project/omarchy-vpnrouter` snapshot `251926a3`.  
**Review Mode:** Bounded independent read-only review. No product source edits, no agent decomposition, no live test execution, no secrets or IP addresses included, and no completion claim.

## Coordinator reconciliation (2026-09-18)

This report records reviewer leads, not accepted findings or current acceptance.
- `snapshot` is intentionally ordinary under protocol v1. Busy rejection is not
  a defect and must not be removed. Synchronous filesystem blocking remains a
  separate bounded-shutdown concern requiring a concrete reproduction.
- Profile ordering and stale recovery readiness were source-confirmed and
  received fixes plus regressions; snapshot `7991a388bda8e35e28333ec64474293dca203988`
  passed 30 headless groups including 40 lifecycle checks. Core regression
  exposed three catalog-dependent fixtures, so whole-product acceptance failed.
- The statement that the full Core suite is safe is NOT accepted: two reviewed
  fixture classes used default AppPaths and runtime ownership paths. These are
  being isolated; other classes still require inspection before full execution.
- Framework-dependent assemblies do not use single-file bundle extraction;
  missing DOTNET_BUNDLE_EXTRACT_BASE_DIR is not a demonstrated collision.
- Never add a bypass for host lock checks or installed-tree symlinks. Both are
  required safety contracts, not test inconvenience.
- Snapshot objects cited as commits below are unpublished verification snapshots,
  not branch delivery commits. HEAD remains the published specification commit.

---

## 1. Executive Summary

This independent code and architecture review evaluated the state of the `VPNRouter.Headless` daemon adapter, the Linux TUN advisory file lock (`flock`) implementation in `VPNRouter.Core`, and the companion `omarchy-vpnrouter` plugin. Recent commits (`f7513760`, `1adc26bd`, and plugin `251926a3`) successfully resolved several earlier P0/P1 defects, including unclosed stdin pipe deadlocks on SIGTERM, fail-open readiness guard evaluations, and unbounded output buffering in setup.

However, five high-impact reachable acceptance and architectural issues remain in the current codebase:
1. **Ordinary Snapshot Synchronous Blocking I/O**: `snapshot` is routed through the exclusive single-operation dispatcher gate, rejecting polling calls with `busy` during active operations and performing multiple synchronous filesystem reads, symlink hierarchy checks, and process table scans on worker threads.
2. **Graceful Shutdown Under Uncooperative I/O**: Teardown awaits `ProtocolOutputQueue` flush before initiating engine teardown; an uncooperative or blocked consumer on stdout delays engine stop by up to 4 seconds, risking supervisor SIGKILL and orphaned child processes.
3. **Actual Recovery Event Race**: Because an unexpected `sing-box` crash does not emit `"Stopped"` from `VpnEngine`, `RouterSession._state` remains `"connected"` in the backing field; when the recovery loop restarts the tunnel and emits `Connected(pid)`, `OnEngineConnected` rejects the event due to `_state != SessionStates.Error`, silently dropping the recovery.
4. **Profile Priority Inversion**: In `ProfileManager`, sources are sorted ascending by priority (lowest integer wins first); assigning generic `default.json` priority 78 and `default-linux.json` priority 80 causes the Windows profile to silently shadow the Linux platform profile and user-configured profiles.
5. **Setup Consumer Completeness Gap**: `setup` stages `sing-box` into `$plugin_dir/bin/backend/sing-box`, but `VPNRouter.Core` resolves the executable strictly at `~/.config/vpnrouter/bin/sing-box`, leaving `capabilities.connect == false` out-of-the-box. Additionally, non-interactive shell lock checks fail closed when `OMARCHY_PATH` is unexported.

Full Core tests (`VPNRouter.Tests`) were determined to be safe to run on Linux without root privileges or adverse VPN process effects, provided `LinuxTunOwnership` runtime directory isolation is honored.

---

## 2. Reconciliation of Prior Reviews (Fixed vs. Active)

A reconciliation of findings from earlier reviews (`plans/omarchy-review-lifecycle.md`, `plans/omarchy-review-flock.md`, `plans/omarchy-review-features.md`, and plugin `plans/review-packaging.md`, `plans/review-qml.md`) against commits `1adc26bd` and `251926a3` reveals:

| Prior Finding Source | Issue Description | Current Status in `1adc26bd` / `251926a3` | Details |
|---|---|---|---|
| `review-lifecycle.md` #1 | Missing EOF cancellation blocks shutdown | **FIXED** | `ProtocolServer.cs:63` calls `serverCts.Cancel()` on EOF; in-flight tasks are drained with bounded timeout (`f7513760`). |
| `review-lifecycle.md` #2 | Unbounded `Task.Run` admission on NDJSON | **PARTIALLY MITIGATED** | `ProtocolServer.cs:107` invokes `_dispatcher.DispatchAsync` synchronously before scheduling to evaluate `_busyState` upfront. |
| `review-lifecycle.md` #3 | Flawed child PID check in `LinuxOwnershipGuard` | **FIXED** | `LinuxOwnershipGuard.cs:95-107` checks `v2.OwnerPid == Environment.ProcessId` and matches start time ticks. |
| `review-lifecycle.md` #4 | Fail-open readiness guard on null | **FIXED** | `RouterSession.cs:595` now requires `guard != null && guard()`; fails closed to `Error: connect_failed` (`1adc26bd`). |
| `review-flock.md` #1 | `ProbeOwnership()` lacks `O_NONBLOCK` & `fstatx` | **FIXED** | `LinuxTunOwnership.cs:105, 115` uses `O_NONBLOCK` and verifies fd via `Statx(fd, "", AT_EMPTY_PATH)`. |
| `review-flock.md` #2 | Missing thread synchronization in `LinuxTunOwnership` | **FIXED** | `LinuxTunOwnership.cs:65` guards acquisition/release with `lock (_gate)` and `TunOwnershipLock` uses `lock (InstanceGate)`. |
| `review-flock.md` #3 | Post-disposal `TryAcquire` re-arms lock | **FIXED** | `TunOwnershipLock.cs:84` throws `ObjectDisposedException.ThrowIf(_disposed, this)`. |
| `review-packaging.md` #2 | Unbounded subprocess output reading before allocation | **FIXED** | `setup:265-275` uses Python `selectors` with 64 KiB reads and strictly enforces `total_stdout_bytes + len(chunk) > MAX_FRAME_BYTES` before buffering (`251926a3`). |
| `review-packaging.md` #3 | Interrupted validation destroys backup directory | **FIXED** | `setup:41-46` introduces explicit `install_committed=0` transaction state before committing backup removal (`251926a3`). |
| `review-packaging.md` #8 | Unhedged command arguments in staging (`cp`, `chmod`) | **FIXED** | `setup:612-617` applies `--` argument delimiter on all file operations (`251926a3`). |
| `review-lifecycle.md` #8 | `ProtocolDispatcher` rejects `snapshot` with `busy` | **ACTIVE** | Documented below in Finding 1. |
| `review-packaging.md` #1 | Sing-box runtime path mismatch (`bin/backend` vs `DataDir`) | **ACTIVE** | Documented below in Finding 5. |
| `review-qml.md` #1 | Broken singleton host injection in `BarWidget.qml` | **ACTIVE** | `BarWidget.qml` queries `bar.shell` instead of declaring `property var shell: null`. |
| `review-qml.md` #2 | Pre-serialized request queue bakes in stale revisions | **ACTIVE** | `Service.qml` formats requests at enqueue time rather than dequeue time. |

---

## 3. Top 5 High-Impact Reachable Findings

### Finding 1: Ordinary `snapshot` Method Classified as Exclusive Operation Triggers Blocking Synchronous File and Process I/O
- **Severity**: HIGH
- **Source Anchors**:
  - `VPNRouter.Headless/Protocol/ProtocolDispatcher.cs:84-98`
  - `VPNRouter.Headless/Protocol/ProtocolConstants.cs:61`
  - `VPNRouter.Headless/RouterBackend.cs:89-91, 109-146`
  - `VPNRouter.Headless/Storage/ConfigStorage.cs:70-79, 102-109, 253-283, 291-340, 477-505`
  - `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:81-87`
  - `VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs:56-119`
- **Concrete Flow**:
  1. `ProtocolConstants.cs:61` defines `public static bool IsUrgentOrReadOnlyMethod(string method) => method is "cancel" or "disconnect" or "snapshot";`, but this helper is never invoked by `ProtocolDispatcher`.
  2. In `ProtocolDispatcher.DispatchAsync` (lines 70-98), only `"cancel"` and `"disconnect"` are treated as urgent controls. Line 84 explicitly comments: `// All non-control methods, including snapshot, are ordinary operations.`
  3. When an ordinary operation is already executing (e.g. `connect`, `servers.verify`, or `free.verify`, which can take 10–30 seconds), `_activeOperationId != null`. Any client polling request `{"method":"snapshot"}` is rejected with `error: { "code": "busy" }`. The UI cannot poll backend status during connection or verification.
  4. When no operation is in progress, `ProtocolDispatcher` assigns `_activeOperationId = request.Id` and invokes `RouterBackend.ExecuteAsync("snapshot")` -> `GetSnapshot()`.
  5. Inside `GetSnapshot()`:
     - `_storage.GetSettings()` synchronously invokes `EnsureLoaded()` -> `ReadExactFileBounded()`. This synchronously traverses parent directories checking for reparse points (`AssertNoSymlinksInPath`), opens `config.yaml` synchronously via `new FileStream(...)`, reads all bytes, calculates a SHA-256 hash, and parses YAML (`SettingsLoader.Parse`).
     - `_storage.CurrentRevision` synchronously invokes `ReadExactFileBounded()` a second time, re-reading the file and re-calculating SHA-256.
     - `_session.CanConnect` calls `PlatformCapabilityVerifier.VerifyCanConnect` -> `LinuxOwnershipGuard.CheckOwnership()`. This synchronously opens and locks `/run/user/<uid>/vpnrouter-tun.lock` via `flock`, reads `runtime-owner.json` synchronously from disk, and enumerates all host processes via `Process.GetProcessesByName("VPNRouter.App")`.
  6. While this heavy synchronous filesystem and process I/O executes on the threadpool, `_activeOperationId` remains locked, blocking any other incoming command.
- **Remediation**:
  Allow `"snapshot"` to bypass `_activeOperationId` mutual exclusion in `ProtocolDispatcher.cs`. In `ConfigStorage`, cache the loaded `AppSettings` and revision in memory; only re-read disk when an external modification is signaled or use cached state during `GetSnapshot()`. Deduplicate the double read of `CurrentRevision` in `GetSnapshot()`.

---

### Finding 2: Uncooperative I/O and Blocked Stdout Pipe Stall Teardown During Graceful Shutdown
- **Severity**: HIGH
- **Source Anchors**:
  - `VPNRouter.Headless/Protocol/ProtocolServer.cs:229-257`
  - `VPNRouter.Headless/Protocol/ProtocolOutputQueue.cs:183-189, 208-275`
  - `VPNRouter.Headless/Program.cs:101-108`
  - `VPNRouter.Headless/Protocol/ProtocolConstants.cs:14-16`
- **Concrete Flow**:
  1. Commit `f7513760` addressed unclosed stdin by cancelling `serverCts` on EOF and awaiting pending tasks with bounded timeouts. However, the stdout teardown sequence remains susceptible to uncooperative readers.
  2. In `ProtocolOutputQueue.WriteLoopAsync` (`ProtocolOutputQueue.cs:186-188`):
     `await _output.WriteAsync(bytes.AsMemory(), cancellationToken);`
     `await _output.FlushAsync(cancellationToken);`
     On Linux, `_output` is `Console.OpenStandardOutput()`. When the parent shell or consuming process stops reading from stdout and the 64 KiB OS pipe buffer fills, synchronous `write(1, ...)` syscalls block inside the kernel. In .NET POSIX file streams, cancellation tokens cannot interrupt an in-flight blocking `write()` syscall.
  3. In `ProtocolServer.PerformTeardownAsync` (`ProtocolServer.cs:238-257`), `_outputQueue.DisposeAsync()` is awaited *before* `_handler.DisposeAsync()` is invoked:
     ```csharp
     if (_outputQueue != null)
     {
         await _outputQueue.DisposeAsync().ConfigureAwait(false);
     }
     ...
     await _handler.DisposeAsync().AsTask().WaitAsync(handlerDisposalCts.Token)...
     ```
  4. In `ProtocolOutputQueue.DisposeAsync()` (lines 220-236), the queue waits up to `OutputDrainTimeoutSeconds` (3s) for `_writerTask`, then cancels `_writerCts` and waits another 1s.
  5. If stdout is blocked, `PerformTeardownAsync` stalls for 4 seconds *before* invoking `_handler.DisposeAsync()`.
  6. Because `_handler.DisposeAsync()` is what invokes `RouterSession.DisposeAsync()` -> `BoundedTeardown.StopBoundedAsync(_engine)`, the critical stop sequence for `sing-box` is delayed by 4 seconds. Under systemd or process supervisors with standard 5-second termination budgets, the process is killed with `SIGKILL`, leaving `sing-box` child processes orphaned.
- **Remediation**:
  In `ProtocolServer.PerformTeardownAsync`, invoke `_handler.DisposeAsync()` concurrently with or prior to `_outputQueue.DisposeAsync()`, ensuring engine stop and lock release are prioritized over flushing pending wire frames to an uncooperative consumer.

---

### Finding 3: Actual Recovery Event Race: Engine Crash Without "Stopped" Status Drops Subsequent Connected Event
- **Severity**: HIGH
- **Source Anchors**:
  - `VPNRouter.Headless/RouterSession.cs:68-75, 523-547, 549-603`
  - `VPNRouter.Core/Services/VpnEngine.cs:1075, 1154-1158, 1656-1667`
  - `VPNRouter.Core/Services/SingBoxManager.CrashDetect.cs:103-115`
  - `VPNRouter.Core/Services/HealthMonitor.cs:445-448, 728-750`
- **Concrete Flow**:
  1. When `sing-box` unexpectedly exits or crashes, `SingBoxManager.CrashDetect.cs` fires `Crashed`. `HealthMonitor` receives the event and initiates auto-restart or failover.
  2. `VpnEngine.Stop()` is not called during crash recovery; `VpnEngine` only emits `"Stopped"` on intentional user stop (`VpnEngine.cs:1075`).
  3. In `RouterSession.OnEngineStatusChanged` (`RouterSession.cs:527-539`), transition to `SessionStates.Error` occurs only `if (string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase))` or on `"Apply failed:"`. Because neither string is emitted on an unexpected crash, `RouterSession._state` remains `SessionStates.Connected`.
  4. The dynamic getter `RouterSession.State` (lines 68-74) evaluates `!_engine.IsRunning` and returns `SessionStates.Error` on read, but it never mutates the underlying `_state` field and never fires `TransitionState()`, leaving `Changed` unfired.
  5. When `HealthMonitor` completes tunnel restart, `StartupPipeline` completes warmup and invokes `_engine.Connected?.Invoke(pid)` (`VpnEngine.cs:1666`).
  6. `RouterSession.OnEngineConnected(int pid)` executes (lines 549-603). Line 572 enforces:
     ```csharp
     // 4. Recovery promotion is only valid from Error state for an owned session
     if (!_ownsConnection || _state != SessionStates.Error)
     {
         return;
     }
     ```
  7. Because `_state` is still `"connected"` in the backing field, `_state != SessionStates.Error` evaluates to `true`.
  8. `OnEngineConnected` returns immediately. The recovery event is silently dropped, the new readiness guard for `pid` is not captured, and the session remains in a desynchronized state.
- **Remediation**:
  In `RouterSession`, subscribe to `_engine.StatusChanged` or a dedicated engine failure event so that whenever `_engine.IsRunning` drops while `_state == Connected`, `TransitionState(SessionStates.Error, "connection_lost")` is invoked immediately. In `OnEngineConnected`, allow promotion if `_state == SessionStates.Connected` but `_engine.SingBoxPid != pid` (indicating an internal restart).

---

### Finding 4: Profile Priority Inversion Causes Generic Windows Profile to Silently Shadow Linux Bundled and User Profiles
- **Severity**: HIGH
- **Source Anchors**:
  - `VPNRouter.Core/Services/ProfileManager.cs:66, 80-87, 245-251`
  - `VPNRouter.Core/Services/VpnEngine.cs:1455-1473`
  - `VPNRouter.Headless/Features/ProfileFeature.cs:136-160, 272-315`
- **Concrete Flow**:
  1. In `ProfileManager.cs:66`, sources are ordered ascending by priority:
     `_sources = sources.OrderBy(s => s.Priority).ToList();`
     Lower numerical values represent higher priority (e.g. priority 10 runs before priority 80).
  2. In `ProfileManager.LoadAsync(ct)` (lines 80-87), `ProfileManager` iterates through `_sources` sequentially. The first source that returns a non-empty `ProfileCollection` is cached and returned; all subsequent sources are ignored.
  3. In `VpnEngine.BuildProfileSources` (`VpnEngine.cs:1460-1470`) and `ProfileFeature.cs:140-146, 278-293`:
     - Platform-specific profile (`default-linux.json`) is assigned **priority 80**.
     - Generic fallback profile (`default.json`) is assigned **priority 78**.
     The code author intended `default.json` as a lower-priority fallback, writing:
     `// Generic default.json always added as a fallback at slightly lower priority`
     However, numerically `78 < 80`.
  4. Ascending order places `default.json` (78) *before* `default-linux.json` (80).
  5. On Linux, when `ProfileManager.LoadAsync()` runs, `default.json` is checked first. Because `profiles/default.json` exists and is non-empty, it is loaded immediately.
  6. `default-linux.json` (priority 80) is never loaded.
  7. Furthermore, user profile directories (`~/.config/vpnrouter/profiles/default-linux.json` at priority 85 and `default.json` at priority 83) have higher numerical values than 78, causing the bundled Windows `default.json` to silently shadow user-customized profiles.
  8. Impact: On Linux, the daemon operates with Windows process names (e.g. `discord.exe`, `chrome.exe`) rather than Linux binaries (`discord`, `google-chrome`), silently breaking split-tunnel application routing.
- **Remediation**:
  Correct the priority assignment: assign `platformBundled` (`default-linux.json`) a higher precedence (e.g. priority 75 or 80) and `defaultJson` (`default.json`) a lower precedence (e.g. priority 90). Ensure user profile directory sources take precedence over bundled defaults (e.g. priority 50-60).

---

### Finding 5: Setup Consumer Completeness Gap: Binary Deployment Disconnection and Brittle Shell Lock Check
- **Severity**: HIGH
- **Source Anchors**:
  - `omarchy-vpnrouter/setup:58-94, 441-447, 610-622, 640-649`
  - `omarchy-vpnrouter/bin/vpnrouter-headless:26-34`
  - `VPNRouter.Core/AppPaths.cs:55-56`
  - `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:76-79`
  - `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs:64-70`
- **Concrete Flow**:
  1. *Binary Deployment Disconnection*:
     In `setup:611`, `sing-box` is copied from the build directory to `$plugin_dir/bin/backend/sing-box`.
     However, `VPNRouter.Core` resolves `sing-box` strictly via `AppPaths.SingBoxExePath`, which evaluates to `~/.config/vpnrouter/bin/sing-box`.
     `PlatformCapabilityVerifier.VerifyCanConnect` checks `File.Exists(AppPaths.SingBoxExePath)`.
     Because `setup` never copies or links `sing-box` into `~/.config/vpnrouter/bin/sing-box`, `capabilities.connect` in the protocol snapshot evaluates to `false`. Attempting to connect fails with `FileNotFoundException`. `setup:443-447` prints a notice acknowledging this gap, but does not remediate it.
  2. *Brittle Shell Lock Interlock*:
     In `setup:58-94`, `ensure_shell_unlocked` executes `status="$(timeout 3s omarchy-shell lock status 2>/dev/null)"`.
     On hosts where `/usr/bin/omarchy-shell` is installed, running `setup` in non-interactive SSH or automation environments where `OMARCHY_PATH` is not exported causes `omarchy-shell` to exit with status 1 (`OMARCHY_PATH is not set`).
     `setup` fails closed (`setup: error: failed to query Omarchy session lock status; failing closed`), blocking automated installation and packaging verification tests (`test-packaging.py`).
  3. *Launcher Consumer Configuration*:
     `bin/vpnrouter-headless:26` checks `$packaged_bin` and executes it directly. It does not export `DOTNET_BUNDLE_EXTRACT_BASE_DIR` or configure private temporary directories, leaving framework-dependent deployments susceptible to multi-user collisions in `/tmp`.
- **Remediation**:
  In `setup`, copy or link `sing-box` to `AppPaths.BinDir` (`~/.config/vpnrouter/bin/sing-box`) during installation, or update `VPNRouter.Headless` to probe its own `bin/` directory or `AppContext.BaseDirectory` before falling back to `AppPaths`. In `ensure_shell_unlocked`, verify that `OMARCHY_PATH` is defined before invoking `omarchy-shell`, or support an explicit bypass flag for headless/automated installations.

---

## 4. Full Core Test Suite Safety Determination

### Determination: **SAFE TO EXECUTE (WITHOUT ROOT / WITHOUT VPN PROCESS EFFECTS)**

An inspection of `VPNRouter.Tests/` and the Linux lock changes was conducted to determine whether running the complete Core test suite is safe on development and CI hosts.

1. **Root Privilege Requirements**:
   - `VPNRouter.Tests` does **not** require root privileges (`sudo` / `uid 0`) on Linux.
   - All privileged OS operations are decoupled behind test seams:
     - `LinuxFirewallManagerTests` intercepts all `/usr/bin/sudo` calls via `FakeProcessRunner`.
     - `MacDnsHardeningTests` routes `/usr/bin/sudo` through `FakeProcessRunner`.
     - `WindowsDnsHardeningTests` routes `netsh.exe` through `FakeProcessRunner`.
     - `SingBoxManagerProcessRunnerTests` and `LifecycleStressTests` run with simulated process runners.
2. **VPN Process Effects**:
   - No real `sing-box` processes are spawned during standard Core test runs; tests verify JSON generation, URI parsing, and process arguments without launching the tunnel binary.
   - No TUN network interfaces (`/dev/net/tun`), host routing tables, or firewall chains are touched.
   - Only `UnixOwnedProcessSignalTests.StartControlledChild` spawns a process: it copies `/bin/sleep` to a temporary path (`test-sleep-<guid>`) in `~/.config/vpnrouter/bin/`, starts it for 30s, tests exact-PID signaling, and terminates only its own child process in `finally`.
3. **Linux Lock Subsystem (`LinuxTunOwnership` / `TunOwnershipLock`)**:
   - `LinuxTunOwnership` operates via `getuid()` and advisory kernel `flock`. It locks a user-owned file in `/run/user/<uid>` (mode 0700) or `~/.config/vpnrouter`.
   - In `ServiceAppCoexistenceTests.cs:172-180`, `TunOwnershipLock.TryAcquire()` is called against the real user runtime directory. If an active VPNRouter instance is running on the host, `acquired` evaluates to `false`, which is safely tolerated by `Assert.True(acquired || !acquired)`.
   - In `VPNRouter.Headless.Tests/LifecycleChecks.cs`, separate-process lock tests explicitly isolate their runtime directory by assigning `LinuxTunOwnership.OverrideRuntimeDirectory = testDir` to a private `/tmp` directory.
   - **Precautionary Measure**: When executing `dotnet test`, setting an isolated `XDG_RUNTIME_DIR` or running under a dedicated test user avoids any potential contention on `/run/user/<uid>/vpnrouter-tun.lock` with a live desktop session.

---

## 5. Coverage Limits & Untested Boundaries

1. **Live TUN Dataplane & Privileged Helper**: No automated tests verify real `sing-box` TUN creation, packet forwarding, or nftables rule insertion on live Linux kernels. Privileged firewall operations remain mock-tested only.
2. **Host Display & Keyboard Navigation**: `qml-test-runner.sh` executes offscreen Quickshell substitution with a synthetic `KeyboardPanel.qml` stub; real keyboard trapping, panel popups, and Wayland compositor layer-shell integration remain untested.
3. **Concurrent Multi-Client Transport Stress**: High-throughput NDJSON line bursts exceeding `MaxOutputQueueFrames` (8 frames) under concurrent pipelined requests have not been soak-tested on live pipes.
4. **Single-Process Evaluation**: All findings were identified via differential source review and static analysis without modifying the product codebase or running network-active agents.

*Note: This report represents an independent differential audit artifact; it does not constitute a release sign-off or task completion claim.*
