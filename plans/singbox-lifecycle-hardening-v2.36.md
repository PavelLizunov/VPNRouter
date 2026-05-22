# v2.36 — SingBoxManager lifecycle hardening

**Authored**: 2026-05-23 после cut'а v2.35.3 stable
**Trigger**: 3-agent post-r1 audit нашёл 4 pre-existing latent concerns в
SingBoxManager (B1-B4). Не блокировали v2.35.3 (defensive-guarded, не
crashят сегодня), но fragile patterns → candidates для consolidation.
**Authority**: post-mortem audit findings от Agent B (Task #53 sibling
race surface review).

## Триггер

Audit, проведённый перед cut'ом v2.35.3 stable, surface'нул 4 latent
issue в `VPNRouter.Core/Services/SingBoxManager.cs`. Они существовали с
до-v2.35 era, не были введены Phase 4 prep. Текущие defensive guards
(idempotent `Dispose`, `_owned` check в `TunOwnershipLock.Release`, ECMA
atomicity для enum) не дают им крашить, но pattern fragile.

Web research (см. Sources внизу) подтвердил, что:
- ProcessExit + Dispose dual-cleanup — known footgun в .NET, standard
  fix через `Interlocked.CompareExchange` flag.
- Concurrent disposal через `Interlocked.CompareExchange` — Microsoft
  recommended pattern (https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose).
- Process.Kill `entireProcessTree:true` на Windows НЕ фaerит Exited
  event — known runtime issue #63328 (dotnet/runtime). Это влияет на
  наш SingBoxManager Stop path → требует platform-aware testing перед
  refactor'ом.
- Enum field на underlying `int` атомарны на x64 per ECMA spec → B4 не
  bug, retire.

## Symptom

Не было user-reported crash'а связанного с этими 4 issues. Audit нашёл
их через code reading + cross-checking с `plans/task53-singboxmanager-restart-tunlock-2026-05-21.md`.

Latent fail modes:
- **B1**: при crash вместо graceful Dispose, ProcessExit handler
  попытается `_tunLock.Dispose()` повторно. Idempotent guard в
  `TunOwnershipLock.Dispose` prevents NRE/ODE, но логика "two cleanup
  paths" muddled.
- **B2**: если два потока вызывают `Stop()` одновременно и оба
  достигают `if (releaseLock) _tunLock.Release()` (4 paths), вторая
  Release no-op'ит через `_owned` guard. Race window мал, но pattern
  без явного guard.
- **B3**: между `StopInternal(releaseLock:false)` в `Restart()` и
  `LaunchProcess()` — если Crashed event фaerит во время sleep window
  (750ms), `OnProcessExited` устанавливает `State=Failed` → потом
  `LaunchProcess` перетирает на `Running`. Listener'ы (HealthMonitor)
  видят ambiguous state transition.
- **B4** ~~enum field unsync~~ — RETIRED, ECMA spec atomicity на int.

## Root cause

**B1**: исторически added как fallback safety net для process crash
(Environment.Exit, Ctrl+C, OOM kill). Pre-dates the public
`SingBoxManager.Dispose()` graceful path. Sequence не consolidated после
Dispose был добавлен.

```csharp
// SingBoxManager.cs:132 (ProcessExit)
AppDomain.CurrentDomain.ProcessExit += (_, _) => _tunLock.Dispose();

// SingBoxManager.cs:1248 (Dispose)
public void Dispose() {
    Stop();  // → StopInternal(releaseLock: true) → _tunLock.Release()
    _handle?.Dispose();
    // _tunLock NOT disposed here explicitly — its Release done
}
```

Если Dispose() уже отработал → `_tunLock` имеет `_owned=false`. Когда
ProcessExit fires, вызывает `Dispose()` повторно. Idempotent через
`if (_disposed) return` guard, но дублирующий код path.

**B2**: `Stop()` метод не имеет entry-level mutex или
`Interlocked.CompareExchange` flag. Concurrent Stop() от UI + HealthMonitor
+ ProcessExit reaches the four Release sites:

```csharp
// SingBoxManager.cs:267, 315, 331, 413 (после Task #53 fix)
if (releaseLock) _tunLock.Release();
```

`TunOwnershipLock.Release` имеет `if (!_owned || _semaphore == null) return`
guard который catch'ит double-release. Но в `Stop()` пути concurrent
вызовов могут race на other state mutations (e.g. `_handle = null`,
`State = Stopped`).

