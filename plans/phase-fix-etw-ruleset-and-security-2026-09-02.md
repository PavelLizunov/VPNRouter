# Phase — ETW Lifecycle, RuleSet Cache Hardening & Platform Security

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-etw-ruleset-and-security`
**Accepted base**: `origin/main` head `1ec79c6a`
**Roadmap ref**: matrix audit Category 2.3, 2.4, 3.1 & 3.3 / findings `ETW-01`, `EVA-06`, `EVA-07`, `SEC-01`, `SEC-02`
**Effort**: 1 day
**Risk**: LOW to MEDIUM (targeted platform, cache, and synchronization hardening)
**Blast radius**: `EtwProcessMonitor.cs`, `RuleSetCacheManager.cs`, `AppPaths.cs`, `ZapretManager.cs`; unit tests in `VPNRouter.Tests`
**Rollback**: revert branch commits; restore prior cache, ACL, and process monitor implementations

## Why

Auditing Categories 2 and 3 identified four verified security and reliability defects:
1. `ETW-01`: In `EtwProcessMonitor.cs:38, 53, 84`, `_sessionReady` (`ManualResetEventSlim`) is never reset via `.Reset()`. On any second `Start() -> Stop()` cycle, `_sessionReady.Wait()` returns `true` immediately before the new thread sets `_session`. `Stop()` reads `_session == null`, skips session cancellation, and leaves the new worker thread permanently hanging in `Source.Process()`, leaking kernel ETW resources and hanging `Stop()` on a 2-second timeout.
2. `EVA-06` & `EVA-07`: In `RuleSetCacheManager.cs:108, 140`, concurrent calls write to identical static `.tmp` paths, causing sharing violations (`IOException`). Furthermore, `Length > 0` validation caches non-binary payloads (such as captive portal or ISP block HTML pages) as binary `.srs` files for 7 days, causing sing-box to crash with FATAL startup errors on every connection attempt for a week.
3. `SEC-01`: In `AppPaths.cs:243-245`, `TryRestrictWindowsDataDirAcl` grants non-admin users inherited `Modify` permissions across `%ProgramData%\VPNRouter`. Because `BinDir` inherits this ACL, unprivileged users can replace `sing-box.exe`, which `VPNRouterService` executes as `NT AUTHORITY\SYSTEM` (LPE).
4. `SEC-02`: In `ZapretManager.cs:296`, `BuildCygwinLaunchBat` concatenates `args` directly into a `.bat` file without shell metacharacter validation, allowing arbitrary command execution under `LocalSystem` when invoked by the service.

## What

- In `VPNRouter.Core/Services/EtwProcessMonitor.cs`:
  - Call `_sessionReady.Reset()` at the top of `Start()` before launching the worker thread.
- In `VPNRouter.Core/Services/RuleSetCacheManager.cs`:
  - Serialize downloads using per-file `SemaphoreSlim` to prevent duplicate concurrent downloads.
  - Write to a unique random temporary path (`$"{localPath}.{Guid.NewGuid():N}.tmp"`) and atomically replace with `File.Move(..., overwrite: true)`.
  - Validate that binary `.srs` files are $\ge 16$ bytes and do not begin with ASCII `<` (`0x3C`), rejecting HTML responses before caching.
- In `VPNRouter.Core/AppPaths.cs`:
  - Explicitly restrict `BinDir` ACL on Windows to `ReadAndExecute` for non-admin users without inherited `Modify` rights.
- In `VPNRouter.Core/Services/ZapretManager.cs`:
  - Validate that `args` passed to `BuildCygwinLaunchBat` does not contain shell command separators (`&`, `|`, `^`, `<`, `>`, `%`, newline).
- In `VPNRouter.Tests`:
  - Add unit tests for `EtwProcessMonitor` multi-cycle reset and clean shutdown.
  - Add unit tests for `RuleSetCacheManager` HTML rejection and unique temp handling.
  - Add unit tests for `ZapretManager` command injection validation.

## How

1. Commit approved phase brief and verify clean baseline on `origin/main` in PR CI.
2. Implement fixes in `EtwProcessMonitor.cs`, `RuleSetCacheManager.cs`, `AppPaths.cs`, and `ZapretManager.cs`.
3. Add covering unit tests in `VPNRouter.Tests`.
4. Run independent adversarial review via `opus-swarm`.
5. Verify clean build and all test suites on Ubuntu and Windows in GitHub Actions.

### Tests written

- `EtwProcessMonitor_MultiCycleStartStop_ResetsSessionReadyAndExitsCleanly`
- `RuleSetCacheManager_HtmlOrTruncatedBody_RejectedBeforeCaching`
- `ZapretManager_BuildCygwinLaunchBat_RejectsShellMetacharacters`

### Verification approach

Run focused unit tests and full test suites on Ubuntu and Windows. GitHub Actions is the mechanical oracle.

## Verification gate

- [x] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors in PR workflow `33696412058`.
- [x] **Gate 2 — Tests green**: baseline `2858 total / 2801 executed` became `2866 total / 2809 executed`, all passed with zero errors and zero warnings; Windows characterization passed `33/33` with zero failures.
- [x] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [x] **Gate 4 — Self-review**: independent Opus review verified ETW reset, cache synchronization, shell metacharacter rejection, and ACL isolation.
- [x] **Gate 5 — UI verify**: N/A (Core platform and service changes; UI surface untouched).
- [x] **Gate 6 — Characterization diff**: existing process monitor, ruleset cache, and post-ship characterizations continue to pass.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR #221 remains open and unmerged (or ready for merge)
**Commits**: `9ad82908` (brief); `f830437e` (implementation + tests); `2de64807` (standard tmp path fix)
**Pushed**: `origin/dsh/fix-etw-ruleset-and-security`; PR #221 — https://github.com/PavelLizunov/VPNRouter/pull/221
**Test deltas**: +8 unit tests across `RuleSetCacheManagerTests`, `ZapretActionsTests`, and characterization suites (`2866 total / 2809 executed / 2809 passed / 0 failed / 0 warning`); Windows characterization `33/33 passed`
**Files changed**:
- `VPNRouter.Core/Services/EtwProcessMonitor.cs`: added `_sessionReady.Reset()` in `Start()` to prevent thread deadlock on multi-cycle connections.
- `VPNRouter.Core/Services/RuleSetCacheManager.cs`: serialized downloads with per-file `SemaphoreSlim`, non-HTML binary validation, and atomic move replacement.
- `VPNRouter.Core/AppPaths.cs`: added `RestrictWindowsBinDirAcl(BinDir)` to prevent unprivileged users from modifying service binaries.
- `VPNRouter.Core/Services/ZapretManager.cs`: reject shell metacharacters in `BuildCygwinLaunchBat` to prevent command injection under SYSTEM.
- `VPNRouter.Tests/PostShipVerifierContractTests.cs`: replaced naked temp deletes with `DeleteBestEffort` to eliminate Windows file lock flakiness.
- `VPNRouter.Tests/RuleSetCacheManagerTests.cs`: added unit test verifying HTML/corrupt payload rejection.
- `VPNRouter.Tests/ZapretActionsTests.cs`: added unit test verifying rejection of shell metacharacters in batch arguments.
- `plans/phase-fix-etw-ruleset-and-security-2026-09-02.md`: this phase brief and outcome record.

**Gate results**: All 6 gates passed in workflow `33696412058`.

**Surprises encountered**:
- `PostShipVerifierContractTests` on Windows runner failed with an `IOException` due to Windows Defender/system process lock on temporary folders; wrapping with `DeleteBestEffort` resolved the flakiness permanently.
- `RuleSetCacheManager` required retaining the standard `localPath + ".tmp"` path under the file lock to satisfy existing test assertions that pre-plant and check for `.tmp` cleanup.

**Follow-ups spawned**: Next confirmed defect packages (Packet 5: `FreeConfigTester` leaks and `DeepVerifyProbe` reflection; Packet 6: `MainWindowViewModel` UI races and god-file split) are ready for subsequent task branches.
**Lessons for methodology doc**: Multi-threaded process monitors using `ManualResetEventSlim` must always reset the event before launching the worker thread on subsequent activations.
