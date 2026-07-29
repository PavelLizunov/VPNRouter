# Phase 2 — R07 — Emergency-channel disposal + wgturn atomic replacement

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r07-updater-resources-2026-07-29`
**Base**: `origin/main`. INSPECTED overlap with P10: the P10 branch (`codex/qwen-audit-p10-zapret-atomicity-2026-07-29`, ZAP-1) touched ONLY `VPNRouter.Core/Services/ZapretUpdater.cs` (+29/-8). ZAP-2 lives in `EmergencyChannel/EmergencyChannelManager.cs` + `EmergencyChannelEngine.cs`; ZAP-3 lives in `WgturnUpdater.cs`. **No file overlap → base is `origin/main`.**
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R07); prompt pool P10
**IDs**: ZAP-2, ZAP-3
**Effort**: ~2 h
**Risk**: MEDIUM (ZAP-2 is a bounded per-cycle handle leak; ZAP-3 can destroy the only working wgturn-cli binary)
**Blast radius**: `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs`, `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelEngine.cs`, `VPNRouter.Core/Services/WgturnUpdater.cs`, tests · ~+90 LOC · runtime: emergency-channel process/manager disposal + wgturn-cli binary replacement
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| ZAP-2 | P2 | CONFIRMED | P2 | High |
| ZAP-3 | P2 | CONFIRMED | P2 | High |

Corrected scope:

- **ZAP-2**: bounded per-cycle leak of a small handle set (one undisposed
  `Process` + manager per crash→reconnect cycle); the engine is long-lived
  (Dispose only at teardown). P2.
- **ZAP-3 wording note**: `tempBin` is the NEW download, not a backup of the
  original — but the destructive outcome matches the claim (delete working binary,
  move throws, `finally` deletes the only remaining copy → no binary, no recovery).

## 2. Verified current root cause (commit `b39a28c3`)

### ZAP-2 — exited Process / crashed manager never disposed

`VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs`:

- `:188` `_process` is overwritten in `LaunchProcess` without disposing the prior
  instance (verified: `_process = new Process { StartInfo = psi, ... }`).
- `:208-225` `OnProcessExited` sets `State = Failed`, fires `Crashed`, and closes
  the log writer — but does NOT dispose or null `_process`.
- `:131-135` `Stop()` early-returns (skipping dispose) when `HasExited`.

`VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelEngine.cs`:

- `:115` `_manager = _managerFactory()` creates a new manager each `StartAsync`.
- `:104-109` the Stop-first guard fires ONLY for `Connecting/Connected`.
- `:187-193` `OnManagerCrashed` sets `State = Failed` and fires `ErrorOccurred`
  WITHOUT disposing or nulling `_manager` (verified).
- Consequence: after a crash (`State = Failed`), a reconnect via `StartAsync`
  skips the Stop-first guard (state is `Failed`, not `Connecting/Connected`) and
  overwrites `_manager` → the crashed manager (and its exited `Process`) is
  orphaned undisposed. One undisposed `Process` handle (+ manager) leaks per
  crash→reconnect cycle.

### ZAP-3 — destructive delete-first binary replacement

`VPNRouter.Core/Services/WgturnUpdater.cs` (verified `:421-481`):

```csharp
if (File.Exists(CliExePath))
{
    File.Delete(CliExePath);          // :427 — deletes the WORKING binary
}
File.Move(tempBin, CliExePath);       // :429 — if this throws, binary is gone
...
finally
{
    try { if (File.Exists(tempBin)) File.Delete(tempBin); } catch { }   // :479-481 — deletes the only remaining copy
}
```

If the delete succeeds but the move throws, the working binary is gone with no
replacement, and the `finally` then deletes `tempBin` (the only remaining copy) →
no wgturn-cli binary and no recovery copy.

## 3. Why

ZAP-2 leaks a small handle set on every crash→reconnect cycle of a long-lived
engine. ZAP-3 can leave the user with no wgturn-cli binary and no recovery copy if
the replacement move fails (e.g. AV/in-use file). Both have existing in-repo
patterns (atomic replace; dispose-before-reassign).

## 4. What

1. **ZAP-2**: dispose and null the prior `_process` before reassignment in
   `LaunchProcess`; dispose the exited process in `OnProcessExited` (after reading
   the exit code); at the engine level, dispose+null the crashed manager in
   `OnManagerCrashed` (or make the `StartAsync` Stop-first guard also cover
   `Failed`). Ensure exactly-once disposal (no double-dispose).
2. **ZAP-3**: stage the new binary and atomically replace with
   `File.Move(tempBin, CliExePath, overwrite:true)` — NO destructive delete-first.
   Keep a recovery copy until success; do NOT delete the only remaining copy in
   `finally`. Reuse the atomic-replace pattern from `FreeConfigPoolFetcher.cs:140`.

```diff
- if (File.Exists(CliExePath))
- {
-     File.Delete(CliExePath);
- }
- File.Move(tempBin, CliExePath);
+ // Atomic replace: keeps the working binary intact if the move fails.
+ File.Move(tempBin, CliExePath, overwrite: true);
```

```diff
+ // Dispose any prior process before reassigning (exactly once).
+ _process?.Dispose();
  _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
