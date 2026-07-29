# Phase 3 — R11 — ETW monitor disposal (PERF-1 residual only)

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r11-etw-disposal-2026-07-29`
**Base**: `codex/qwen-audit-p02-failover-wiring-2026-07-29` (MANDATED: PERF-1 edits the `VpnEngine` teardown region — `_etw?.Stop()` / `_etw = null` — which P02 rewrote, +92/-45 in `VpnEngine.cs`. Verified via `git diff --stat origin/main...codex/qwen-audit-p02-failover-wiring-2026-07-29`.)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R11); prompt pool P10
**IDs**: PERF-1 (P3 residual ONLY)
**Effort**: ~45 min
**Risk**: LOW (dispose hygiene; the heavy session is already disposed)
**Blast radius**: `VPNRouter.Core/Services/VpnEngine.cs` (teardown call site) · ~+2 LOC · runtime: per-reconnect `ManualResetEventSlim` WaitHandle disposal
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| PERF-1 | P2 | PARTIALLY_CONFIRMED | P3 | High |

**The heavy `TraceEventSession` leak claim is REFUTED and MUST NOT be fixed.**
The heavy session IS disposed deterministically: `RunSession` uses `using var
session = new TraceEventSession(SessionName)` (`EtwProcessMonitor.cs:122`), which
disposes when `Source.Process()` returns. `Stop()` deliberately skips Dispose
(`:73-78` comment) because a second Dispose can throw on an already-finalised
session in some TraceEvent versions.

Corrected scope (residual ONLY): a per-connect ETW monitor is Stopped (which
allocates a `ManualResetEventSlim` WaitHandle via `_sessionReady.Wait(1s)` at
`:84`) and nulled WITHOUT `Dispose`; only `Dispose` (`:197-208`) disposes
`_sessionReady`. The residual is a single small `ManualResetEventSlim` whose inner
`SafeWaitHandle` is finalizer-closed — NOT an unbounded accumulation of heavy
resources → downgrade P2 → P3.

## 2. Verified current root cause (commit `b39a28c3`)

`VPNRouter.Core/Services/VpnEngine.cs` teardown (verified `:832` / `:845`):

```csharp
try { _etw?.Stop(); } catch { }      // :832 — Stop only, not Dispose
...
_etw = null;                          // :845 — monitor dropped without Dispose
```

`VPNRouter.Core/Services/EtwProcessMonitor.cs`:

- `:38` `private readonly ManualResetEventSlim _sessionReady = new(false);`
- `:84` `Stop()` → `if (!_sessionReady.Wait(TimeSpan.FromSeconds(1)))` — the first
  `Wait(timeout)` lazily allocates the kernel `SafeWaitHandle`.
- `:122` `using var session = new TraceEventSession(SessionName);` — heavy session
  disposed deterministically (refutes the heavy-leak claim).
- `:197-208` `Dispose()` is the ONLY thing that disposes `_sessionReady`
  (`try { _sessionReady.Dispose(); } catch { }`), and it calls `Stop()` itself.
- Per-connect creation: `StartupPipeline.cs:1370-1372` (`MonitorFactory()`) →
  `PlatformServices.cs:50` (`new EtwProcessMonitor(logger)`).

Consequence: each connect teardown calls `Stop()` (allocating the WaitHandle) and
nulls the monitor without `Dispose`, so the `ManualResetEventSlim`'s
`SafeWaitHandle` waits for the finalizer instead of being released deterministically
per reconnect.

## 3. Why

The monitor's own `Dispose` already does the right thing (Stop + dispose
`_sessionReady`), but the engine teardown only calls `Stop`. Calling `Dispose`
makes the per-reconnect WaitHandle release deterministic. This is a small hygiene
fix; the heavy ETW session is already correctly disposed and must not be touched.

## 4. What

At the `VpnEngine` connect-teardown site, dispose the ETW monitor instead of only
stopping it. Because `EtwProcessMonitor.Dispose()` already calls `Stop()`
internally (`:197-208`), the minimal change is to call `Dispose` (guarded by the
same `try/catch`) and keep the subsequent `_etw = null`.

```diff
- try { _etw?.Stop(); } catch { }
+ try { _etw?.Dispose(); } catch { }
```

(Keep `_etw = null;` at `:845`. Do NOT add a second Dispose elsewhere. Do NOT
change `EtwProcessMonitor` itself — its `Dispose`/`Stop` contract is correct.)

## 5. How (ordered minimal steps)

1. **Rebase onto P02 first** (base branch). Re-read the post-P02 `VpnEngine`
   teardown and CONFIRM the lines still read `try { _etw?.Stop(); } catch { }` and
   `_etw = null;` (P02 rewrote this region). If P02 already changed the `_etw`
   handling, adapt to the post-P02 shape and record it in the Outcome.
2. Verify `EtwProcessMonitor.Dispose()` calls `Stop()` and disposes `_sessionReady`
   (so `Dispose` is a superset of `Stop` — safe to substitute).
3. Replace the teardown `_etw?.Stop()` with `_etw?.Dispose()` (same `try/catch`).
4. Check there is no OTHER `_etw?.Stop()` teardown site that also needs the change
   (search `_etw` reads/writes); fix the shared teardown point once, not per-caller.
5. Add the reconnect-disposal test.

### Tests written

- `EtwProcessMonitorTests.StopThenDispose_DisposesSessionReady` — pins that
  `Dispose` releases the `ManualResetEventSlim` (guard against regressing the
  monitor's own contract).
- `VpnEngineTeardownTests.ConnectDisconnectCycles_DisposeEveryMonitor` — fails on
  old code (teardown called `Stop` only). Use a fake/observable monitor (or a
  dispose-counting seam via `MonitorFactory`) and assert each connect/disconnect
  cycle disposes the monitor exactly once.

### Verification approach

Fake/observable monitor + dispose counting (no live ETW session, no admin).
Execution in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- Consumers: `VpnEngine` teardown (the `_etw` field), `StartupPipeline`
  `MonitorFactory` (`:1370-1372`), `PlatformServices.cs:50`. Invariant: the heavy
  `TraceEventSession` disposal via `using var session` is UNCHANGED; `Stop()`
  semantics are unchanged (Dispose calls Stop); exactly-once disposal preserved
  (`Dispose` guards on `_disposed`).
- Do NOT change `EtwProcessMonitor.Stop`'s deliberate "skip Dispose" behavior
  (`:73-78`) — that protects against a double-Dispose throw in some TraceEvent
  versions. The engine calling the monitor's public `Dispose` once is the correct
  path.

## 7. Exact expected file list

- `VPNRouter.Core/Services/VpnEngine.cs` (teardown `_etw?.Dispose()`)
- `VPNRouter.Tests/EtwProcessMonitorTests.cs` (or existing ETW test file)
- `VPNRouter.Tests/VpnEngineTeardownTests.cs` (or existing VpnEngine test file)

## 8. Non-goals

- Do NOT "fix" the heavy `TraceEventSession` leak — it is REFUTED (disposed via
  `using var session`).
- Do NOT modify `EtwProcessMonitor.Stop`/`RunSession`/`Dispose` internals.
- Do NOT add an ETW monitor abstraction/interface.
- Do NOT run any live ETW session or require admin (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Concurrency**: `Dispose` is guarded by `_disposed` and calls `Stop` (which
  joins the worker thread with bounded timeouts). Substituting `Dispose` for `Stop`
  at teardown does not introduce a double-dispose because the engine nulls `_etw`
  immediately after and `Dispose` is idempotent.
- **Platform**: ETW is Windows-only (`#if` guarded — the file ends with `#endif`);
  the teardown change is inside the same platform-conditional path. On non-Windows
  the monitor is a no-op; verify the call site remains platform-correct after the
  P02 rewrite.
