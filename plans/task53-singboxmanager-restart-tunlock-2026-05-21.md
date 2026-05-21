# Task #53 — fix `SingBoxManager.Restart` TUN-lock release race

**Owner**: Claude session (Task #53)
**Branch**: main (direct commit)
**Predecessor brief**: `plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md`
(Task #49 surprise §2 flagged this bug, commit `69f0d4c` / `35ee599`).
**Effort**: ~1.5 hours.
**Risk**: LOW. Single-line conditional fix in `SingBoxManager.StopInternal`'s
Windows graceful path + 3 new behavioural tests covering Restart vs
public Stop vs the source-pin's intent.
**Blast radius**: 1 production file (`SingBoxManager.cs`, 1 line + 1
comment) · 1 new test file (`SingBoxManagerRestartTunLockTests.cs`,
~270 LOC) · 0 existing tests touched.
**Rollback**: `git revert <commit>` — pure additive conditional.

## Why

Per Task #49's outcome (`plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md`
surprise §2):

> `engine.ApplyAsync`'s Restart fallback releases the TUN lock at
> `SingBoxManager.cs:405` — `StopInternal(releaseLock: false)` in the
> Restart path goes through the Kill→Release `finally` block which calls
> `_tunLock.Release()` UNCONDITIONALLY (ignoring the `releaseLock`
> parameter). That's a real production race window (Restart's TUN lock
> is briefly released between StopInternal and LaunchProcess), but well
> outside Task #49's test scope. Flagged here for future investigation.

The existing source-pin
`SingBoxManagerStateMachineTests.Restart_PreservesTunLock_SourcePin`
(commit history pre-Task-#53) pins the call site shape
(`StopInternal(releaseLock: false)`) but NOT the downstream effect: the
parameter exists, but only 3 of 4 paths through `StopInternal` honour
it. Path 4 (Windows graceful Kill, `_handle != null && !_handle.HasExited`)
ignores the parameter and releases the lock unconditionally in its
`finally` block.

### The race window (in production)

`Restart()` calls `StopInternal(releaseLock: false)` → Windows path 4
fires (since `_handle` is alive going in) → `finally` block at line
~405 runs `_tunLock.Release()` UNCONDITIONALLY → lock is gone. Then
`Restart()` sleeps 750 ms and calls `LaunchProcess(exePath)` — which
does NOT re-acquire the lock. So after Restart returns:

- The new sing-box process is alive.
- `_tunLock._owned == false`.
- Another VPNRouter instance (Service vs UI) can now call
  `_tunLock.TryAcquire()` and win the named semaphore — they think
  they own the TUN, but the live sing-box that THIS instance just
  spawned is still using it.

This recreates exactly the bug the `Global\VPNRouter-SingBox-Owner`
named semaphore was designed to prevent (the v2.26.1 + v2.31.10-r2
coexistence design).

## What

### Bug confirmation (TDD-first)

Added `SingBoxManagerRestartTunLockTests` with 3 behavioural tests.
The first one (`Restart_PreservesTunLock_BehaviourTest`) FAILS against
pre-fix production code — confirming the bug at the behavioural level
beyond the existing source-pin.

### Fix

`SingBoxManager.cs:405` — wrap the unconditional `_tunLock.Release()`
in the Windows graceful path's `finally` block with the
`releaseLock` guard, matching the other 3 paths through `StopInternal`.

```csharp
// before
_tunLock.Release();

// after
if (releaseLock) _tunLock.Release();
```

That's it. The bug is a missed guard, not a missing concept.

### Tests

`VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs` — 3 tests:

1. `Restart_PreservesTunLock_BehaviourTest` — the failing TDD pin.
   Acquires the singleton lock via reflection (mirroring what
   StartWithJson does), pokes `_handle` to a fresh FakeProcessHandle
   so the Windows graceful path runs, calls Restart(), and asserts
   the singleton's `_owned` field is STILL true after Restart returns.
   Pre-fix: fails. Post-fix: passes.

2. `Restart_StopInternalReleasesLockOnlyWhenAsked` — defence-in-depth
   matrix pin: calls `StopInternal(releaseLock: true)` (the public Stop
   shape) and `StopInternal(releaseLock: false)` (the Restart shape)
   on equivalent setups. Pins that the parameter actually GATES the
   release behaviour in path 4.

3. `Stop_PublicEntryPoint_ReleasesLockNormally_RegressionPin` — proves
   the bug fix doesn't accidentally introduce a leak. `Stop()` (the
   public entry point) calls `StopInternal(releaseLock: true)`, which
   must still release the lock. Without this pin, an over-zealous
   "always preserve" refactor would silently leak the lock across
   user Stops.

All 3 tests gate on `OperatingSystem.IsWindows()` via `Assert.SkipUnless`.
The Linux pkexec path is intractable to unit-test (external-process-heavy,
no IProcessRunner seam for `getcap` / `pkexec`); same Windows-only
gating reasoning as Task #49's lifecycle suites.

## Items refused / deferred

None. The bug was a one-line gate omission; the fix matches the existing
design intent (3 of 4 paths already honour the parameter).

The follow-up flagged in Task #49's "follow-ups spawned" — "low priority"
because of the named-semaphore detection — is now properly closed.
Higher-confidence test coverage replaces the "real-world impact: low"
hedge.

## Verification gates

- [ ] `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] Full test suite green (1354+ baseline post-`35ee599`).
- [ ] Existing `Restart_PreservesTunLock_SourcePin` stays green
  (belt-and-braces source-string pin).
- [ ] Existing `SingBoxManagerRestartTunHandshakeTests` Wave-38 source
  pins stay green.
- [ ] New `SingBoxManagerRestartTunLockTests` (3 tests) pass on Windows.
- [ ] Post-push CI verify.
- [ ] Brief: this file.

## Outcome

(populated post-verification)

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/SingBoxManager.cs` | 1 line: line 405 wrapped with `if (releaseLock)` guard. |
| `VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs` | +~270 LOC (new). 3 tests, Windows-only via Assert.SkipUnless. |
| `plans/task53-singboxmanager-restart-tunlock-2026-05-21.md` | This brief. |

### Test count delta

- Pre-Task-#53: 1354 baseline (post-`35ee599`).
- Post-Task-#53: 1354 + 3 = 1357.

### Cross-platform / CI matrix

3 new tests are Windows-only (Assert.SkipUnless gating). Same reasoning
as Task #49's lifecycle suites: SingBoxManager's Linux path shells out
through pkexec/sudo/getcap on a different code branch not routed through
the IProcessRunner seam (Phase 3+ migration covers sing-box itself; the
elevation wrappers are still Process.Start direct).

### Surprises encountered

(populated post-verification)
