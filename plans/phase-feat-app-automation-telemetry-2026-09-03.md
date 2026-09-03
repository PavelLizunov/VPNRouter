# Phase — Cross-Platform UI Automation & Telemetry Server (AppAutomationDriver)

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/feat-app-automation-telemetry`
**Accepted base**: `origin/main` head `be495b05`
**Roadmap ref**: Cross-Platform Testing & Real-Time Observability
**Effort**: 0.5 days
**Risk**: LOW (opt-in loopback server, inactive by default)
**Blast radius**: `VPNRouter.App/Services/AppAutomationDriver.cs`, `VPNRouter.App/Program.cs`, `VPNRouter.App/App.axaml.cs`, and `VPNRouter.Tests`.
**Rollback**: revert branch commit; restore prior implementations

## Why

Currently, automated E2E testing and live telemetry measurement on real user interfaces are confined to Windows UI Automation via `WINBRAT` (over WinRM). There was no cross-platform mechanism for developers or autonomous agents to inspect live UI state, query memory/CPU/GC and Dispatcher latency, take live window screenshots, or dispatch user actions programmatically on Linux and macOS without native OS-level desktop automation packages.

## What

1. Embed `AppAutomationDriver` in `VPNRouter.App/Services/`:
   - Activated only when `--automation-port <port>` or `VPNROUTER_AUTOMATION_PORT` is specified (0 overhead in standard production runs).
   - Listens on `http://127.0.0.1:<port>/` with optional Bearer token authentication via `--automation-token <secret>` or `VPNROUTER_AUTOMATION_TOKEN`.
   - Endpoints:
     - `GET /metrics`: returns process memory (WorkingSet, PrivateBytes, CPU time), GC generation collections and total heap size, Avalonia Dispatcher latency measurement, and live ViewModel status (`IsConnected`, `IsConnecting`, `IsSimpleMode`, `SelectedTabIndex`, `StatusText`, `ServersCount`, `WindowState`).
     - `GET /ui/tree`: dumps the live Avalonia visual tree hierarchy with control names, types, texts, visibility, and bounding coordinates.
     - `POST /ui/action`: dispatches actions on `Dispatcher.UIThread`: `switch_tab`, `toggle_mode`, `connect`, `click` (by button name or text), and `set_text`.
     - `GET /ui/screenshot`: renders the active window into `RenderTargetBitmap` and returns PNG bytes on Linux, macOS, and Windows.
2. In `VPNRouter.App/Program.cs`:
   - Parse `--automation-port` and `--automation-token` in `AppAutomationDriver.ParseArgs(args)`.
3. In `VPNRouter.App/App.axaml.cs`:
   - Call `AppAutomationDriver.StartIfConfigured(mainWindow, _viewModel)` on startup, and `AppAutomationDriver.Stop()` on application shutdown.
   - Preserve `MainWindowViewModelCharacterizationTests` public API surface hash strictly unchanged.
4. Tests:
   - Comprehensive test suite in `VPNRouter.Tests/AppAutomationDriverTests.cs` verifying argument parsing, environment variable fallbacks, Bearer token authorization, metrics gathering, tab switching, and visual tree extraction.
   - Lifecycle wiring test in `VPNRouter.Tests/PerformanceThrottleContractTests.cs`.

## How

1. Commit phase brief.
2. Commit implementation and test suites.
3. Multi-iteration verification (build/tests, Opus adversarial review, GitHub Actions CI).
4. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [ ] Gate 1 — Build clean: Release solution build completes with zero errors.
- [ ] Gate 2 — Tests green: all unit and characterization tests pass (0 failures).
- [ ] Gate 3 — Docs: outcome recorded and plans updated.
- [ ] Gate 4 — Adversarial review: Opus swarm review confirms loopback isolation, auth enforcement, and zero VM surface drift.
- [ ] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

Pending execution.
