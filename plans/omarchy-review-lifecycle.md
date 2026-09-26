# Independent Code Review: VPNRouter.Headless Protocol & Lifecycle

**Date:** 2026-09-17  
**Branch:** `dsh/omarchy-plugin-2026-09-17` (Base: `517bf7e2`, Brief HEAD: `b8bde92c`)  
**Scope:** `VPNRouter.Headless/Protocol/`, `Program.cs`, `RouterSession.cs`, `Lifecycle/`, `IRouterSession.cs`, and `VPNRouter.Headless.Tests/`.  
**Review Type:** Read-Only Differential Security & Architecture Review.

---

## Executive Summary

This independent code review audited the untracked headless daemon adapter and lifecycle implementation for the Omarchy Quattro shell plugin against the canonical architectural invariants, security rules, and protocol specification (`plans/omarchy-protocol-v1.md`).

While core framing, line-length bounding, and JSON depth checks are implemented, the review identified **8 concrete prioritized findings** across EOF teardown, task concurrency admission, ownership verification logic, live state synchronization, mutable static path resolution, capability detection, and secret/path disclosure. Several unit tests in `VPNRouter.Headless.Tests/` provide false confidence by testing only synthetic mocks or unrepresentative edge cases.

---

## Prioritized Findings

### Finding 1 [P0 / High]: Missing EOF Cancellation Blocks Shutdown and Risks Orphaned Background Engine
- **Source Anchors:** `VPNRouter.Headless/Protocol/ProtocolServer.cs:57-64, 126-135`
- **Description:**
  When standard input reaches EOF (e.g. parent shell process terminates or pipes close), `ProtocolLineReader` returns `LineReadStatus.Eof`. `ProtocolServer.RunAsync` executes `break;` without invoking `serverCts.Cancel()`. The server then executes:
  ```csharp
  Task[] pending;
  lock (inFlightLock) { pending = inFlightTasks.ToArray(); }
  if (pending.Length > 0) { await Task.WhenAll(pending).ConfigureAwait(false); }
  ```
  Because `serverCts` was never cancelled, `serverCts.Token` passed to in-flight tasks (`Task.Run`) remains uncancelled. The server indefinitely blocks on `Task.WhenAll(pending)` until running tasks (e.g. a long network test, connect attempt, or subscription refresh) complete on their own. If an in-flight `connect` attempt finishes during this window, sing-box starts and is left orphaned in the background.
- **Counterevidence & Test Limits:**
  `ProtocolTests.TestEofShutdownAsync` (`ProtocolTests.cs:394-406`) tests an empty `MemoryStream` where no background tasks are in-flight. It completely fails to exercise EOF behavior while an asynchronous operation is active.
- **Impact:** Violates `plans/omarchy-protocol-v1.md:15-16` ("EOF/SIGTERM cancels work and stops owned engine. No detached daemon or second shell"). Process hangs on exit; background connection attempts cannot be cancelled via EOF.
- **Fix:** Call `serverCts.Cancel()` immediately when `lineResult.Status == LineReadStatus.Eof` prior to breaking or awaiting pending tasks.

---

### Finding 2 [P0 / High]: Unbounded `Task.Run` Admission Allows ThreadPool Flooding and Fatal Backpressure Abort
- **Source Anchors:** `VPNRouter.Headless/Protocol/ProtocolServer.cs:86-108`, `VPNRouter.Headless/Protocol/ProtocolOutputQueue.cs:40-71`
- **Description:**
  For each incoming NDJSON line, `ProtocolServer` unconditionally schedules a threadpool task via `Task.Run` and adds it to `inFlightTasks`. There is no admission gate or queue limit on the reader loop. While `ProtocolDispatcher` uses a lock to reject concurrent ordinary operations with a `busy` response frame, that rejection occurs *inside* the asynchronously scheduled task.
  When a burst of requests arrives (e.g. 10 rapid commands or pipelined requests), each task executes and calls `_outputQueue.EnqueueResponse(response)`.
  In `ProtocolOutputQueue.cs:40-71`, the queue capacity is fixed at `MaxOutputQueueFrames = 8`. If 8 response frames fill the queue, any subsequent response frame sets `triggerBackpressure = true` and invokes `_onBackpressureOrEof()`. In `ProtocolServer.cs:23-33`, this callback executes `serverCts.Cancel()`.
