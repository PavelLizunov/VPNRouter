# Phase: Revalidate CLI stop process identity

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/revalidate-cli-stop-identity`
Audit ID: `SU-3-1`

## 1. Intent & Invariants

- **What:** prevent `VPNRouter.CLI stop` from killing a process that reused the recorded sing-box PID while the command waited for graceful owner shutdown.
- **Invariants:** destructive fallback requires the same trusted `PID + UTC start ticks + executable path` captured before the wait; a gone process is treated as stopped; a changed, unreadable, or no-longer-owned process fails closed without `Kill` or state-file deletion; successful owner-event shutdown and ordinary same-process fallback behavior remain unchanged; no state schema migration; no merge/release/tag/deploy/install.

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
  -> capture current owned identity -> exact identity compare -> Kill
```

## 3. Verification Checklist (Definition of Done)

- [ ] The initial gate and post-wait gate both consume one coherent owned identity snapshot.
- [ ] Same PID with different start ticks is rejected.
- [ ] Same PID/start with a different executable path is rejected.
- [ ] Equivalent Windows path casing remains accepted through existing path semantics.
- [ ] A no-longer-owned process is rejected immediately before `Kill`.
- [ ] A process gone before or after the wait is treated as already stopped and state cleanup remains safe.
- [ ] Source ordering pins revalidation after the wait and before the only legacy fallback `Kill`.
- [ ] Focused and full exact-head CI pass; independent correctness/race/test reviews are clean.

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

## Outcome

Pending implementation and verification.
