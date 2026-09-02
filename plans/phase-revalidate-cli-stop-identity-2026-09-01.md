# Phase: Revalidate CLI stop process identity

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/revalidate-cli-stop-identity`
Audit ID: `SU-3-1`

## 1. Intent & Invariants

- **What:** prevent `VPNRouter.CLI stop` from killing a process that reused the recorded sing-box PID while the command waited for graceful owner shutdown.
- **Invariants:** the audited direct destructive fallback requires the same trusted `PID + UTC start ticks + executable path` captured before the wait; a gone process is treated as stopped; a changed, unreadable, or no-longer-owned fallback target returns before `Kill` and before the fallback cleanup; successful owner-event shutdown and ordinary same-process fallback behavior remain unchanged; no state schema migration; no merge/release/tag/deploy/install.
- **Separated follow-ups:** run-generation-bound IPC/atomic state compare-delete and Unix PID-stable signaling are broader pre-existing contracts recorded in `plans/OPEN-DEFECTS.md`; this PR does not claim to close them.

## 2. Interface / Data Contract

```csharp
ProcessOwnership.TryReadOwnedSingBoxIdentity(process)
    -> OwnedProcessIdentity? // one coherent trusted snapshot

ProcessOwnership.IsSameProcessIdentity(expected, current)
    -> expected.Pid == current.Pid
       && expected.StartedAtUtcTicks == current.StartedAtUtcTicks
       && IsSamePath(expected.ExecutablePath, current.ExecutablePath)

StopCommand:
  capture expected identity -> owner-event wait -> reopen PID
  -> pin native Windows SafeHandle -> capture current owned identity
  -> exact identity compare -> Kill while handle remains alive
```

## 3. Verification Checklist (Definition of Done)

- [x] The initial gate and post-wait gate both consume one coherent owned identity snapshot.
- [x] Same PID with different start ticks is rejected.
- [x] Same PID/start with a different executable path is rejected.
- [x] Equivalent Windows path casing remains accepted through existing path semantics.
- [x] A no-longer-owned process is rejected immediately before `Kill`.
- [x] Windows retains one native process handle from the fresh identity snapshot through `Kill`, preventing PID recycling in the final gap.
- [x] A controlled Windows child-process characterization executes fresh lookup -> SafeHandle pin -> identity compare -> Kill/wait -> handle keepalive.
- [x] A process gone during either identity read is treated as already stopped and never receives `Kill`.
- [x] A changed/unreadable fallback target and a post-`Kill` exit timeout return failure before fallback state cleanup.
- [x] Source ordering pins revalidation after the wait and before the only legacy fallback `Kill`.
- [x] Focused and full exact-head CI pass; independent correctness/race/test reviews are clean.

## Risk / rollback

- Risk: an overly strict comparison can leave stale state after a legitimate process transition.
- Control: only the direct destructive fallback is gated; already-gone paths still clean up, while ambiguous identity changes deliberately require a fresh status/stop attempt.
- Rollback: revert this task PR; no persisted shape changes.

## Six gates

1. **Scope:** existing ownership primitive, CLI stop fallback, focused contracts, ledger, brief.
2. **Identity:** PID alone is never destructive authority after waiting.
3. **TOCTOU:** current trusted identity is captured from the fresh process handle immediately before kill.
4. **Compatibility:** owner-event graceful shutdown and same-process fallback remain unchanged.
5. **Review:** independent identity, race, and false-pass lenses; lead source-verifies claims.
6. **Handoff:** scoped commits, PR, exact-head green; owner alone decides merge/release.

## Primary sources

- [.NET 10 `Process.StartTime`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.starttime?view=net-10.0): exposes process creation time; on Unix it is cached on first access and can become unavailable after exit, so unreadable snapshots fail closed.
- [.NET 10 `Process.SafeHandle`](https://source.dot.net/System.Diagnostics.Process/System/Diagnostics/Process.cs.html): `GetOrOpenProcessHandle` opens and caches one long-term all-access handle; the fallback obtains it before verification and retains it through termination.
- [Win32 `PROCESS_INFORMATION`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_information): a PID cannot be reused until every handle to its process object is closed.
- [.NET 10 `Process.Kill`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0): callers should wait for exit after termination; the fallback now treats an unconfirmed five-second exit as failure.

## Outcome

**PASS / review-ready in PR #211.** Product head `444825353b73545239eee1dbcce8acdda48faef0` closes the scoped Windows `SU-3-1` post-wait PID-reuse gap.

- **Delta:** `StopCommand` now captures a trusted child identity before the owner wait, opens and pins one fresh Windows native process handle after the wait, re-reads exact ownership, compares PID/start/path, and keeps that handle live through the sole fallback `Kill` and bounded wait. `ProcessOwnership` exposes the coherent snapshot and exact comparator without changing existing ownership rules.
- **Tests:** pure identity contracts reject PID/start/path drift and pin platform path semantics; the source guard enforces the complete production order and one-Kill contract; a controlled Windows `PING.EXE` child characterizes fresh lookup -> `SafeHandle` -> identity -> `Kill(true)` -> wait -> open-handle keepalive with bounded cleanup.
- **Exact-head evidence:** workflow-dispatch run `33571444366` checked out exact SHA `444825353b73545239eee1dbcce8acdda48faef0`. Attempts 1 and 2 both passed: full discovered suite 2,832 total / 2,775 passed / 57 skipped; Windows characterization 20/20; Windows Go gate passed. Final PR merge-ref checks also passed.
- **Review:** three independent identity/race/false-pass reviews are CLEAN after repairs. Fixed: gone-during-read handling, incomplete source ordering, missing Unix case inverse, unpinned Windows comparison-to-Kill gap, and observable controlled-handle lifetime. The fixture's production-binding concern is covered by the separate exact source-order contract; descendant-tree behavior is outside this root-process identity finding.
- **QA:** Ouroboros session `qa-eb79c868` iteration 2 PASS, `0.86` (iteration 1 was `0.78` and drove the controlled Windows fixture plus repeated exact-head evidence).
- **Separated confirmed follow-ups:** CLI run-generation-bound IPC/atomic state compare-delete and Unix PID-stable signaling remain open in `plans/OPEN-DEFECTS.md` for separate branches/PRs; this task does not overstate their closure.
- **Limitations/safety:** local .NET is unavailable, so GitHub Actions is the mechanical oracle. Verification terminated only the child process created by the controlled test. No VPN process, merge, release, tag, deployment, or install occurred.
- **Rollback:** revert PR #211; no state schema or persisted data shape changed.
