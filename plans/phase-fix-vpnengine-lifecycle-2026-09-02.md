# Phase — VpnEngine StartAsync Cancellation & Teardown Safety

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-vpnengine-lifecycle`
**Accepted base**: `origin/main` head `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
**Roadmap ref**: matrix audit Category 1.1 / findings `VENG-01` & `VENG-02`
**Effort**: 1 day
**Risk**: LOW (targeted lifecycle safety fixes in `VpnEngine.cs`; no public API or protocol changes)
**Blast radius**: `VPNRouter.Core/Services/VpnEngine.cs` startup cancellation and teardown exception handling; new unit tests in `VPNRouter.Tests`
**Rollback**: revert branch commits; `VpnEngine.cs` returns to prior startup handling

## Why

Auditing Category 1.1 identified two critical lifecycle defects:
1. `VENG-01`: In `VpnEngine.StartAsync`, caller `ct` is not linked to `_sessionCts`. When `Stop()` is called concurrently during connection bring-up, it signals `_sessionCts?.Cancel()` and waits synchronously on `_lifecycleGate.Wait()`. Because `StartAsyncInternal` executes against `ct` rather than `_sessionCts.Token`, startup ignores the stop intent and runs all 8 phases to completion before `Stop()` can tear it down.
2. `VENG-02`: In `VpnEngine.StartAsync`, there is no `catch` block around `StartAsyncInternal`. If an exception occurs in Phase 7 or 8 (e.g. sing-box fails to start within 5s, or startup is cancelled), `TeardownInternal()` is not called. This leaves Windows Firewall block rules (`VPNRouter-Block-*`) orphaned in the OS packet filter, leaks process/ownership handles, and leaves the engine in an unrecoverable state.

## What

- In `VpnEngine.StartAsync`:
  - Link caller `ct` into `_sessionCts` via `CancellationTokenSource.CreateLinkedTokenSource(ct)`.
  - Pass `_sessionCts.Token` into `StartAsyncInternal` so both caller cancellation and `Stop()` abort in-flight bring-up immediately.
  - Wrap `StartAsyncInternal` in `try ... catch (Exception ex)`: if `!IsRunning`, invoke `TeardownInternal()` to clean up firewall rules, process handles, and network state before rethrowing the exception.
- In `VPNRouter.Tests`:
  - Add unit tests verifying that `Stop()` called during an active `StartAsync` aborts bring-up and releases `_lifecycleGate`.
  - Add unit tests verifying that startup failure calls `TeardownInternal()` and clears firewall rules.

## How

1. Commit approved phase brief and verify clean baseline on `origin/main` in PR CI.
2. Implement linked CTS and failure teardown in `VPNRouter.Core/Services/VpnEngine.cs`.
3. Add unit tests in `VPNRouter.Tests/VpnEngineStartAsyncSeamTests.cs`.
4. Run independent adversarial review to verify that happy-path connect, failover restart, and disconnect semantics are preserved.
5. Verify clean build and all test suites on Ubuntu and Windows in GitHub Actions.

### Tests written

- `StartAsync_StopCalledDuringBringUp_AbortsImmediatelyAndReleasesGate`
- `StartAsync_PhaseFailure_TearsDownFirewallRulesAndState`

### Verification approach

Run focused `VpnEngine` lifecycle tests, full discovered test suites on Ubuntu and Windows. GitHub Actions is the mechanical oracle.

## Verification gate

- [x] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors in PR workflow `33680869292`.
- [x] **Gate 2 — Tests green**: baseline `2830 total / 2773 executed` became `2833 total / 2776 executed`, all passed with zero errors and zero warnings; Windows characterization passed `19/19` with zero failures.
- [x] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [x] **Gate 4 — Self-review**: independent Opus review verified cancellation linkage, failure teardown, and safe disposal without double-free or object disposal leaks.
- [x] **Gate 5 — UI verify**: N/A (Core lifecycle change; UI surface untouched).
- [x] **Gate 6 — Characterization diff**: all existing lifecycle seam and characterization tests continue to pass.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR #216 remains open and unmerged
**Commits**: `5a5904d8` (brief); `06e6f00b` (lifecycle safety fix); `f75ef4c1` (key alignment); `fb623942` (dummy binary fixture); `36855a8d` (using directive)
**Pushed**: `origin/dsh/fix-vpnengine-lifecycle`; PR #216 — https://github.com/PavelLizunov/VPNRouter/pull/216
**Test deltas**: +3 unit tests in `VPNRouter.Tests/VpnEngineStartAsyncSeamTests.cs` (`2833 total / 2776 executed / 2776 passed / 0 failed / 0 warning`); Windows characterization `19/19 passed`
**Files changed**:
- `VPNRouter.Core/Services/VpnEngine.cs`: linked `_sessionCts` via `CreateLinkedTokenSource(ct)` in `StartAsync`, teardown on failure if `!IsRunning`, symmetric `_slipstream?.Dispose()` in `TeardownInternal`, and cleanup in `Dispose()` and pre-start failover restart.
- `VPNRouter.Tests/VpnEngineStartAsyncSeamTests.cs`: added 3 unit tests verifying cancellation abort and teardown cleanup on failure.
- `plans/phase-fix-vpnengine-lifecycle-2026-09-02.md`: this phase brief and outcome record.

**Gate results**: All 6 gates passed in workflow `33680869292`.

**Surprises encountered**:
- During testing, Phase 6 `DeploySingBoxBinary` required ensuring a dummy binary file existed at `SingBoxExePath` so the pipeline reaches the firewall setup and teardown phase without throwing an earlier `FileNotFoundException`.

**Follow-ups spawned**: Remaining Category 1 packets (Packet 2: `SingBoxManager` exit code handling and `HealthMonitor` counter reset; Packet 3: `CustomConfigInjector` rule leaks) are ready for subsequent implementation branches.
**Lessons for methodology doc**: Asynchronous startup sequences must always link the outer session cancellation token with caller tokens, and ensure that failure at any intermediate pipeline stage guarantees complete teardown of already-allocated OS packet filter rules.