**B3**: `Restart()` flow:
```csharp
// SingBoxManager.cs:463
State = SingBoxState.Restarting;
StopInternal(releaseLock: false);  // Process killed, _handle stays
Thread.Sleep(750);
LaunchProcess(exePath);  // ← sets State = Running (line 939)
```

Между `StopInternal` и `LaunchProcess` — если killed process'а Exited
event фaerит (известный gotcha на Linux/macOS; на Windows известный
issue #63328 не фaerит для entireProcessTree:true → но мы используем
другие Kill paths), `OnProcessExited` handler:
```csharp
// SingBoxManager.cs:1025
if (State == SingBoxState.Restarting) { /* expected, no-op */ }
else { State = SingBoxState.Failed; FireCrashed(); }
```

В теории guard'ed правильно. На практике если `State` уже flipped в
Stopped (concurrent Stop call), а потом Restart sets it обратно в
Restarting — race window для Crashed fire.

## Fix strategy

### Order (по приоритету и риску)

1. **B2 first** — concurrent Stop guard. Самый bounded fix, well-known
   pattern, minimal blast radius. Foundation для B1.
2. **B1 second** — ProcessExit consolidation. Использует B2's atomic
   flag чтобы distinguish "Dispose already ran" vs "process crash, run
   fallback". Builds on B2.
3. **B3 deferred** — отдельный brief, dedicated test suite, требует
   review всех 4 Kill paths cross-platform + factoring в issue #63328.
   Не для v2.36 ship.
4. **B4 retired** — non-bug per ECMA.

### B2 — concurrent Stop guard (this brief)

**Approach**: добавить `int _stopState` field (0=idle, 1=stopping) и
`Interlocked.CompareExchange` guard в `StopInternal` entry. Match
existing `_disposed` pattern.

```csharp
private int _stopState; // 0 = idle, 1 = stopping

private void StopInternal(bool releaseLock)
{
    // B2 (v2.36.x): atomic guard — only one thread proceeds through
    // StopInternal at a time. Second concurrent caller sees stopState=1
    // and returns immediately. Idempotent re-Stop (intended in some
    // code paths) requires _stopState reset at end of StopInternal.
    if (Interlocked.CompareExchange(ref _stopState, 1, 0) != 0)
    {
        _logger.Debug("[SingBoxManager] StopInternal: concurrent call detected, returning");
        return;
    }
    try
    {
        // ... existing StopInternal body ...
    }
    finally
    {
        Volatile.Write(ref _stopState, 0); // allow next Stop
    }
}
```

**Test pin**: `SingBoxManagerConcurrentStopTests.cs` (new file):
- `ConcurrentStop_OnlyOneThreadProceeds` — 5 threads call Stop()
  simultaneously, verify only one passes through Kill + Release;
  others return early.
- `Stop_ThenStopAgain_BothProcessSequentially` — sequential Stop's
  both run (re-entry allowed post-finally).

**Risk**: LOW — additive flag, minimal code change. Only affects
concurrent-call paths. Existing single-threaded callers unaffected.

### B1 — ProcessExit consolidation (this brief, after B2)

**Approach**: ProcessExit handler reads B2's `_stopState` (or new
`_disposed` flag from Dispose) to determine if cleanup already ran.
Only invokes `_tunLock.Dispose()` if "neither Dispose nor Stop has run".

```csharp
private int _disposed; // 0 = alive, 1 = disposed

public SingBoxManager(...)
{
    // B1 (v2.36.x): ProcessExit fallback ТОЛЬКО если Dispose не
    // отработал. Это происходит на abrupt termination (Environment.Exit,
    // OOM, force-kill). На normal Dispose() path этот lambda no-ops.
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        if (Volatile.Read(ref _disposed) == 0)
            _tunLock.Dispose();
    };
}

public void Dispose()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
    Stop();
    _handle?.Dispose();
    _tunLock.Dispose(); // explicit — replaces ProcessExit fallback for normal path
}
```

**Test pin**: `SingBoxManagerCleanupPathTests.cs` (new file):
- `Dispose_RunsCleanupOnce_ProcessExitNoOps` — call Dispose, then
  invoke ProcessExit handler manually (or via test harness), verify
  no second Dispose on `_tunLock`.
