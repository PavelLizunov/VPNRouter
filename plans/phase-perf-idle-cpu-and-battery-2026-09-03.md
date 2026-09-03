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

- [ ] Gate 1 — Build clean: Release solution build completes with zero errors.
- [ ] Gate 2 — Tests green: all unit and characterization tests pass (0 failures).
- [ ] Gate 3 — Docs: outcome recorded and plans updated.
- [ ] Gate 4 — Adversarial review: Opus swarm review confirms no stale status or missed reconnects.
- [ ] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

Pending execution.
