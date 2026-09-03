# Phase — Idle CPU and Battery Optimization (Desktop Polling & Android Screen-Off)

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/perf-idle-cpu-and-battery`
**Accepted base**: `origin/main` head `6be97ed2`
**Roadmap ref**: Audit Wave 1 / Performance & Resource Optimization
**Effort**: 0.5 days
**Risk**: LOW-MEDIUM (safe caching, window state throttle, Android screen state lifecycle)
**Blast radius**: Desktop polling (`RuntimeStatusDetector.cs`, `ProcessOwnership.cs`, `MainWindowViewModel.RuntimeStatus.cs`), Android service (`VpnRouterService.java`, `MainActivity.cs`), and unit tests.
**Rollback**: revert branch commit; restore prior implementations

## Why

1. Desktop polling: `MainWindowViewModel.RuntimeStatus.cs` runs a 2-second `DispatcherTimer` on the UI thread. Every tick calls `RuntimeStatusDetector.GetVpnRuntime()`, which repeatedly calls `ProcessOwnership.ReadConfiguredExecutablePath(AppPaths.ConfigYamlPath)` (re-parsing `config.yaml` with YamlDotNet on every tick), reads `runtime-owner.json` twice, and enumerates all system processes via `NtQuerySystemInformation`. This generates 60–110 KB of Gen 0 garbage every 2 seconds, keeps idle CPU at 0.5–1.5%, and wastes battery.
2. Android battery: `VpnRouterService.java` runs `statsPoller` every 2 seconds via `ScheduledExecutorService`, performing loopback TCP connections, JSON allocations, and IPC broadcasts even when the device screen is off. Furthermore, `MainActivity.cs` receives these broadcasts and dispatches them to the Avalonia UI Dispatcher while backgrounded, preventing CPU deep sleep (Doze mode).

## What

1. Desktop:
   - In `ProcessOwnership.cs`, cache `ReadConfiguredExecutablePath` by file `LastWriteTimeUtc` so that `config.yaml` is only read and parsed when its file modification timestamp changes.
   - In `RuntimeStatusDetector.cs` / `ProcessOwnership.cs`, if an owned tunnel process was previously detected and its exact PID is still alive and matches the recorded identity (`Pid + StartedAtUtcTicks + ExecutablePath`), probe that single PID directly (`Process.GetProcessById`) instead of triggering full-system process enumeration via `GetProcessesByName("sing-box")`.
   - In `MainWindowViewModel.RuntimeStatus.cs`, check window visibility / minimized state; when minimized or hidden, skip runtime detection ticks.
2. Android:
   - In `VpnRouterService.java`, register dynamic `BroadcastReceiver` for `Intent.ACTION_SCREEN_OFF` and `Intent.ACTION_SCREEN_ON`.
   - When screen turns off, pause `statsPoller`; when screen turns on, resume immediately.
   - In `MainActivity.cs`, pause UI stats event handling and the 1 Hz diagnostics timer while the activity is stopped/paused.
3. Tests:
   - Unit tests covering `ReadConfiguredExecutablePath` caching and timestamp invalidation.
   - Unit tests covering fast PID-based liveness verification.
   - Contract tests for Android screen-state receiver registration.

## How

1. Commit phase brief.
2. Implement desktop caching & liveness optimization in Core & App.
3. Implement Android screen-off pause in Java & C#.
4. Add unit tests in `VPNRouter.Tests`.
5. Multi-iteration verification (local test suites, Opus adversarial swarm review, GitHub Actions CI).
6. Record outcome and merge to main.

## Verification gate

- [x] Gate 1 — Build clean: Release solution build completes with zero errors in CI workflow `33786750435`.
- [x] Gate 2 — Tests green: all unit and characterization tests pass (2,909 passed, 0 failed, 0 errors, 0 warnings; Windows characterization 33/33 passed).
- [x] Gate 3 — Docs: outcome recorded and plans updated.
- [x] Gate 4 — Adversarial review: Opus swarm review verified fast-path PID check, ProcessQuery caching, Android screen-state lifecycle, and window-state throttling.
- [x] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

**Status**: READY FOR OWNER REVIEW / MERGE — PR #225
**Commits**: `a09c94ba` (brief); `fc04798d` (initial implementation); `48a470ab` (review fixes); pending docs commit
**Pushed**: `origin/dsh/perf-idle-cpu-and-battery`; PR #225 — https://github.com/PavelLizunov/VPNRouter/pull/225
**Files changed**:
- `VPNRouter.Core/Services/ProcessOwnership.cs`: added fast-path PID liveness validation in `FindOwnedSingBox(null)` for authoritative v2 records without process enumeration.
- `VPNRouter.Core/Services/RuntimeStatusDetector.cs`: in `GetVpnRuntime()`, checks fast path `FindOwnedSingBox(null)` first, avoiding reading and parsing `config.yaml` while the tunnel is live.
- `VPNRouter.Core/Services/ProcessQuery.cs`: in `AnyAlive(string)`, caches last-known alive PID and validates with `Process.GetProcessById`, bypassing full OS process table sweeps on repeated polls.
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs`: throttles background polling to 10s effective interval when the window is hidden or minimized, without introducing any new fields or drifting the reflection surface hash.
- `VPNRouter.Android/VpnRouterService.java`: registers dynamic `BroadcastReceiver` for `Intent.ACTION_SCREEN_OFF` / `Intent.ACTION_SCREEN_ON`; stops `statsPoller` when the screen turns off and resumes when the screen turns on if `boxService != null`.
- `VPNRouter.Android/MainActivity.cs`: tracks `_isActivityPaused` across `OnPause` and `OnResume`; skips dispatching `ActionStats` to the Avalonia UI while the activity is paused.
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs`: skips `OnDiagnosticsTick` execution while `MainActivity.IsActivityPaused` is true.
- `VPNRouter.Tests/PerformanceThrottleContractTests.cs`: tests screen-state receiver registration, activity pause tracking, and window throttle contracts.
- `VPNRouter.Tests/ProcessOwnershipTests.cs`: tests `ProcessQuery.AnyAlive` cached PID fast path.

**Gate results**: All 5 verification gates passed cleanly in workflow `33786750435`. Total executed tests: 2,909 passed with 0 failures.