- `NoDispose_ProcessExitRunsFallbackCleanup` — skip Dispose, invoke
  ProcessExit, verify `_tunLock.Dispose()` was called.

**Risk**: MEDIUM. Touching cleanup ordering. Risk: if somewhere
existing code relies on ProcessExit running EVEN after Dispose (e.g.
secondary cleanup), we miss it. Mitigation: grep ProcessExit handlers
in codebase to confirm only this one site, no callers depend on
post-Dispose ProcessExit re-trigger.

### B3 — Restart state race (DEFERRED to separate brief)

Не входит в v2.36 ship. Требует:
- Полный review всех 4 Kill paths cross-platform.
- Factoring в .NET runtime issue #63328 (Process.Kill entireProcessTree
  не фaerит Exited на Windows).
- Dedicated test suite pinning EACH state transition path до refactor'а.
- Brat-style approach: один Kill path / один restart scenario per -rN.

Brief на это: `plans/singbox-restart-state-machine-v2.36.x.md` (TBD).

## Acceptance — v2.36 ship (B1 + B2)

- [ ] **B2 fix** landed: `Interlocked.CompareExchange` guard на
  `StopInternal` entry + `Volatile.Write` reset в finally.
- [ ] **B2 tests**: 2+ new tests in `SingBoxManagerConcurrentStopTests.cs`
  pin both concurrent + sequential re-entry.
- [ ] **B1 fix** landed: `_disposed` flag через `Interlocked.CompareExchange`
  в Dispose + Volatile.Read в ProcessExit lambda.
- [ ] **B1 tests**: 2+ new tests in `SingBoxManagerCleanupPathTests.cs`
  pin Dispose-runs-once + ProcessExit-fallback paths.
- [ ] `dotnet build -c Release` → 0 errors.
- [ ] Existing regression tests stay green (1241+ methods).
- [ ] No regression в `SingBoxManagerStateMachineTests` / `SingBoxManagerRestartTunLockTests` / `SingBoxManagerTunOrphanRecoveryTests`.
- [ ] MCP UI smoke v2.36.0-r1 — Connect/Disconnect cycle no-crash.
- [ ] Live update gate v2.35.3 → v2.36.0-r1 PASS.
- [ ] Audit-style code review (1 agent) confirms no new fragility
  introduced.

## Оценка

- **B2**: ~2 часа (Interlocked.CompareExchange + 2 tests + build verify).
- **B1**: ~2 часа (consolidation + 2 tests + grep check для других
  ProcessExit dependencies).
- **Tests + audit-pass**: ~1 час.
- **MCP smoke + ship -r1**: ~30 мин.
- **Total**: ~5-6 часов.
- **Risk**: LOW для B2, MEDIUM для B1. Не блокирует v2.35.3, можно
  поднять как v2.36.0-r1 после field-test'а v2.35.3 (~24h soak).

## Связь с другими планами

- `plans/task53-singboxmanager-restart-tunlock-2026-05-21.md` — Task
  #53 fix, surfaced these 4 concerns в Outcome → Surprises.
- `plans/phase4-iwindowsdnshardening-2026-05-21.md` — IWindowsDnsHardening
  seam, pattern для new test infrastructure.
- (TBD) `plans/singbox-restart-state-machine-v2.36.x.md` — B3 deferred.

## Web research sources

- [Implement a Dispose method (Microsoft)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose) — `Interlocked.CompareExchange` reference pattern для thread-safe Dispose.
- [Thread-safe disposable objects (Faithlife)](https://faithlife.codes/blog/2008/03/threadsafe_disposable_objects/) — atomic disposal flag без full lock overhead.
- [Process.Kill(entireProcessTree: true) does not fire Exited event on Windows (dotnet/runtime#63328)](https://github.com/dotnet/runtime/issues/63328) — platform-specific Kill behaviour, влияет на B3 review.
- [The C# Memory Model (Microsoft MSDN Magazine)](https://learn.microsoft.com/en-us/archive/msdn-magazine/2012/december/csharp-the-csharp-memory-model-in-theory-and-practice) — ECMA atomicity для int/enum, обоснование B4 retire.
- [AppDomain.ProcessExit Event (Microsoft)](https://learn.microsoft.com/en-us/dotnet/api/system.appdomain.processexit) — .NET Core убрал 2-second time limit на ProcessExit handlers (vs Framework).