```

## 5. How (ordered minimal steps)

1. Read `EmergencyChannelManager` `LaunchProcess`/`OnProcessExited`/`Stop`/
   `Dispose` fully; map every `_process` read/write.
2. ZAP-2 (manager): dispose-before-reassign in `LaunchProcess`; dispose in
   `OnProcessExited` after capturing the exit code; ensure `Dispose` is idempotent.
3. Read `EmergencyChannelEngine` `StartAsync`/`Stop`/`OnManagerCrashed`. ZAP-2
   (engine): dispose+null the crashed manager in `OnManagerCrashed` (preferred —
   keeps the field honest) so a later `StartAsync` does not orphan it.
4. Read `WgturnUpdater` install step + `finally`. ZAP-3: switch to
   `File.Move(..., overwrite:true)`; remove the delete-first; adjust the `finally`
   so it only cleans a genuinely-stale temp (not the only copy).
5. Add failure-injection tests (below). Static review of atomicity/rollback paths.

### Tests written

- `EmergencyChannelManagerTests.LaunchProcess_SecondLaunch_DisposesPriorProcess`
  — fails on old code (prior process undisposed). Use a fake/disposable process
  wrapper or assert dispose count.
- `EmergencyChannelManagerTests.OnProcessExited_DisposesProcess`.
- `EmergencyChannelEngineTests.CrashThenReconnect_DisposesCrashedManager` — fails on
  old code (crashed manager orphaned). Use a fake manager factory counting
  disposals.
- `EmergencyChannelEngineTests.MultipleCrashReconnectCycles_NoManagerLeak`.
- `WgturnUpdaterTests.Install_MoveFails_PreservesPreviousBinary` — fails on old
  code (working binary deleted). Inject a failing move (e.g. read-only target /
  fake FS seam) and assert the prior binary still exists.
- `WgturnUpdaterTests.Install_Success_CleansTemp` — successful replace leaves no
  stray temp.

### Verification approach

Fake filesystem / fake process+manager owners (no real binary/service mutation).
Execution in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- ZAP-2 consumers: `EmergencyChannelEngine` (manager owner), `RestartAsync`
  (`:168-176` Stop+Start). Invariant: `Stop()` remains idempotent; disposal is
  exactly-once; `RestartAsync` still works; started/crashed event wiring is
  preserved.
- ZAP-3 consumers: wgturn-cli install callers (UI W-4 stops the running process
  first per the `:425-426` comment). Invariant: on success the new binary is in
  place and version/variant markers (`:447-459`) still written; the in-use-exe
  Windows constraint is still honored (caller stops the process first).
- The atomic-replace pattern is shared with DATA-6 (R03) and the P1 DATA-1 fix —
  reuse, do not fork.

## 7. Exact expected file list

- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs` (ZAP-2)
- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelEngine.cs` (ZAP-2)
- `VPNRouter.Core/Services/WgturnUpdater.cs` (ZAP-3)
- `VPNRouter.Tests/EmergencyChannelManagerTests.cs` / `EmergencyChannelEngineTests.cs` (or existing emergency-channel test files)
- `VPNRouter.Tests/WgturnUpdaterTests.cs` (or existing wgturn test file)

## 8. Non-goals

- Do NOT change the wgturn-cli download/checksum policy (only the replace step).
- Do NOT change the Zapret version-marker logic (that is ZAP-1 / P10, already
  done — different file).
- Do NOT introduce a global process/factory abstraction.
- Do NOT run any real Zapret/Wgturn update or process kill (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Concurrency**: ZAP-2 disposal must be exactly-once and race-safe with the
  `Exited` event and `Dispose` (the existing `OnProcessExited` already tolerates a
  Dispose race on exit-code read — preserve that). The engine's `OnManagerCrashed`
  disposal must not race a concurrent `Stop()`.
- **Data-loss**: ZAP-3 is a data/availability fix — a failed update must leave the
  last working binary intact.
- **Security**: none new (do not weaken the in-use-exe admin guidance).
- **Platform**: `File.Move(..., overwrite:true)` works on Windows for a non-in-use
  target; the caller-stops-process-first contract (`:425-426`) is preserved.

## 10. Dependencies / overlaps

- **P10 (ZAP-1) inspected — NO file overlap** (P10 = `ZapretUpdater.cs` only) →
  base `origin/main`.
- **R06 (SEC-3) overlap caution**: R06 also edits `EmergencyChannelManager.cs`
  (the launch-args block). R07 edits the process dispose/exit block — a different
  region. If both are in flight, sequence R06 before R07 (or rebase) to avoid a
  textual conflict in `LaunchProcess`.
- The atomic-replace pattern is shared with R03 (DATA-6) — keep them consistent.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new emergency-channel + wgturn tests pass; existing updater tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: static bug-hunt of atomicity/rollback + exactly-once disposal.
- [ ] Gate 5 — MCP verify: N/A (Core + tests only).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — Core + tests only
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R07 branch, or delete
`codex/qwen-audit-r07-updater-resources-2026-07-29`. Emergency-channel disposal
reverts to prior (leak) behavior; wgturn replacement reverts to delete-first. No
persistent state is written by the fix itself.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r07-updater-resources-2026-07-29.md через Qwen
Code. IDs: ZAP-2, ZAP-3 (оба P2). Base branch: origin/main (P10/ZAP-1 трогал
только ZapretUpdater.cs — overlap отсутствует). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. ZAP-2: dispose+null
предыдущий Process в EmergencyChannelManager.LaunchProcess до перезаписи и
dispose crashed manager в EmergencyChannelEngine.OnManagerCrashed. ZAP-3:
stage-and-atomic-replace wgturn-cli (File.Move(tmp, path, overwrite:true)) без
destructive delete-first; сохрани recovery copy до успеха; не удаляй единственную
copy в finally. Переиспользуй существующий atomic-replace паттерн
(FreeConfigPoolFetcher.cs:140). Напиши failure-injection тесты, падающие на старом
поведении. НЕ запускай локальные build/test/app/binary/service, не делай live
Zapret/Wgturn update. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```
