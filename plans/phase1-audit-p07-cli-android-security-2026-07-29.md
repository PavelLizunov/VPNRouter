# Phase 1 Audit Remediation — P07 CLI Stop Ownership and Android Error Scrub

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p07-cli-android-security-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~3-4 h
**Risk**: HIGH (CLI stop is a process-lifecycle path; Android error scrub touches trust boundary)
**Blast radius**: 2 CLI product files + 1 Android product file + tests
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| CLI-1 | P1 | CONFIRMED | **P1** | High |
| CLI-2 | P1 | CONFIRMED | **P1** | High |
| AND-1 | P1 | CONFIRMED | **P1** | High |

ONLY CLI-1, CLI-2, AND-1. Explicitly NOT in scope: AND-2 (onRevoke lifecycle,
REFUTED by P00 — `super.onRevoke()` calls `stopSelf()`; no code change needed).

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

Three distinct defects:

- **CLI-1** — `vpnrouter stop` kills only the recorded sing-box child PID and
  clears the state file. The still-running `start` process sees the child death
  as a crash (`_isStopping` is false because only Ctrl+C sets it), restarts
  sing-box via `HealthMonitor.AttemptRestart`, and cannot re-record the new PID
  because the state file was already cleared. The VPN runs untracked.
- **CLI-2** — `stop` kills the PID from the state file with NO image/path/name
  ownership validation. If the recorded PID has been reused by the OS,
  `Kill(entireProcessTree: true)` terminates an unrelated process tree. An
  ownership gate (`ProcessOwnership.IsOwnedSingBox`) already exists and is used
  by the GUI but not by the CLI.
- **AND-1** — a libbox `startTunnel` failure logs and broadcasts the RAW
  exception message. libbox messages can embed server addresses, UUIDs, or
  config fragments. A scrubber exists (`scrubSecrets`) but is applied only in
  the crash-report builder, not on the tunnel-error path.

## Current root cause (verified against current code)

### CLI-1
- [FACT] `VPNRouter.CLI/Commands/StopCommand.cs:23-24` — `Process.GetProcessById(state.SingBoxPid)`
  + `Kill(entireProcessTree: true)`. No IPC/mutex/event signals the running `start`.
- [FACT] `:38` — `StateFile.Clear()` after kill.
- [FACT] `VPNRouter.CLI/Commands/StartCommand.cs:198-204` — `start` blocks on
  `Task.Delay(Timeout.Infinite, cts.Token)`; exits only on Ctrl+C (`Console.CancelKeyPress`).
- [FACT] `StartCommand.cs:164-167` — `SingBoxStarted` handler: `var existing = StateFile.Read(); if (existing == null) return;`
  — stop already cleared state, so the new PID is never re-recorded.
- [FACT] `VPNRouter.Core/Services/HealthMonitor.cs:252` — `_singBox.Crashed += OnSingBoxCrashed;`
- [FACT] `HealthMonitor.cs:728` — `OnSingBoxCrashed` returns early only `if (_isStopping)`.
  `_isStopping` is set true solely by the Ctrl+C / engine `Stop()` path — NOT by an external child kill.
- [FACT] `HealthMonitor.cs:753-754` — `if (RestartOnFailure) AttemptRestart();`
- [INFER] No IPC/mutex/named-pipe mechanism exists between CLI `start` and `stop`
  (grep confirms no `NamedPipe`, `Mutex`, `EventWaitHandle` in VPNRouter.CLI).

### CLI-2
- [FACT] `StopCommand.cs:23-24` — kills the PID with NO ownership check.
- [FACT] `VPNRouter.Core/Services/OrphanCleanup.cs:44` — `public static KillOrphans(...)`.
- [FACT] `:91` — `KillByName("sing-box", null, killOnly: ProcessOwnership.IsOwnedSingBox)`
  (comment: "sing-box is a common third-party process name").
- [FACT] `VPNRouter.Core/Services/ProcessOwnership.cs` — `IsOwnedSingBox` validates
  the process image path contains the VPNRouter install directory.