- **Counterevidence & Test Limits:**
  `TestSingleOrdinaryCommandBusyAsync` tests only two sequential requests, never verifying pipelined bursts or concurrency limits.
- **Impact:** Denial of Service. A harmless burst of 9 requests (even invalid requests or `snapshot` queries) exhausts the output queue, trips backpressure on error frames, and fatally terminates the entire headless service.
- **Fix:** Enforce admission gating on the reader loop before scheduling `Task.Run` (reject or drop excessive requests with bounded concurrency), and do not trigger fatal process backpressure on non-dataplane error responses.

---

### Finding 3 [P1 / High]: Flawed Child PID Check in `LinuxOwnershipGuard` Causes Self-Ownership Conflict Masked by Test Bypass
- **Source Anchors:** `VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs:109-132, 165-174`, `VPNRouter.Headless/RouterSession.cs:349-356, 416-435`
- **Description:**
  In `LinuxOwnershipGuard.cs`:
  ```csharp
  if (v2.ChildPid > 0 && v2.ChildPid != Environment.ProcessId)
  ```
  and
  ```csharp
  var ownedSingBox = ProcessOwnership.FindOwnedSingBox(null);
  if (ownedSingBox is { } ownedChild && ownedChild.Pid != Environment.ProcessId)
  ```
  Both checks compare the child process PID (`sing-box`) against `Environment.ProcessId` (the parent `.NET` host process). A child process PID will never equal the parent process PID. Even when `v2.OwnerPid == Environment.ProcessId` (meaning the current process owns the tunnel), `LinuxOwnershipGuard` reports the tunnel as `HeldByAnother`.
  To mask this issue in tests, `RouterSession.cs` lines 349 and 416 use:
  ```csharp
  var isHeldByForeign = _ownershipProbe != null && _ownershipProbe().Status == OwnershipStatus.HeldByAnother;
  ```
  In production, `_ownershipProbe` is `null`, meaning `isHeldByForeign` is *never* checked in production during `DisconnectAsync` or `DisposeAsync`. If it were checked, the bug in `LinuxOwnershipGuard` would cause `RouterSession` to treat its own child as foreign, refuse to stop it, and transition to `Error: conflict`.
  Furthermore, `LinuxOwnershipGuard.cs:183-206` treats any running `VPNRouter.App` process as `HeldByAnother`, falsely blocking `ConnectAsync` even when the GUI is idle and disconnected.
- **Counterevidence & Test Limits:**
  All tests in `LifecycleChecks.cs` (`CheckNonOwnerDisposalDoesNotStopForeignSessionAsync`, etc.) inject a mock `_ownershipProbe` delegate (`() => OwnershipCheckResult.HeldByAnother(...)`). None test the production `_ownershipProbe == null` fallback or evaluate `LinuxOwnershipGuard` against an actively owned child.
- **Impact:** Breaks process ownership semantics. Disconnect/Dispose cannot safely guard foreign sessions in production, and `LinuxOwnershipGuard` self-conflicts with its own child processes.
- **Fix:** In `LinuxOwnershipGuard`, check `v2.OwnerPid`: if `v2.OwnerPid == Environment.ProcessId`, the child belongs to this instance and is `Free`. Ensure `FindOwnedSingBox` validates owner identity. In `RouterSession`, consistently fall back to `LinuxOwnershipGuard.CheckOwnership()` when `_ownershipProbe` is `null`.

---

### Finding 4 [P1 / High]: Fail-Open Readiness Guard on Null and Incomplete Live State Synchronization
- **Source Anchors:** `VPNRouter.Headless/RouterSession.cs:64-75, 482-541`, `VPNRouter.Headless/Lifecycle/TwoPhaseConnectCoordinator.cs:174-179`
- **Description:**
  1) In `RouterSession.cs` lines 505 and 533:
     ```csharp
     var guard = _engine.CaptureReadinessGuard(pid);
     if (guard == null || guard()) { TransitionState(SessionStates.Connected, null); }
     ```
     Treating `guard == null` as true violates the fail-closed invariant ("Only typed Core readiness establishes connected, not process spawn. Readiness guard fails closed on null or false"). While `TwoPhaseConnectCoordinator.cs:174-179` correctly fails closed when `guard == null`, `RouterSession` event handlers fail open to `Connected`.
  2) In `OnEngineStatusChanged` (lines 499-514), the session inspects `status.StartsWith("Connected")`. As documented in `StartupPipeline.cs:1325-1334`, `StatusChanged` emits `"Connected (PID ...)"` even when TUN warmup failed (for backwards compatibility). This causes premature or invalid transitions to `Connected`.
  3) In the `RouterSession.State` property getter (lines 68-74), if `_state == Connected && !_engine.IsRunning`, it returns `Error` dynamically but never calls `TransitionState()`, leaving `Changed` unfired.