- **Security / data-loss**: none.

## 10. Dependencies / overlaps

- **Base is P02** (FAIL-1) because both edit the `VpnEngine` teardown block. Verify
  the `_etw` lines after the P02 rewrite before editing; rebase onto the merged P02
  before pushing.
- No other R-package touches `VpnEngine`/`EtwProcessMonitor`.
- PERF-2 (free-config HttpClient) is REFUTED and is NOT part of this package.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new ETW/teardown tests pass; existing lifecycle tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; note the post-P02 line shape verified.
- [ ] Gate 4 — Self-review: confirm exactly-once disposal + unchanged heavy-session path (static).
- [ ] Gate 5 — MCP verify: N/A (Core lifecycle hygiene).
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
- [-] Gate 5: N/A — Core lifecycle hygiene
- [-] Gate 6: N/A

**Surprises encountered**: PENDING (include the verified post-P02 `_etw` line shape)
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R11 branch, or delete
`codex/qwen-audit-r11-etw-disposal-2026-07-29`. Because R11 is based on P02,
reverting R11 leaves the P02 failover fix intact. The teardown reverts to
`_etw?.Stop()`; the heavy session disposal is unaffected either way.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase3-audit-r11-etw-disposal-2026-07-29.md через Qwen Code.
ID: PERF-1 (P3, PARTIALLY_CONFIRMED). Base branch:
codex/qwen-audit-p02-failover-wiring-2026-07-29 (PERF-1 трогает VpnEngine.cs
teardown, который P02 переписал). ВАЖНО: heavy TraceEventSession leak claim
ОПРОВЕРГНУТ (TraceEventSession освобождается через using var session) — НЕ
исправляй его. Scope строго ограничен dispose'ом ManualResetEventSlim/monitor:
вызывай Dispose (не только Stop) на ETW monitor при connect teardown в VpnEngine
(_etw?.Stop() -> Dispose), чтобы _sessionReady SafeWaitHandle не ждал finalizer.
Проверь, что строки _etw ещё читаются как try { _etw?.Stop(); } catch { } /
_etw = null; после P02 rewrite. Напиши тест: несколько connect/disconnect cycles
dispose'ят каждый monitor. НЕ запускай локальные build/test/app/binary/service,
не делай live мутаций. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```
