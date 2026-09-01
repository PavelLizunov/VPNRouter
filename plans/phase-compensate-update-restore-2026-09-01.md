# Phase: Compensate failed update snapshot restore

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/compensate-update-restore`
Audit ID: `SU-1-1`

## 1. Intent & Invariants

- **What:** If replacement of `app.bak/` into `app/` fails after the current app was staged aside, restore the staged current app instead of leaving the installation without `app/`.
- **Invariants:** A failed restore never consumes the snapshot; compensation runs only when this call moved `app/` to staging and `app/` is absent; successful restore behavior and snapshot integrity checks remain unchanged; no merge/release/tag/deploy/install occurs.

## 2. Interface / Data Contract

```csharp
public static RestoreResult RestoreSnapshot(string installDir);
internal static RestoreResult RestoreSnapshot(
    string installDir,
    Action<string, string> moveDirectory); // deterministic fault seam

// second move fails + compensation succeeds:
// Restored=false, original app/ restored, app.bak/ retained, app.bak.tmp absent
// compensation also fails:
// Restored=false, diagnostic includes both failures, staging retained for recovery
```

## 3. Verification Checklist (Definition of Done)

- [ ] Happy path and existing idempotency tests remain green.
- [ ] Forced snapshot-to-app move failure restores the staged current app.
- [ ] Forced compensation failure preserves staging and reports both errors.
- [ ] Focused `UpdateBackupTests` pass in designated CI.
- [ ] Full discovered suite and exact-head CI pass.
- [ ] Independent correctness/fault-injection/test reviews have no surviving P0/P1.

## Risk / rollback

- Risk: an over-broad compensation branch could overwrite a destination produced by another recovery actor.
- Control: compensate only when this call completed the first move, destination `app/` is absent, and stage still exists; never overwrite.
- Rollback: revert the task PR; there is no schema or migration change.

## Six gates

1. **Scope:** `UpdateBackup`, its tests, ledger, and this brief only.
2. **Ownership:** compensation is guarded by per-call move state and filesystem existence.
3. **Data safety:** no deletion before one usable `app/` tree exists.
4. **Tests:** both second-move and compensation failures are deterministic.
5. **Review:** independent correctness, recovery, and test lenses; lead source-verifies claims.
6. **Handoff:** scoped commits, PR, exact-head green; owner alone decides merge/release.

## Outcome

Pending implementation and verification.
