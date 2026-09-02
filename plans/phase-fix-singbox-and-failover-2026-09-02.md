# Phase — SingBoxManager Exit Handling & AutoFailover Resilience

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-singbox-and-failover`
**Accepted base**: `origin/main` head `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
**Roadmap ref**: matrix audit Category 1.2 & 1.3 / findings `SEC-1.2-01`, `SEC-1.2-02`, `SEC-1.3-01`, `SEC-1.3-04`
**Effort**: 1 day
**Risk**: LOW to MEDIUM (targeted process supervision and recovery fixes in `SingBoxManager.cs`, `HealthMonitor.cs`, and `AutoFailoverEngine.cs`)
**Blast radius**: Process exit handling, TUN semaphore cleanup on failed restart, health-monitor backoff progression, and failover candidate exclusion; new unit tests
**Rollback**: revert branch commits; restore prior supervision and failover logic

## Why

Auditing Category 1.2 and 1.3 identified four critical reliability and recovery defects:
1. `SEC-1.2-01`: In `SingBoxManager.Lifecycle.cs:825`, the `startedHandle.Exited` subscription discards event parameters (`(_, _) => OnProcessExited()`). In `SingBoxManager.CrashDetect.cs:33`, `OnProcessExited()` re-reads instance field `_handle`. When an intentional stop or restart runs `StopInternal`, `_handle` is nulled or disposed in `finally`. When `OnProcessExited` runs asynchronously on the ThreadPool, `exitCode` is null, exit-code suppression fails, and `Crashed` is falsely fired during normal disconnects or reloads.
2. `SEC-1.2-02`: In `SingBoxManager.Lifecycle.cs:484-518`, `Restart()` retains `_tunLock` (`releaseLock: false`) but has no `catch` block around `LaunchProcess`. If `LaunchProcess` throws an exception (e.g. invalid config rejected on reload fallback), `State` is never marked `Failed` and `_tunLock` remains permanently held, causing all future connection attempts to fail with `TunOwnershipException`.
3. `SEC-1.3-01`: In `HealthMonitor.cs:988-992`, `lock (_attemptRestartLock) { _restartAttempts = 0; }` is called inside the `ContinueWith` continuation immediately after process restart, before tunnel health or connectivity is verified. If the process crashes 1s later, `_restartAttempts` is reset to 0, trapping `HealthMonitor` in an infinite 5-second crash loop, defeating exponential backoff (10s, 20s, 40s), and starving `FailoverRequested`.
4. `SEC-1.3-04`: In `AutoFailoverEngine.cs:214-225`, when candidate restart fails (`committed == false`), the active server reverts to `oldActive`, but `candidate` is never added to `_tried`. On the next dead-config trigger, `PickNextCandidate` picks the exact same failed server again, locking the engine into an infinite retry loop on a single broken candidate and preventing rotation to secondary/tertiary servers.

## What

- In `SingBoxManager.Lifecycle.cs` and `CrashDetect.cs`:
  - Pass the event-captured exit code from `startedHandle.Exited += (_, code) => OnProcessExited(code);` directly to `OnProcessExited(int? eventExitCode)`.
  - Check suppression against `eventExitCode` first before attempting to query `_handle`.
  - Add a `catch` block in `Restart()` that sets `State = SingBoxState.Failed`, releases `_tunLock`, and re-throws the exception.
- In `HealthMonitor.cs`:
  - Remove premature counter reset `_restartAttempts = 0` from the post-launch continuation (lines 988-992). The attempt counter is reset strictly in `OnHealthTick` when health is verified by a live probe (`isHealthy && !_vpnWasRunning`).
- In `AutoFailoverEngine.cs`:
  - In the `!committed` block, record `newName` into `_tried` (when not cancelled by the user), ensuring subsequent failover evaluations advance down the server pool.
- In `VPNRouter.Tests`:
  - Add unit tests verifying `OnProcessExited` suppression when `_handle` is nulled.
  - Add unit tests verifying `Restart()` releases `_tunLock` when `LaunchProcess` throws.
  - Add unit tests verifying `HealthMonitor` preserves `_restartAttempts` and backoff across rapid crashes.
  - Add unit tests verifying `AutoFailoverEngine` excludes failed candidates and advances to the next server.

