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

- [ ] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors.
- [ ] **Gate 2 — Tests green**: all unit and characterization tests pass with zero failures.
- [ ] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [ ] **Gate 4 — Self-review**: adversarial review confirms cancellation linkage and exception safety without regressions.
- [ ] **Gate 5 — UI verify**: N/A (Core lifecycle change; UI surface untouched).
- [ ] **Gate 6 — Characterization diff**: existing lifecycle characterizations continue to pass.

## Outcome

**Status**: IN PROGRESS
**Commits**: brief commit pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results**: pending.
**Surprises encountered**: pending.
**Follow-ups spawned**: pending.
**Lessons for methodology doc**: pending.