- **Counterevidence & Test Limits:**
  `CheckReadinessGuardFailureAsync` only checks the synchronous return from `TwoPhaseConnectCoordinator.RunAsync`, never the recovery or event-driven callback paths in `RouterSession`.
- **Impact:** False-positive `Connected` transitions when readiness guards are missing or fail; client UI remains unaware of disconnected engines due to suppressed `Changed` notifications.
- **Fix:** Fail closed on `guard == null` (`guard != null && guard()`). Ignore string status `"Connected"` in `OnEngineStatusChanged`; rely exclusively on typed `Connected` event + valid guard. Ensure `TransitionState` is invoked when `IsRunning` drops.

---

### Finding 5 [P1 / Medium]: Process-Wide Static `AppPaths` Mutation Breaks Executable Resolution and Test Isolation
- **Source Anchors:** `VPNRouter.Headless/Program.cs:98`, `VPNRouter.Headless/Storage/ConfigStorage.cs:57`, `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:76`
- **Description:**
  `AppPaths.OverrideDataDir` mutates a process-wide static variable in `VPNRouter.Core.AppPaths`.
  `AppPaths.SingBoxExePath` is derived directly from `DataDir`: `Path.Combine(DataDir, "bin", "sing-box")`.
  When `--data-dir` is provided (for test isolation or custom config locations):
  1) `PlatformCapabilityVerifier.VerifyCanConnect` checks `File.Exists(AppPaths.SingBoxExePath)`. Because the custom `--data-dir` does not contain a copied `bin/sing-box` binary, `CanConnect` returns `false`, permanently disabling connection capabilities.
  2) `ConfigStorage` constructor calls `AppPaths.OverrideDataDir(DataDir)` whenever `dataDir` is passed, modifying global path resolution for any other component in the process.
- **Counterevidence & Test Limits:**
  `FeatureChecks.cs` passes isolated directories to `ConfigStorage`, which mutates `AppPaths._dataDir` across tests, but masks the failure by using `FakeRouterSession` (which does not query `SingBoxExePath`).
- **Impact:** Supplying `--data-dir` breaks `CanConnect` in production; static mutation creates cross-test and cross-instance race conditions.
- **Fix:** Decouple `SingBoxExePath` check from custom data configuration directories (verify the installed binary in `/usr/lib/vpnrouter-headless/bin` or standard install paths), and do not call `AppPaths.OverrideDataDir` inside `ConfigStorage`.

---

### Finding 6 [P1 / Medium]: Optimistic Windows Capability Reporting and Unverified Permanent Linux Killswitch Denial
- **Source Anchors:** `VPNRouter.Headless/Lifecycle/PlatformCapabilityVerifier.cs:30-34, 99-105`, `VPNRouter.Headless/RouterSession.cs:37, 159-164`
- **Description:**
  1) `PlatformCapabilityVerifier.VerifyKillSwitchSupport` line 32 unconditionally returns `true` on Windows without checking firewall status or admin privileges, violating the rule that capabilities reflect verified availability rather than platform detection.
  2) On Linux (the primary target of the Omarchy plugin), `RouterSession` production constructor passes `killSwitchProbe = null`. `VerifyKillSwitchSupport` calls `ProbeLinuxNftWithoutPassword`, which is a hardcoded stub returning `false`.
  3) In `RouterSession.cs:159`, if a profile specifies full-tunnel mode with `BlockOnVpnFail = true`, `RequiresKillSwitchAsync` returns `true`. Because `SupportsKillSwitch` is permanently `false`, `ConnectAsync` unconditionally throws `RouterException("unavailable", "Firewall killswitch is not supported without verified privileges.")`, making killswitch-enabled profiles unusable on Linux.
- **Counterevidence & Test Limits:**
  `LifecycleChecks.CheckLinuxCapabilityUnverifiedFailsClosedAsync` asserts that passing `null` returns `false`, verifying the stub returns `false` without verifying live adapter integration.
