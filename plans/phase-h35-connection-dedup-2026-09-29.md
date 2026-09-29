# H-35: remove the duplicated start and stop code in the connection view model

## Why

The duplicate-code scan (2026-09-29) showed `ToggleConnectionAsync` and `ReconnectAsync` in
`MainWindowViewModel.Connection.cs` repeating the same blocks: the `sc stop VPNRouter` process start (twice),
the `StartAsync` + `TwoPhaseStartCoordinator.RunAsync` wiring (twice, 16 lines each) and the "stop the engine
and reset the state" sequence (four times in the start path).

## What

- `TryStopVpnRouterService()` (Windows only) returns whether the service stopped with exit code 0; the stop
  path ignores the result and logs failures exactly as before, the start path sleeps 2 s only on success as
  before (reading `ExitCode` of a process that has not exited threw and skipped the sleep; the new
  `HasExited` guard gives the same result without the exception).
- `RunTwoPhaseStartAsync(ct, skipConflictCheck)` takes the skip flag as a `Func<bool>` so that
  `ToggleConnectionAsync` keeps using the value captured earlier and `ReconnectAsync` keeps reading the field
  when the task runs.
- `AbortStartAsync(statusText)` replaces the four identical failure sequences of `ToggleConnectionAsync`
  (stop engine, `IsConnecting = false`, `IsConnected = false`, status, button text, same order). The
  `TunOwnershipException`, conflicting-VPN and generic branches are left alone because their order or content
  differs. `ReconnectAsync` failure branches also differ and are untouched.

## Verification

Read of the full diff; App, CLI and Service build and the view model tests on `windows-worker`; exact-head CI
(the Windows job runs the full suite). There is no direct unit test of `ToggleConnectionAsync`; the two-phase
coordinator has its own tests.

## Outcome

Pending CI.