- [FACT] Used by GUI (`Program.cs:405`, `MainWindowViewModel.cs:4156,4233`) but NOT by `StopCommand`.

### AND-1
- [FACT] `VPNRouter.Android/VpnRouterService.java:682` — `Log.e(LOG_TAG, "startTunnel failed: " + e.getMessage(), e)`.
- [FACT] `:683-685` — `Intent … putExtra(EXTRA_ERROR_MESSAGE, e.getMessage())` + `sendBroadcast`.
- [FACT] `:465` — `scrubSecrets` method exists (redacts UUIDs, server addresses, tokens via regex).
- [FACT] `:448` — `scrubSecrets` is called ONLY in the crash-report builder, not on the `:682/:684` error path.
- [FACT] C# `AndroidDiagnosticsExporter.cs:236,270` — `RedactLogText` redacts elsewhere.
- [INFER] Attacker/trust boundary: logcat readers and any app/UI consumer of the
  broadcast extra. Impact: VPN credential/endpoint disclosure.

## What

### Minimal expected file list
- `VPNRouter.CLI/Commands/StopCommand.cs` — ownership check before kill (CLI-2);
  signal the owner process to stop gracefully (CLI-1).
- `VPNRouter.CLI/Helpers/StateFile.cs` — add owner PID to state (CLI-1).
- `VPNRouter.CLI/Commands/StartCommand.cs` — record owner PID in state; handle
  external stop signal (CLI-1).
- `VPNRouter.Android/VpnRouterService.java` — scrub before log + broadcast (AND-1).
- `VPNRouter.Tests/StopCommandOwnershipTests.cs` (new, CLI-2).
- `VPNRouter.Tests/StopCommandProtocolTests.cs` (new, CLI-1).

### Explicit non-goals
- Do NOT fix AND-2 (REFUTED — `super.onRevoke()` already calls `stopSelf()`).
- Do NOT change `HealthMonitor`, `VpnEngine`, or `SingBoxManager`.
- Do NOT add a full IPC framework (named pipes, gRPC) — a minimal file-based
  or event-based signal is sufficient.
- Do NOT change `ProcessOwnership` or `OrphanCleanup` internals.
- Do NOT modify the Android `scrubSecrets` regex patterns (they are correct;
  the bug is that they are not CALLED on the error path).
- Do NOT touch the GUI stop path (`MainWindowViewModel`).

## How (ordered; fix each shared root cause once)

### CLI-2 (ownership check — do first, smaller change)
1. In `StopCommand.cs`, before `Kill`, call `ProcessOwnership.IsOwnedSingBox(proc)`
   (reuse the existing helper). If it returns false, print a warning
   ("PID {pid} is not a VPNRouter-owned sing-box process — refusing to kill")
   and return 1 without killing. This mirrors the GUI's `OrphanCleanup` pattern.

### CLI-1 (stop-request protocol)
2. In `StateFile.cs`, add `OwnerPid` (int) to `RunState`. `Write` serializes it;
   `Read` deserializes it (default 0 for backward compat with old state files).
3. In `StartCommand.cs`, after `StateFile.Write(new RunState { ... })`, include
   `OwnerPid = Environment.ProcessId`.
4. In `StopCommand.cs`, after the ownership check (CLI-2), signal the owner
  process to stop. Minimal approach: use a named `EventWaitHandle`
  (`VPNRouter_CLI_Stop_{OwnerPid}`, `EventResetMode.AutoReset`). Set the event;
  wait up to 5 s for the owner to exit; if it does not, fall back to killing
  the sing-box child (existing behavior) + clear state.
5. In `StartCommand.cs`, create the same named event in the `start` process.
  Register a wait callback that cancels the `CancellationTokenSource` (same as
  Ctrl+C). This triggers the existing graceful shutdown path (`:207-213`:
  `engine.Stop(); StateFile.Clear();`).
6. Clear state ONLY after confirmed owner exit (move `StateFile.Clear()` from
  `:38` to after the owner-exit wait). If the owner exits gracefully, it clears
  state itself; `stop` verifies and clears only if stale.

