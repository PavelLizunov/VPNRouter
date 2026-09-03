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

- [x] Happy path and existing idempotency tests remain green.
- [x] Forced snapshot-to-app move failure restores the staged current app.
- [x] Forced compensation failure preserves staging and reports both errors; another failed retry restores that stage.
- [x] A held install lock makes create/restore/delete fail without mutating any tree or launching startup SelfRepair.
- [x] A non-contention lock acquisition error is not mislabeled busy, so fallback remains available.
- [x] Stale delayed cleanup cannot delete a replacement snapshot generation.
- [x] Missing, empty, malformed, whitespace-padded, truncated, or oversized generation sidecars fail closed and repair under lock.
- [x] Cleanup refuses to delete the only recovery stage when `app/` is absent.
- [x] Focused `UpdateBackupTests` pass in designated CI.
- [x] Full discovered suite and exact-head CI pass.
- [x] Independent correctness/fault-injection/test reviews have no surviving P0/P1.

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

- Implementation commits: `18e7776642e51885bd45428d2b01c7beb3fb9ca5` and `9531c04214fcf62c8d9ce60c6315861360e79752`; PR: #206.
- Scope: `UpdateBackup`, startup recovery/cleanup wiring, deterministic contracts, defect ledger, and this brief (`+576/-27` over base at implementation head).
- Adversarial repair rounds additionally closed six source-verified races beyond the original missing compensation: concurrent cleanup deleting a live stage, retry deleting a persisted stage, busy restore launching concurrent SelfRepair, stale delayed cleanup deleting a replacement snapshot, positional-record API breakage, and permission/I/O lock errors being mislabeled contention. Malformed generation sidecars now fail closed and repair under lock.
- Primary platform contracts: [.NET `FileShare.None`](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0) rejects same- or cross-process opens until close; [.NET `Directory.Move`](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.move?view=net-10.0) fails when the destination exists, matching the compensation seam.
- Exact implementation-head CI passed twice without flake: each run had 2,838 total tests, 2,781 passed, 57 skipped; `characterization-windows` and `go-test-windows` passed twice; `grep` passed. The local control plane has no `dotnet`, so GitHub Actions was the build/test oracle.
- Three independent final correctness/race/test reviews returned CLEAN. Ouroboros QA session `qa-6ab0319f` passed the approved data-safety ACs at `0.95` after malformed-input and repeated-run evidence.
- Rollback is a plain PR revert; the empty lock and generation sidecars require no migration. No release, tag, merge, deploy, or install occurred.
