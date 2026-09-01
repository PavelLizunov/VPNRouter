# Phase: Compensate failed update snapshot restore

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/compensate-update-restore`
Audit ID: `SU-1-1`

## 1. Intent & Invariants

- **What:** If replacement of `app.bak/` into `app/` fails after the current app was staged aside, restore the staged current app instead of leaving the installation without `app/`.
- **Invariants:** A failed restore never consumes the snapshot; a persisted recovery stage remains usable across retries; create/restore/delete are serialized by one install-scoped cross-process file lock; lock contention defers startup repair; delayed cleanup can delete only the generation observed by that healthy launch; compensation runs only when a current tree was staged and `app/` is absent; successful restore behavior and snapshot integrity checks remain unchanged; no merge/release/tag/deploy/install occurs.

## 2. Interface / Data Contract

```csharp
public static RestoreResult RestoreSnapshot(string installDir);
internal static RestoreResult RestoreSnapshot(
    string installDir,
    Action<string, string> moveDirectory); // deterministic fault seam
public static string? GetSnapshotGeneration(string installDir);
public static bool DeleteSnapshot(string installDir, string expectedGeneration);

// second move fails + compensation succeeds:
// Restored=false, original app/ restored, app.bak/ retained, app.bak.tmp absent
// compensation also fails:
// Restored=false, diagnostic includes both failures, staging retained for recovery
// concurrent create/restore/delete while .update-backup.lock is held => fail safely
// verified lock contention => OperationInProgress=true; startup returns before SelfRepair
// permission/path/general I/O lock failure => OperationInProgress=false; fallback remains available
// stale expectedGeneration => no deletion
```

## 3. Verification Checklist (Definition of Done)

- [ ] Happy path and existing idempotency tests remain green.
- [ ] Forced snapshot-to-app move failure restores the staged current app.
- [ ] Forced compensation failure preserves staging and reports both errors; another failed retry restores that stage.
- [ ] A held install lock makes create/restore/delete fail without mutating any tree or launching startup SelfRepair.
- [ ] A non-contention lock acquisition error is not mislabeled busy, so fallback remains available.
- [ ] Stale delayed cleanup cannot delete a replacement snapshot generation.
- [ ] Missing, empty, malformed, whitespace-padded, truncated, or oversized generation sidecars fail closed and repair under lock.
- [ ] Cleanup refuses to delete the only recovery stage when `app/` is absent.
- [ ] Focused `UpdateBackupTests` pass in designated CI.
- [ ] Full discovered suite and exact-head CI pass.
- [ ] Independent correctness/fault-injection/test reviews have no surviving P0/P1.

## Risk / rollback

- Risk: an over-broad compensation branch could overwrite a destination produced by another recovery actor, while background snapshot cleanup can otherwise delete a live restore stage.
- Control: all mutating snapshot operations hold `.update-backup.lock`; compensate only when this call or a prior interrupted restore staged a current tree, destination `app/` is absent, and stage still exists; never overwrite.
- Rollback: revert the task PR; there is no schema or migration change.

## Six gates

1. **Scope:** `UpdateBackup`, startup fallback/cleanup wiring, its tests, ledger, and this brief only.
2. **Ownership:** one install-scoped cross-process file lock guards create, restore, compensation, and delete.
3. **Data safety:** no recovery-stage deletion before one usable `app/` tree exists; cleanup is generation-bound.
4. **Tests:** second-move, compensation, retry, lock-contention, and stale-cleanup failures are deterministic.
5. **Review:** independent correctness, recovery, and test lenses; lead source-verifies claims.
6. **Handoff:** scoped commits, PR, exact-head green; owner alone decides merge/release.

## Outcome

Pending implementation and verification.