### AND-1 (Android error scrub)
7. In `VpnRouterService.java`, at the `startTunnel` catch block (`:682-685`),
   apply `scrubSecrets(e.getMessage())` BEFORE both `Log.e` and the broadcast
   `putExtra`. One scrub call, two consumers:
   ```java
   String safeMsg = scrubSecrets(e.getMessage());
   Log.e(LOG_TAG, "startTunnel failed: " + safeMsg, e);
   ...putExtra(EXTRA_ERROR_MESSAGE, safeMsg)...
   ```
   Do NOT scrub the exception object passed to `Log.e`'s third argument
   (stack trace) — it does not contain the message string in the logcat
   output format. Do NOT modify `scrubSecrets` itself.

## Callers / consumers to preserve

CLI-1/CLI-2:
- `StopCommand.Execute` — single entry point for `vpnrouter stop`.
- `StartCommand.ExecuteAsync` — single entry point for `vpnrouter start`.
- `StateFile.Read/Write/Clear` — used by `StatusCommand.cs` (reads state),
  `StartCommand` (writes), `StopCommand` (reads + clears).
- `HealthMonitor.OnSingBoxCrashed` — restart logic; MUST NOT be changed.
  The fix ensures `stop` signals the OWNER, which sets `_isStopping` via
  `engine.Stop()`, so `OnSingBoxCrashed` correctly early-returns.
- `ProcessOwnership.IsOwnedSingBox` — existing gate; unchanged.
- `OrphanCleanup.KillOrphans` — GUI path; unchanged.

AND-1:
- `VpnRouterService.startTunnel` catch block — the fixed path.
- `scrubSecrets` — existing method; unchanged.
- `buildCrashReport` (`:448`) — existing scrub caller; unchanged.
- `AndroidDiagnosticsExporter.RedactLogText` — C# redactor; unchanged.

## Regression tests (exact)