## How

1. Establish baseline on `origin/main` in PR CI.
2. Implement fixes in `SingBoxManager`, `HealthMonitor`, and `AutoFailoverEngine`.
3. Add covering unit tests in `VPNRouter.Tests`.
4. Run independent adversarial review via `opus-swarm`.
5. Verify clean build and all test suites on Ubuntu and Windows in GitHub Actions.

### Tests written

- `OnProcessExited_WhenHandleNulledDuringStop_SuppressesCrashEvent`
- `Restart_WhenLaunchThrows_ReleasesTunLockAndSetsFailedState`
- `HealthMonitor_RapidCrashes_ProgressesExponentialBackoffWithoutPrematureReset`
- `AutoFailoverEngine_FailedRestart_ExcludesCandidateAndAdvancesToNext`

### Verification approach

Run focused unit tests and full test suites on Ubuntu and Windows. GitHub Actions is the mechanical oracle.

## Verification gate

- [x] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors in PR workflow `33684042693`.
- [x] **Gate 2 — Tests green**: baseline `2830 total / 2773 executed` became `2834 total / 2777 executed`, all passed with zero errors and zero warnings; Windows characterization passed `19/19` with zero failures.
- [x] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [x] **Gate 4 — Self-review**: independent Opus review verified exit-code capture, TUN lock release, counter reset logic, and candidate rotation without regressions.
- [x] **Gate 5 — UI verify**: N/A (Core services changes; UI surface untouched).
- [x] **Gate 6 — Characterization diff**: existing lifecycle and process runner characterizations continue to pass.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR #218 remains open and unmerged
**Commits**: `a29ce880` (brief); `dadde426` (implementation + tests)
**Pushed**: `origin/dsh/fix-singbox-and-failover`; PR #218 — https://github.com/PavelLizunov/VPNRouter/pull/218
**Test deltas**: +4 unit tests across `AutoFailoverEngineTests`, `HealthMonitorFailoverTriggerTests`, and `SingBoxManagerProcessRunnerTests` (`2834 total / 2777 executed / 2777 passed / 0 failed / 0 warning`); Windows characterization `19/19 passed`
**Files changed**:
- `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs`: pass event-captured exit code to `OnProcessExited(code)`, and wrap `Restart()` in `catch` block to set `State = Failed` and release `_tunLock`.
- `VPNRouter.Core/Services/SingBoxManager.CrashDetect.cs`: prioritize event-captured `eventExitCode` over reading mutable `_handle`.
- `VPNRouter.Core/Services/HealthMonitor.cs`: remove premature `_restartAttempts = 0` from restart continuation, preserving exponential backoff and `FailoverRequested`.
- `VPNRouter.Core/Services/AutoFailoverEngine.cs`: record failed candidate in `_tried` upon restart failure, ensuring rotation to subsequent servers.
- `VPNRouter.Tests/AutoFailoverEngineTests.cs`: added unit test verifying candidate rotation after failed restart.
- `VPNRouter.Tests/HealthMonitorFailoverTriggerTests.cs`: added unit test verifying multi-attempt backoff progression to ceiling.
- `VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs`: added unit tests verifying `Restart()` releases `_tunLock` on failure and `Stop()` suppresses exit events even when `_handle` is nulled.
- `plans/phase-fix-singbox-and-failover-2026-09-02.md`: this phase brief and outcome record.

**Gate results**: All 6 gates passed in workflow `33684042693`.

**Surprises encountered**:
- In `HealthMonitor.cs`, `AttemptRestart()` evaluates `_restartAttempts >= MaxRestartAttempts` before incrementing the counter. For `MaxRestartAttempts = 2`, the ceiling is hit on invocation 3, which our unit test reflects.

**Follow-ups spawned**: Next confirmed defect packages (Packet 3: `CustomConfigInjector` rule leaks; Packet 4: `EtwProcessMonitor` reset and `NaivePairing` global fallback) are ready for subsequent task branches.
**Lessons for methodology doc**: Process exit listeners must always capture exit codes directly from the event args rather than re-querying mutable instance state that may have been disposed or cleared by stopping threads.
