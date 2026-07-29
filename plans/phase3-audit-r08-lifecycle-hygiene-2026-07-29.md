# Phase 3 — R08 — TUN ownership lock handle-churn hygiene (LIFE-1 residual only)

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r08-lifecycle-hygiene-2026-07-29`
**Base**: `origin/main` (verified: P02 `codex/qwen-audit-p02-failover-wiring-2026-07-29` touched ONLY `VpnEngine.cs`; `TunOwnershipLock.cs` and the `SingBoxManager.cs` caller are not modified by P02)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R08); prompt pool P02
**IDs**: LIFE-1 (P3 residual ONLY)
**Effort**: ~45 min (or close with no code — see §4 option B)
**Risk**: LOW (hygiene; the named semaphore is already released every cycle)
**Blast radius**: `VPNRouter.Core/Services/TunOwnershipLock.cs` (+ optional cheap test) · ~+15 LOC · runtime: handle re-arm consistency after dispose
**Rollback**: `git revert <commit>` / delete branch / or close brief with no code

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| LIFE-1 | P1 | REFUTED (P1 form) | P3 | High |

**The original P1 claim is REFUTED and MUST NOT be fixed.** The claim ("2nd
Dispose returns early, never releases the named semaphore, blocks other
processes") is impossible:

- `Release()` (`:108-127`) gates on `_owned`/`_semaphore`, NOT `_disposed`.
- `Dispose()` (`:129-143`) itself calls `Release()` BEFORE disposing the handle,
  then disposes+nulls the semaphore and clears `_instance`.
- `Instance()` (`:53-61`) recreates the singleton when `_instance is null ||
  _instance._disposed`.
- Caller order: `SingBoxManager.cs:340` `Stop()` (→ `StopInternal(releaseLock:true)`
  → `_tunLock.Release()`) runs before `:348` `_tunLock.Dispose()`.

The named semaphore count is restored on every cycle. **No code change is required
for the claimed defect.**

Corrected scope (residual ONLY): in a shared-singleton edge, `TryAcquire` (`:80`)
can recreate `_semaphore` after the instance is disposed, and that recreated
handle is never disposed because `Dispose` early-returns at `:135`
(`if (_disposed) return;`). This is a **handle churn**, NOT a semaphore hold —
P3 hygiene.

## 2. Verified current root cause (commit `b39a28c3`)

`VPNRouter.Core/Services/TunOwnershipLock.cs`:

- `TryAcquire` (`:72-107`): `if (_owned) return true;` then unconditionally
  `_semaphore = new Semaphore(1, 1, MutexName, out _);` (`:80`). There is no guard
  against re-arming after `_disposed`.
- `Dispose` (`:129-143`): `if (_disposed) return;` (`:135`) → a second dispose, or
  any dispose after `_disposed=true`, is a no-op; a semaphore recreated by a later
  `TryAcquire` is therefore never disposed.
- `Instance` (`:53-61`): normally hands out a FRESH instance once `_disposed` is
  set, so the common path is clean. The churn is confined to the edge where the
  same disposed instance is reused and `TryAcquire` re-arms.

## 3. Why

The residual is a minor lifecycle inconsistency: a re-armed `Semaphore` handle on
a disposed instance escapes deterministic disposal (it is finalizer-closed). It is
not a semaphore hold and not cross-process. This brief exists to (a) record the
refutation authoritatively and (b) optionally make `Dispose`/`TryAcquire` re-arm
consistent so no handle churns.

## 4. What

Two acceptable outcomes — pick the smallest defensible one:

- **Option A (preferred if cheap)**: make re-arm consistent. Either (a) guard
  `TryAcquire` so it does not recreate `_semaphore` when `_disposed` (return the
  fail-open path), or (b) make `Dispose` dispose whatever `_semaphore` currently
  is and have `TryAcquire` reset `_disposed` semantics consistently. Exactly-once
  disposal must be preserved.
- **Option B (close with no code)**: if the residual is judged not worth a change
  (the singleton is recreated on the normal path; the churn is a single
  finalizer-closed handle in a rare edge), document that decision in the Outcome
  and close the brief WITHOUT a product change. This is an acceptable P3 outcome.

```diff
  public bool TryAcquire()
  {
      if (_owned) return true;
+     if (_disposed) return true;   // do not re-arm a handle on a disposed instance (fail-open)
      try { _semaphore = new Semaphore(1, 1, MutexName, out _); }
      ...
```

## 5. How (ordered minimal steps)

1. Re-verify the refutation end-to-end (Stop → Release → Dispose → Instance
   recreate) in ALL `SingBoxManager.StopInternal` exit paths before touching
   anything. If any path actually holds the semaphore across dispose, STOP and
   escalate (that would be a different, real defect).
2. Decide Option A vs B. Default to B unless the re-arm edge is reachable in a
   normal flow.
3. If Option A: add the `_disposed` guard (or equivalent) preserving fail-open and
   exactly-once disposal.
4. If a test is cheap and genuinely useful, add the lifecycle test below; otherwise
   skip (do not write a test purely to justify a no-op).

### Tests written (only if Option A is taken and the test is cheap)

- `TunOwnershipLockTests.AcquireDisposeAcquireDispose_ReleasesSemaphore` — two
  acquire/stop/dispose cycles release the named semaphore and leave no dangling
  handle (assert the semaphore is re-acquirable by a fresh instance).
- `TunOwnershipLockTests.TryAcquireAfterDispose_DoesNotLeakRearmedHandle` — fails
  on old code if Option A's guard is omitted.

### Verification approach

Pure lifecycle assertions using the named semaphore (no live VPN). Execution in
remote GitHub CI. If Option B, no test is added and the brief closes on the
refutation proof.

## 6. Affected callers / consumers + invariants

- Consumers: `SingBoxManager` (`Stop`/`StopInternal`/`Dispose`), `Instance()`
  callers. Invariant: the named semaphore is STILL released every cycle (the
  refuted behavior must not change); fail-open on semaphore-creation failure is
  preserved; exactly-once disposal preserved.
- Do NOT change `Release()` gating or the `Instance()` recreate condition.

## 7. Exact expected file list

- `VPNRouter.Core/Services/TunOwnershipLock.cs` (Option A only)
- `VPNRouter.Tests/TunOwnershipLockTests.cs` (Option A only; or existing lifecycle test file)
- (Option B: this brief only — no product file changes)

## 8. Non-goals

- Do NOT "fix" the semaphore-block / cross-process-brick claim — it is REFUTED.
- Do NOT introduce a second lifecycle coordinator or a new locking abstraction.
- Do NOT change `SingBoxManager` stop/dispose ordering (it is correct).
- Do NOT run any live VPN/service/process (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Concurrency**: the only real risk is introducing a double-dispose or breaking
  the release-before-dispose ordering. Preserve `lock (InstanceGate)` around
  dispose and the fail-open semantics.
- **Security / data-loss**: none.
- **Platform**: named semaphore is Windows-centric; the guard is platform-neutral.

## 10. Dependencies / overlaps

- No P1 branch touches `TunOwnershipLock.cs` → base `origin/main`.
- P02 (FAIL-1) touched `VpnEngine.cs` only; `SingBoxManager.cs` (the caller) is
  unmodified, so no rebase onto P02 is needed.
- Independent of all other R-packages.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors (Option A) / N/A (Option B).
- [ ] Gate 2 — Tests green (remote CI): lifecycle test passes (Option A) / N/A (Option B).
- [ ] Gate 3 — Docs: brief Outcome filled with the refutation proof and the A/B decision.
- [ ] Gate 4 — Self-review: confirm release-before-dispose ordering unchanged (static).
- [ ] Gate 5 — MCP verify: N/A (Core lifecycle hygiene).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Decision**: PENDING (Option A code fix / Option B close-with-no-code)
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — Core lifecycle hygiene
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R08 branch, or delete
`codex/qwen-audit-r08-lifecycle-hygiene-2026-07-29`. Option B has no code to roll
back. The lock reverts to prior re-arm behavior; the named semaphore release path
is untouched either way.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase3-audit-r08-lifecycle-hygiene-2026-07-29.md через Qwen
Code. ID: LIFE-1 (P3, residual handle-churn ONLY). Base branch: origin/main.
ВАЖНО: исходный P1 claim (semaphore block / cross-process brick) ОПРОВЕРГНУТ —
НЕ исправляй его. Сначала прочитай brief целиком, AGENTS.md, plans/CLAUDE.md и
VPNRouter.Core/CLAUDE.md. Scope строго ограничен P3 handle-churn: сделай
TunOwnershipLock Dispose/TryAcquire re-arm согласованным, чтобы пересозданный
после dispose handle отслеживался и освобождался. Если residual признан не
стоящим изменения — закрой brief с обоснованием без кода. Не создавай второй
lifecycle coordinator. Напиши дешёвый lifecycle тест (два acquire/stop/dispose
cycle) только если он действительно полезен. НЕ запускай локальные
build/test/app/binary/service, не делай live мутаций. Только чтение/поиск/
редактирование и запись тестов. Commit/push/CI делает orchestrator. Без
release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