New `VPNRouter.Tests/StopCommandOwnershipTests.cs` (CLI-2; cross-platform, no real process kill):
- `Stop_RefusesToKill_UnownedPid` — create a `RunState` with a PID that is NOT
  a VPNRouter-owned sing-box (e.g., the test runner's own PID). Execute
  `StopCommand`. Assert exit code 1 and the process is still alive.
- `Stop_AcceptsOwnedSingBox` — mock/fake a process that passes
  `ProcessOwnership.IsOwnedSingBox` (or use a known-benign PID with a mocked
  ownership check). Assert the kill path is reached. (If mocking is not feasible
  without a seam, assert the ownership check is called and the code path branches
  correctly via a dry-run flag or output inspection.)

New `VPNRouter.Tests/StopCommandProtocolTests.cs` (CLI-1; cross-platform):
- `Stop_SignalsOwner_OwnerExitsGracefully` — simulate: write a state file with
  `OwnerPid` = current process. Create the named event. Execute `StopCommand`.
  Assert the event is signaled. Assert state file is cleared after owner exit.
- `Stop_FallsBackToChildKill_WhenOwnerUnresponsive` — write a state file with
  `OwnerPid` = a non-existent PID. Execute `StopCommand`. Assert it falls back
  to the child-kill path and clears state.
- `Start_RecordsOwnerPid` — verify `RunState.OwnerPid` is set to
  `Environment.ProcessId` after `StartCommand` writes state. (May require
  inspecting the state file after a dry-run or mocked engine start.)

AND-1 — no new C# test (Java code; tested via Android instrumentation if available).
Static verification: grep the error path for `scrubSecrets` call presence.

Must stay green: all existing CLI tests, `ProcessOwnership` tests (if any),
Android build (Gradle compile in CI if enabled).

## Risks

- **Security**: CLI-2 prevents killing unrelated processes (safety). AND-1
  prevents credential disclosure via logcat/broadcast (privacy). CLI-1 prevents
  untracked VPN resurrection (lifecycle integrity).
- **Compatibility**: `RunState.OwnerPid` is additive (default 0 for old state
  files). The named event is auto-cleaned by the OS on process exit. Backward
  compat: if `OwnerPid` is 0 (old state), `stop` falls back to the existing
  child-kill behavior.
- **Cross-platform**: CLI-1 named events (`EventWaitHandle`) are Windows-only.
  On Linux/macOS, fall back to the existing child-kill + state-clear (the CLI
  `start` path is Windows-primary; Linux/macOS use the Avalonia app). Guard with
  `OperatingSystem.IsWindows()`. CLI-2 `ProcessOwnership.IsOwnedSingBox` is
  already cross-platform (image path check). AND-1 is Android-only Java.
- **Rollback**: per-file revert. No schema/migration/wire-format change.
- **P02 semantic dependency**: P02's brief notes CLI-1 relies on the
  `HealthMonitor.Stop()` → `_isStopping` → `OnSingBoxCrashed` early-return
  contract. P07's fix ensures `stop` signals the owner, which calls `engine.Stop()`,
  which sets `_isStopping`. **P02 MUST NOT regress the graceful-stop-no-restart path.**

## Dependencies and file overlap with the other seven packages

- **P01 (UPD-1/UPD-2)**: P01 touches `TestUpdateCommand.cs` (CLI project) and
  `AndroidApp.AutoUpdate.cs` (Android project). P07 touches `StopCommand.cs`,
  `StartCommand.cs`, `StateFile.cs` (CLI) and `VpnRouterService.java` (Android).
  Different files; no overlap. Sequence to avoid concurrent edits in the same project.
- **P02 (FAIL-1)**: SEMANTIC dependency — CLI-1 relies on the
  `HealthMonitor.Stop()` → `_isStopping` → `OnSingBoxCrashed` early-return
  contract and `VpnEngine.Stop()` ordering. P02 MUST NOT regress this.
  No file overlap (P02 touches `VpnEngine.cs`; P07 touches CLI/Android files).
- **P05 (DATA-1)**: no overlap (SettingsLoader.cs).
- **P06 (FLOW-1)**: no overlap (SimpleMode.cs).
- **P08 (SUP-1)**: no overlap (build-linux.yml).
- **P09 (SEC/OBS)**: no overlap (SubscriptionFetcher, AppPaths, ClashLogStream).
- **P10 (ZAP-1)**: no overlap (ZapretUpdater).

## Zone CLAUDE.md constraints

- `VPNRouter.CLI/CLAUDE.md`: CLI is a thin Spectre.Console wrapper around Core.
  `StateFile.cs` is the state.json read/write helper. Admin check pattern established.
  `StartCommand` uses `ISettingsStore` ctor injection for testability.
- `VPNRouter.Core/CLAUDE.md`: `ProcessOwnership` and `OrphanCleanup` are Core
  services; `InternalsVisibleTo VPNRouter.Tests` configured.
- `VPNRouter.App/CLAUDE.md`: N/A (P07 does not touch App).
- No emoji (AGENTS.md #9).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors (CLI + Android). Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `StopCommandOwnershipTests` + `StopCommandProtocolTests` green in CI; full existing suite stays green.
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review; **security review** of the ownership check and the Android scrub change (security-relevant).
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM/ADB). Do NOT fake PASS.
- [ ] **Gate 6 — Characterization**: N/A (no god-file split; no MVM surface change).

## Outcome (PENDING — fill after remote GitHub CI)

**Status**: PENDING
**Commits**: <orchestrator fills>
**Pushed**: <orchestrator fills>
**Test deltas**: +<new> / -<removed>
**Files changed**: <count> · <total LOC delta>

**Gate results:**
- [ ] Gate 1 build (remote CI): <output>
- [ ] Gate 2 tests (remote CI): <output>
- [ ] Gate 3 docs: <output>
- [ ] Gate 4 self-review / security-review: <output>
- [-] Gate 5 UI/live: deferred (owner constraint) — not live-verified
- [-] Gate 6 characterization: N/A

**Surprises encountered**: <fill>
**Follow-ups spawned**: <fill>
**Rollback**: `git revert <hash>` / branch delete