- **Impact:** Full-tunnel killswitch is unusable on Linux; Windows optimistically reports support without verification.
- **Fix:** Implement a non-destructive verification probe (e.g. testing `sudo -n nft --version` or table check) rather than hardcoding `false`, and verify admin/netsh capability on Windows.

---

### Finding 7 [P2 / Medium]: Unsanitized Exception Message Leaks Host Paths in `LinuxOwnershipGuard` via `RouterException`
- **Source Anchors:** `VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs:176-180`, `VPNRouter.Headless/RouterSession.cs:137-142`, `VPNRouter.Headless/Protocol/ProtocolDispatcher.cs:72-75`
- **Description:**
  In `LinuxOwnershipGuard.cs:176-180`:
  ```csharp
  catch (Exception ex)
  {
      logger?.Warning(ex, "[OwnershipGuard] Error validating runtime owner record; failing closed.");
      return OwnershipCheckResult.Unavailable($"Failed to verify runtime owner record: {ex.Message}");
  }
  ```
  If `ReadRuntimeOwnerRecord` throws an `IOException`, `UnauthorizedAccessException`, or `JsonException`, `ex.Message` contains full filesystem paths (including user home directories and usernames).
  `LinuxOwnershipGuard` sets `ownership.Details` to this unredacted string.
  `RouterSession.ConnectAsync` throws `new RouterException("unavailable", ownership.Details)`.
  In `ProtocolDispatcher.cs:72-75`, `RouterException` error messages are serialized directly to the wire response without scrubbing.
- **Counterevidence & Test Limits:**
  `ProtocolTests.TestNoSecretInErrorsAsync` tests only `subscriptions.add` throwing an `InvalidOperationException` (sanitized to `"An internal error occurred."`). It does not test `RouterException` instances carrying unredacted `ownership.Details`.
- **Impact:** System paths and user profile information are leaked to QML/client logs in error responses, violating privacy and security invariants.
- **Fix:** Sanitize exception messages using `BoundedTeardown.SanitizeExceptionMessage(ex)` or use a static, generic error message in `LinuxOwnershipGuard.cs:179`.

---

### Finding 8 [P2 / Low]: `ProtocolDispatcher` Rejects `snapshot` With `busy` During Active Operations
- **Source Anchors:** `VPNRouter.Headless/Protocol/ProtocolDispatcher.cs:40-65`, `VPNRouter.Headless/RouterBackend.cs:87-95, 137`
- **Description:**
  `RouterBackend.ExecuteAsync` explicitly handles `snapshot` before checking `_busyState`, and `GetSnapshot()` explicitly returns `busy = _busyState != 0` so clients can poll status while an operation is in progress.
  However, in `ProtocolDispatcher.cs:40-65`, only `cancel` and `disconnect` are treated as urgent controls. All other methods—including `snapshot`—are treated as ordinary operations.
  When an operation (such as `connect` or `servers.verify`) is running, `_activeOperationId != null`. Any `snapshot` request sent by the client is rejected with error code `"busy"`.
- **Counterevidence & Test Limits:**
  No test attempts to call `snapshot` while an asynchronous operation is running in the background.
- **Impact:** The client UI cannot fetch snapshot state while an operation is executing, breaking UI status updates and progress polling.
- **Fix:** In `ProtocolDispatcher.DispatchAsync`, allow `snapshot` to bypass `_activeOperationId` exclusivity (read-only snapshot querying).

---

## Coverage and Untested Boundaries

1. **Live Process & TUN Interaction:** All lifecycle and feature tests use `FakeLifecycleEngine` or `FakeRouterSession`. No tests execute against real `sing-box` processes, TUN devices, or Linux network namespaces.
2. **Signal Handling & ProcessExit:** `Program.cs` signal handlers (`PosixSignalRegistration`) for `SIGTERM`, `SIGINT`, and `SIGQUIT` are never executed in the test suite.
3. **Pipelined Transport Stress:** No tests verify behavior when input lines arrive concurrently or exceed `MaxOutputQueueFrames` under high-throughput conditions.
4. **Linux nftables Integration:** No automated tests verify `LinuxFirewallManager` or `sudo -n nft` execution on Linux hosts; capability probes are tested strictly against mock delegates.
