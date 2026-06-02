# P0 handle-leak sweep — v2.40.0-r3 (audit Этап 1), 2026-06-02

First action off `plans/bug-responsiveness-memory-audit-targets-2026-06-02.md`
(P0 "Неосвобожденные Process после GetProcessesByName"). User picked "Этап 1:
handle-leak sweep". A bare `Process.GetProcessesByName(name).Length` leaks one OS
kernel handle per returned Process until GC finalises it — on hot polling paths
(runtime status every 1–2 s) that was the AU-9 "+170 handles per VPN cycle" leak
(fixed centrally in RuntimeStatusDetector, v2.31.1) but still open in side paths.

## Fix: shared handle-safe helper

New `VPNRouter.Core/Services/ProcessQuery.cs` — `AnyAlive(name)`,
`AnyAlive(params names)`, `CountAlive(name)`; all dispose the `Process[]` in a
`finally`. `RuntimeStatusDetector.AnyProcessAlive` now delegates to it (one
source of truth; its handle-leak test still pins the behaviour).

## Full audit of every product `GetProcessesByName` site

| Site | Pattern | Verdict |
|---|---|---|
| `ZapretManager.cs:39` IsWinwsRunning | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `ZapretActions.cs:125` conflict diag | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `ZapretActions.cs:143` winws diag | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `MainWindowViewModel.cs:2978` DetectServiceManagedVpn | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `MainWindowViewModel.cs:4331` IsZapretRunning | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `FreeConfigsPageViewModel.cs:1561` IsMainVpnActive | `.Length>0` inline | FIXED → ProcessQuery.AnyAlive |
| `ZapretActions.cs:53` ClearDiscordCache | `var running` + foreach, no dispose | FIXED → try/finally dispose (yield-safe) |
| `ZapretManager.cs:46` WinwsPid | capture + `finally dispose` | already correct |
| `ConflictingVpnDetector.cs:97` | `finally { foreach dispose }` | already correct |
| `FirewallManager.cs:292` | `finally { proc.Dispose() }` | already correct |
| `HealthCheck.cs:285/290` | explicit `foreach Dispose` | already correct |
| `OrphanCleanup.cs:116` kill loop | `finally { proc.Dispose() }` | already correct |
| `TgProxyManager.cs:528` kill loop | `finally { proc.Dispose() }` | already correct |
| `ZapretAutoStrategy.cs:934/1113` | `p.Dispose()` in loop | already correct |
| `ZapretUpdater.cs:546` kill loop | `finally { proc.Dispose() }` | already correct |
| `MainWindowViewModel.cs:4300` KillAllZapret | `finally { proc.Dispose() }` | already correct |
| `ServiceViewModel.cs:72` ResolveServicePid | `finally { foreach dispose }` | already correct |
| `Service/Program.cs:48` orphan cleanup | `finally { z.Dispose() }` | already correct |

Net: **7 real leaks fixed** (6 inline `.Length` hot-poll + 1 cold-path Discord
read-and-drop); every other site already disposed. The codebase's foreach-kill
loops were already disciplined.

## Regression guard

`.githooks/pre-commit` Gate 7: scans STAGED product `.cs` (excludes
`VPNRouter.Tests` + `ProcessQuery.cs` + comment lines) for
`GetProcessesByName(...).Length` and hard-fails the commit — the leak can't
quietly return. Satisfies the audit DoD: "no product
GetProcessesByName(...).Length without disposing the objects."

## Tests

`VPNRouter.Tests/ProcessQueryTests.cs` (11): input guards (null/empty/whitespace),
missing-process negative, current-process positive, params overload, and a
500-iteration callable-stability soak (leak proxy). RuntimeStatusDetectorHandleLeakTests
still green after the delegate refactor.

## Verification
Desktop build 0 errors; ProcessQuery+RuntimeStatusDetector+Conflicting 21/21;
full logic suite 1558/0. MVM characterization unchanged (private body changes).

## Deferred (not in this DoD item)
The audit's other P0/P1 items (Android Servers O(N²) rebuild, Public Configs
UI-queue, page-subscription teardown, SingBoxManager ProcessExit lambda,
HttpClient ownership, AppIconCache native teardown) remain on the audit map for
measurement-first follow-ups (Этап 2/3).
