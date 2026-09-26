# Adversarial Review: Lane P04 - Headless Protocol v1, State Machines & Storage Concurrency

> **TL;DR:** Deep adversarial analysis of the Headless Protocol subsystem (ProtocolServer, LineReader, Dispatcher, OutputQueue, RouterBackend, ConfigStorage) identified **3 P0 findings** (dual-cancel architecture producing dead cancel path with always-false result, semaphore disposal race causing ObjectDisposedException crash, raw exception logging violating zero-leak invariant), **5 P1 findings** (state desynchronization from dynamic readiness guard evaluation, non-atomic custom config removal causing orphaned state, unhandled mutation cancellation allowing partial writes, subscription refresh loop aborting on first failure, and snapshot method's contradictory classification across layers), and **5 P2 findings** (dead code, progress stamping dictionary mutation race, reader loop suspension under backpressure, global AppPaths mutation, and TOCTOU in symlink checks). Total: **3 P0, 5 P1, 5 P2**.

---

## 1. Subsystem State Machine Analysis

### 1.1. ProtocolDispatcher: Implemented vs. Intended Concurrency State Machine

**Implemented states:** `Idle`, `OrdinaryActive`, `UrgentExecuting` (concurrent with either).

The dispatcher implements a clean two-track concurrency model:
- **Ordinary track**: At most one active. CAS-guarded via `_activeOperationId`. Concurrent ordinary requests get immediate `busy` rejection.
- **Urgent track**: `cancel` (synchronous, immediate), `disconnect` (async, cancels ordinary first). Bounded by `_activeUrgentCount <= 4`.

**Finding: `snapshot` classification desynchronization.** `ProtocolConstants.IsUrgentOrReadOnlyMethod` classifies `snapshot` as urgent/read-only (line 62), but `ProtocolDispatcher.DispatchAsync` treats it as an ordinary operation (subject to `busy` rejection). Meanwhile, `RouterBackend.ExecuteAsync` treats `snapshot` as a bypass operation that skips `_busyState` (line 97-101). This creates a three-layer inconsistency:

| Layer | `snapshot` classification |
|---|---|
| `ProtocolConstants.IsUrgentOrReadOnlyMethod` | Urgent/read-only |
| `ProtocolDispatcher.DispatchAsync` | Ordinary (subject to `busy`) |
| `RouterBackend.ExecuteAsync` | Bypass (skips `_busyState`) |

The net effect: the Dispatcher rejects concurrent `snapshot` requests with `busy`, but if a `snapshot` somehow reaches `RouterBackend` directly it would bypass `_busyState`. In the actual NDJSON pipeline, the Dispatcher's ordinary-gating is authoritative, so `snapshot` IS subject to `busy` at the wire level. But the backend contract disagrees. See FINDING-P04-05.

### 1.2. RouterSession Lifecycle State Machine: Dynamic Guard Evaluation Gap

The session state machine has an unusual property: the `State` getter dynamically evaluates `_retainedReadinessGuard()` under `_stateLock` and may return `SessionStates.Error` even when `_state` field is `SessionStates.Connected`. Critically, this dynamic override does NOT:
1. Mutate `_state` to `Error`
2. Fire the `Changed` event
3. Trigger `TransitionState`

This means the QML consumer can observe `state: "error"` on a `snapshot` response but never receive a `state` event transition, leaving it in a desynchronized state until the next operation or engine event forces a `TransitionState`. See FINDING-P04-04.

### 1.3. RouterBackend: Dual-Cancel Architecture Produces Dead Path

There are **two independent cancel mechanisms** that are completely disconnected:

1. **`ProtocolDispatcher.HandleCancel`** (Protocol layer, line 134): Looks up `_activeOperationId` and cancels `_activeOperationCts`. This is the LIVE path. It signals the CTS linked to the handler's `ExecuteAsync` call.

2. **`RouterBackend.HandleCancel`** (Backend layer, line 337): Looks up `_inFlightRequests` dictionary and cancels the CTS found there. But `_inFlightRequests.TryAdd(...)` is **never called anywhere in the codebase**. This dictionary is always empty.

The protocol flow routes `cancel` through the Dispatcher first (which handles it as an immediate non-blocking control), so `cancel` never even reaches `RouterBackend.HandleCancel` via the normal NDJSON pipeline. But even if it did (e.g., via direct backend testing), the dictionary lookup would always fail, and the response would always be `{ id, cancelled = false }`. See FINDING-P04-01.

### 1.4. Output Queue State Machine: Semaphore Count Invariant

The output queue maintains a semaphore `_signal` whose count should equal the number of items in `_queue`. Analysis reveals a subtle correctness property:

- `EnqueueStateEvent` with in-place coalescing: When an existing `ProtocolStateEventFrame` is found and its `StateData` is updated in-place, the semaphore is NOT incremented (correct - frame count didn't change).
- `EnqueueStateEvent` eviction by `EnqueueResponseAsync`: When a state event is evicted to make room for a response, `_signal` is not decremented but also no new `Release()` is called (correct - the evicted frame's signal was already consumed or pending).

This is correct but fragile: the invariant `_signal.CurrentCount <= _queue.Count` holds only because evictions remove one frame and add one without touching the semaphore.

---

## 2. Concurrency Hazards & Race Conditions

### 2.1. SemaphoreSlim Disposal Race in RouterSession.DisposeAsync (P0)

**File:** `RouterSession.cs:517-586`

`DisposeAsync` does NOT acquire `_gate` before stopping the engine or in its `finally` block. The sequence is:

```
DisposeAsync:                     ConnectAsync (concurrent):
  _disposed = true                  ... inside _gate ...
  _activeConnectCts?.Cancel()       ... _engine operations ...
  BoundedTeardown.Stop...           ... finally:
  ...                                   _gate.Release()  // <-- ObjectDisposedException!
  finally:
    _gate.Dispose()               
```

If `ConnectAsync` or `ApplyAsync` is executing inside the gate when `DisposeAsync` runs, the `finally { _gate.Dispose() }` at line 584 will dispose the semaphore. When the concurrent operation reaches its own `finally { _gate.Release() }`, it throws `ObjectDisposedException`. This is an unhandled crash in the `ConnectAsync` finally block (line 364). See FINDING-P04-02.

### 2.2. Progress Event Dictionary Mutation Race (P2)

**File:** `ProtocolDispatcher.cs:54-65`

`HandleProgress` reads `_activeOperationId` under lock, then calls `EnsureProgressId` outside the lock. `EnsureProgressId` mutates the dictionary (`dict["id"] = activeId`) if the incoming `progressData` is an `IDictionary<string, object>`. If the same dictionary instance is shared across concurrent progress events (e.g., if a feature handler reuses a dictionary), this is an unsynchronized mutation. Current features create fresh dictionaries, but the contract does not enforce this.

### 2.3. Reader Loop Suspension Under Output Backpressure (P2)

**File:** `ProtocolServer.cs:143` + `ProtocolOutputQueue.cs:34-72`

When `EnqueueResponseAsync` blocks waiting for `_queueNotFullTcs`, the entire stdin reader loop suspends. A client that sends rapid requests but stops reading stdout will cause the output queue to fill with responses. Once all 8 slots are responses (no evictable state events), `EnqueueResponseAsync` blocks, halting stdin parsing. Any urgent `cancel` or `disconnect` sent during this window will not be parsed until the 3-second output write stall deadline fires and triggers teardown. This is an accepted tradeoff per `plans/omarchy-transport-security-review.md`, but it means urgent operations have no delivery guarantee under backpressure.

---

## 3. Fail-Closed & Leak Invariants

### 3.1. Traffic Fail-Closed Analysis

The session state machine is rigorously fail-closed for traffic:

1. **Crash/SIGTERM**: `Program.Main` cancels via console CTS. `ProtocolServer.RunAsync` catches `OperationCanceledException`, calls `CancelActiveOperation`, drains in-flight tasks (2s), then tears down. `RouterSession.DisposeAsync` runs `BoundedTeardown.StopBoundedAsync` with 5s deadline. If the engine process remains running, state transitions to `Error("stop_failed")` - never claims `Disconnected` with a live process.

2. **Network interface drops**: The readiness guard (`_retainedReadinessGuard`) returns `false`, causing `State` getter to return `Error` dynamically. The engine's `StatusChanged("Stopped")` event triggers `TransitionState(Error, "connection_lost")`.

3. **Pipe stalls**: Output write stall (>3s) sets `_isStalled = true`, invokes `_onBackpressureOrEof()` which cancels `serverCts`, triggering full teardown chain.

**Verdict:** Traffic fails closed. The system never reports `Disconnected` while an owned sing-box process is alive (enforced both in `TransitionState` and the `State` property getter). No unencrypted traffic leak path was identified.

### 3.2. Secret Leak Paths

**Raw exception logging in RouterBackend (P0):** Lines 321 and 326 pass full exception objects to `_logger.Error(conflictEx/rollbackEx, ...)`. Serilog's structured logging with `ex` as first argument logs the full exception message, inner exceptions, and stack trace. These exceptions originate from `ConfigStorage.SaveSettings` which may contain filesystem paths, mutex names (which embed hashed data directory paths), and serialization details. This violates the zone invariant: "Errors, log lines, and responses must never echo raw input, exception messages, inner exceptions, stack traces..."

**Raw exception logging in ConfigStorage (P0):** Lines 359 and 375 pass `ex` as first argument to `_logger.Warning(ex, ...)`. All other catch blocks in ConfigStorage correctly log only `ex.GetType().Name`. These two sites in `AcquireCooperativeLock` deviate and may leak OS-level exception messages containing paths.

### 3.3. RouterBackendHandler Event Leak on Disposal

`RouterBackendHandler.DisposeAsync` unsubscribes from `_backend` events but does not clear its own `StateChanged` and `ProgressChanged` invocation lists. If a caller retains a reference to the handler after disposal, the handler's own subscribers remain rooted. Impact is minimal since disposal happens immediately before process exit.

---

## 4. Security & Privilege Boundaries

### 4.1. Privilege Model

The headless daemon runs entirely unprivileged (UID > 0). Elevated capabilities are attached only to the external `sing-box` binary via file capabilities (`cap_net_admin,cap_net_bind_service=+eip`). No `pkexec`, `sudo`, or setuid invocations exist in the headless codebase. `SingBoxRuntimePolicy.DefaultProduction` defaults to `untrusted`, refusing to launch any binary without explicit authorization.

### 4.2. Input Validation

The input validation chain is comprehensive and correctly ordered:
1. Line framing: 256 KiB pre-accumulation (ProtocolLineReader)
2. JSON parsing: depth 32, duplicate keys at all levels, root schema enforcement (ProtocolParser)
3. Method allowlist: 37 known methods (ProtocolConstants)
4. ID validation: `^[a-zA-Z0-9-]{1,64}$` (ProtocolRequest)
5. Parameter allowlists: per-method `EnsureAllowedProperties`/`EnsureEmptyParameters` (RouterBackend + Features)

### 4.3. File System Security

- **Symlink protection**: `AssertNoSymlinksInPath` walks the full parent hierarchy, fail-closed on unknown exceptions.
- **POSIX permissions**: ConfigStorage strictly enforces 0700/0600 and fails closed if forbidden bits remain. CustomConfigStorage is best-effort (see FINDING-P04-11).
- **TOCTOU in symlink checks (P2)**: Both `ConfigStorage` and `CustomConfigStorage` check for symlinks before `File.Move`. Between the check and the move, a local attacker with directory access could swap an entry for a symlink. However, exploitation requires write access to the user's private 0700 directory.
- **Path sanitization**: CustomConfigStorage strips invalid filename chars and prepends `custom-`, preventing traversal.

### 4.4. CustomConfigStorage Permission Asymmetry

`ConfigStorage` enforces file permissions fail-closed (throwing `storage_error` if non-private). `CustomConfigStorage` applies permissions best-effort (catches and ignores errors). This means custom sing-box JSON configs may be written with world-readable permissions on filesystems that don't support POSIX modes, while `config.yaml` would correctly refuse. Custom configs may contain sensitive routing rules and outbound configurations.

---

## 5. Inconsistencies & Protocol Violations

### 5.1. Dual Cancel Architecture Mismatch

`ProtocolDispatcher.HandleCancel` is the authoritative cancel handler at the wire level. `RouterBackend.HandleCancel` is a dead second cancel implementation that can never succeed (empty `_inFlightRequests`). Any direct consumer of `RouterBackend.ExecuteAsync("cancel", ...)` (bypassing the protocol layer) will always receive `{ id, cancelled = false }` regardless of whether an operation is running.

### 5.2. `snapshot` Method Classification Triple-Inconsistency

Three layers disagree on whether `snapshot` is urgent, ordinary, or bypass:
- `ProtocolConstants`: urgent/read-only
- `ProtocolDispatcher`: ordinary (subject to `busy`)
- `RouterBackend`: bypass (skips `_busyState`)

At the wire level, the Dispatcher's classification wins: `snapshot` is treated as ordinary and blocked by `busy`. But the `IsUrgentOrReadOnlyMethod` helper creates false documentation, and the backend's bypass creates a contract mismatch for direct callers.

### 5.3. Error Code Inconsistency for Custom Config I/O Failures

`CustomConfigStorage.SaveCustomConfig` lacks a catch block wrapping filesystem operations (unlike `ConfigStorage.SaveSettings`). If `File.Move` or `StreamWriter.Write` throws `IOException`, it escapes as a raw .NET exception. The `ProtocolDispatcher` catches it as `Exception` and returns `"internal_error"`, whereas the domain-correct code is `"storage_error"`. This inconsistency means the QML consumer cannot distinguish storage I/O failures from internal bugs for custom config operations.

---

## 6. Prioritized Actionable Findings

### FINDING-P04-01: Dead `_inFlightRequests` Dictionary Produces Always-False Cancel (P0)

- **Source Anchor:** `RouterBackend.cs:47` (declaration), `RouterBackend.cs:337-358` (HandleCancel), `RouterBackend.cs:423-428` (DisposeAsync cleanup)
- **Mechanism:** `_inFlightRequests` is a `ConcurrentDictionary<string, CancellationTokenSource>` that is declared, queried in `HandleCancel`, iterated and cleaned up in `DisposeAsync`, but `TryAdd` is never called anywhere in the codebase. The dictionary is permanently empty.
- **Threat/Failure Scenario:** Any consumer calling `RouterBackend.ExecuteAsync("cancel", ...)` directly (outside ProtocolDispatcher) gets `{ id, cancelled = false }` even when a cancellable operation is running. The DisposeAsync cleanup loop over `_inFlightRequests` is dead code. The real cancel path exists only in `ProtocolDispatcher`, creating an undocumented contract split. If a future integration uses `RouterBackend` without `ProtocolDispatcher`, cancellation silently fails.
- **Fix:** Remove `_inFlightRequests` from `RouterBackend` entirely. Document that `cancel` is managed exclusively by `ProtocolDispatcher`. Remove the dead cleanup in `DisposeAsync`. If backend-level cancel is needed, accept request IDs in `ExecuteAsync` and populate the dictionary.

### FINDING-P04-02: `RouterSession.DisposeAsync` Races with Gate Holders, Causing Crash (P0)

- **Source Anchor:** `RouterSession.cs:517-586` (DisposeAsync), `RouterSession.cs:361-365` (ConnectAsync finally), `RouterSession.cs:429-432` (ApplyAsync finally)
- **Mechanism:** `DisposeAsync` sets `_disposed = true` and cancels `_activeConnectCts`, then proceeds to `BoundedTeardown.StopBoundedAsync` and `_engine.DisposeAsync()` without acquiring `_gate`. In the `finally` block (line 584), `_gate.Dispose()` is called unconditionally. If `ConnectAsync` or `ApplyAsync` is currently inside the gate and reaches its `finally { _gate.Release(); }`, it calls `Release()` on an already-disposed `SemaphoreSlim`, throwing `ObjectDisposedException`.
- **Threat/Failure Scenario:** Process crash during concurrent disposal + connect/apply. The crash occurs in a `finally` block, potentially preventing proper engine teardown and leaving an orphaned sing-box process.
- **Fix:** `DisposeAsync` should attempt `_gate.WaitAsync(TimeSpan.FromSeconds(1))` before proceeding, or wrap gate holders' `Release()` in a try-catch for `ObjectDisposedException`, or use a CancellationTokenSource to signal disposal instead of direct `_gate.Dispose()`.

### FINDING-P04-03: Raw Exception Objects Logged in ConfigStorage and RouterBackend (P0)

- **Source Anchor:** `ConfigStorage.cs:359`, `ConfigStorage.cs:375` (Mutex error logging), `RouterBackend.cs:321`, `RouterBackend.cs:326` (rollback error logging)
- **Mechanism:** Four logging sites pass `ex` (the full exception object) as the first argument to `_logger.Warning(ex, ...)` or `_logger.Error(ex, ...)`. Serilog renders the complete exception including message, inner exceptions, and stack trace. Exception messages from filesystem or mutex operations may contain absolute paths, data directory locations, and OS-specific error details.
- **Threat/Failure Scenario:** Violation of zone invariant #1: "Errors, log lines, and responses must never echo raw input, exception messages, inner exceptions, stack traces, subscription URLs, or credentials." Leaked paths could assist local privilege escalation or reveal deployment topology.
- **Fix:**
  - `ConfigStorage.cs:359,375`: Replace `_logger.Warning(ex, ...)` with `_logger.Warning("[ConfigStorage] ...: {ErrorType}", lockName, ex.GetType().Name)`.
  - `RouterBackend.cs:321,326`: Replace `_logger.Error(conflictEx, ...)` and `_logger.Error(rollbackEx, ...)` with `_logger.Error("[RouterBackend] ...: {ErrorType}", conflictEx.GetType().Name)` / `rollbackEx.GetType().Name`.

### FINDING-P04-04: Dynamic Readiness Guard Evaluation Causes Silent State Desynchronization (P1)

- **Source Anchor:** `RouterSession.cs:84-108` (State getter), `RouterSession.cs:112-134` (ErrorCode getter), `RouterSession.cs:137-152` (EvaluateRetainedGuard)
- **Mechanism:** When `_state == Connected` but `EvaluateRetainedGuard()` returns false, the `State` property dynamically returns `SessionStates.Error` without updating `_state`, firing `Changed`, or calling `TransitionState`. The QML consumer observes `state: "error"` in snapshot responses, but never receives a `state` event with the transition.
- **Threat/Failure Scenario:** The QML plugin's state binding may remain on the previous state event (which reported `"connected"`) while individual snapshot polls show `"error"`. This creates a split-brain view where event-driven UI elements show connected but polling-based elements show error. The consumer cannot act on the transition (e.g., show disconnect UI) until a separate engine event triggers a formal `TransitionState`.
- **Fix:** When `EvaluateRetainedGuard()` returns false inside the `State` getter, dispatch an asynchronous `TransitionState(SessionStates.Error, "connection_lost")` via `Task.Run` or `ThreadPool.QueueUserWorkItem` to formally commit the state change and fire the event. Guard against re-entrant calls.

### FINDING-P04-05: `snapshot` Method Classification Triple-Inconsistency (P1)

- **Source Anchor:** `ProtocolConstants.cs:62` (IsUrgentOrReadOnlyMethod), `ProtocolDispatcher.cs:84-96` (ordinary gating), `RouterBackend.cs:97-101` (bypass gating)
- **Mechanism:** Three layers disagree on `snapshot`'s concurrency classification. `ProtocolConstants.IsUrgentOrReadOnlyMethod` returns `true`. `ProtocolDispatcher` treats it as ordinary (blocks with `busy` if another operation is running). `RouterBackend` treats it as bypass (always executes regardless of `_busyState`). `IsUrgentOrReadOnlyMethod` is completely unreferenced across the codebase.
- **Threat/Failure Scenario:** The Dispatcher's gating means `snapshot` fails with `busy` during long operations (e.g., `servers.verify`). The QML plugin cannot poll current state during a running operation. If the intent is for `snapshot` to be read-only and always available, the Dispatcher should let it through. If the intent is for it to be ordinary, the constants and backend should agree.
- **Fix:** Either (a) make `snapshot` truly urgent in the Dispatcher by adding it to the urgent check alongside `cancel`/`disconnect` (requires ensuring `GetSnapshot()` is safe to call concurrently with mutations), or (b) remove `IsUrgentOrReadOnlyMethod` from `ProtocolConstants` and change `RouterBackend` to gate `snapshot` behind `_busyState` for consistency. Option (a) is recommended as it matches the backend's actual thread-safety (GetSnapshot reads cloned settings).

### FINDING-P04-06: Non-Atomic Custom Config Removal Causes Orphaned State (P1)

- **Source Anchor:** `CustomConfigFeature.cs` (via inventory `H08_B065_H08.md` defect 4, `H07_B064_H07.md:460-470`)
- **Mechanism:** `custom.remove` deletes the physical JSON file via `_customStorage.DeleteCustomConfig(match.Name)` BEFORE `_storage.SaveSettings(settings, revision)`. If `SaveSettings` fails (revision conflict, lock contention, disk error), the file is permanently deleted but `config.yaml` still references it in `CustomConfigs`.
- **Threat/Failure Scenario:** The engine attempts to activate the missing custom config on next connect, failing with a file-not-found error. The user sees the config listed but cannot use it and cannot remove it again (the file is already gone, so re-issuing `custom.remove` may behave inconsistently).
- **Fix:** Defer file deletion until after `_storage.SaveSettings` succeeds. Alternatively, use staged rename (`.trash.{guid}`) that can be rolled back if SaveSettings fails.

### FINDING-P04-07: Unhandled Cancellation During Mutation Allows Partial Writes (P1)

- **Source Anchor:** `RouterBackend.cs:290-300` (ExecuteMutationAsync)
- **Mechanism:** `ct.ThrowIfCancellationRequested()` is called before `mutationAction()`, but if the cancellation token fires DURING `mutationAction()` (which may be async, e.g., `_subscriptionFeature.RefreshAsync` or `_profileFeature.SelectAsync`), the mutation action may throw `OperationCanceledException` after partially writing to disk. The `mutationAction()` call is NOT wrapped in the try/catch that handles guarded rollback (which only wraps `_session.ApplyAsync`).
- **Threat/Failure Scenario:** Subscription refresh writes partial server data to `config.yaml` via CAS, then gets cancelled. The partial data persists. No rollback runs because the exception escapes outside the guarded compensation block.
- **Fix:** Wrap `await mutationAction()` in the same try/catch as the apply phase, or capture the pre-mutation revision and perform rollback on any exception (not just apply failures).

### FINDING-P04-08: Subscription Refresh Loop Aborts on First Failure (P1)

- **Source Anchor:** `SubscriptionFeature.cs:229-236` (via inventory `H07_B064_H07.md` finding 3)
- **Mechanism:** `subscriptions.refresh` iterates through target subscriptions sequentially with no per-item exception handling. If subscription #1's `RefreshEntryAsync` throws (DNS failure, HTTP timeout, network error), the entire loop aborts. Subscriptions #2..N are never attempted. `ReconcileActiveSubscriptionServer` and `_storage.SaveSettings` are skipped, losing any successfully refreshed data from prior iterations.
- **Threat/Failure Scenario:** A single unreachable subscription endpoint prevents all subscriptions from refreshing. Successfully fetched server data is discarded.
- **Fix:** Wrap each `RefreshEntryAsync` invocation in try/catch, log per-subscription failures, continue the loop, then reconcile and save after all attempts.

### FINDING-P04-09: Readiness Guard Delegate Execution Under `_stateLock` (P1)

- **Source Anchor:** `RouterSession.cs:84-108` (State getter), `RouterSession.cs:137-152` (EvaluateRetainedGuard)
- **Mechanism:** `EvaluateRetainedGuard` is called inside `lock (_stateLock)` from the `State` and `ErrorCode` property getters. It invokes `_retainedReadinessGuard()`, an external delegate from `ILifecycleEngine`. If this delegate acquires its own locks or performs blocking operations, it creates a lock inversion risk. Additionally, every call to `GetSnapshot()` evaluates `State` and `ErrorCode`, invoking the guard delegate twice under lock, adding uncontrolled latency to snapshot generation.
- **Threat/Failure Scenario:** If a future engine implementation's readiness guard acquires an internal lock that is also acquired by `OnEngineStatusChanged` (which calls `TransitionState`, which acquires `_stateLock`), a deadlock occurs. Current implementations use lock-free checks, but the contract does not enforce this.
- **Fix:** Evaluate `_retainedReadinessGuard()` outside `_stateLock`, store the result, then check it under lock. Alternatively, cache the guard result with a short TTL (e.g., 500ms) to avoid repeated invocations.

### FINDING-P04-10: Global `AppPaths.OverrideDataDir` Mutation in ConfigStorage Constructor (P2)

- **Source Anchor:** `ConfigStorage.cs:85-89`
- **Mechanism:** Constructing `ConfigStorage(dataDir)` with a non-null `dataDir` calls `AppPaths.OverrideDataDir(DataDir)`, which mutates process-wide static state. Multiple `ConfigStorage` instances with different directories (e.g., in tests) cause the last one to win, corrupting `AppPaths.DataDir` for all other consumers including `CustomConfigStorage` which relies on `AppPaths.ConfigDir`.
- **Fix:** Pass explicit `dataDir`/`configDir` to `CustomConfigStorage`, or avoid mutating `AppPaths` when an explicit directory is supplied.

### FINDING-P04-11: CustomConfigStorage Best-Effort Permissions vs. ConfigStorage Fail-Closed (P2)

- **Source Anchor:** `CustomConfigStorage.cs` (via inventory `H08_B065_H08.md` table in section 5.1)
- **Mechanism:** `ConfigStorage` strictly enforces 0600/0700 and fails closed if forbidden bits remain. `CustomConfigStorage` applies permissions best-effort, catching and ignoring exceptions. Custom configs may contain sensitive routing outbound configurations.
- **Fix:** Align `CustomConfigStorage` with `ConfigStorage`'s fail-closed permission enforcement.

### FINDING-P04-12: Dead Code - `IsUrgentOrReadOnlyMethod` and `ProtocolOutputQueue.EnqueueResponse` Sync Variant (P2)

- **Source Anchor:** `ProtocolConstants.cs:62` (IsUrgentOrReadOnlyMethod), `ProtocolOutputQueue.cs:75-98` (EnqueueResponse synchronous variant)
- **Mechanism:** `IsUrgentOrReadOnlyMethod` is never called anywhere. The synchronous `EnqueueResponse(ProtocolResponseFrame)` method is also unreferenced - all callers use the async `EnqueueResponseAsync` variant.
- **Fix:** Remove both dead code paths or document their intended future use.

### FINDING-P04-13: Symlink TOCTOU Race in File Persistence (P2)

- **Source Anchor:** `ConfigStorage.cs:149-199`, `CustomConfigStorage.cs:82-100` (via inventory `H08_B065_H08.md` defect 5)
- **Mechanism:** `AssertNoSymlinksInPath` runs before file creation and `File.Move`. Between the check and the move, a local attacker could swap an entry for a symlink. .NET `File.Move` does not support `O_NOFOLLOW` or descriptor pinning.
- **Impact:** Requires local write access to the user's private 0700 directory, making practical exploitation extremely narrow.
- **Fix:** Accept as residual risk given the 0700 directory protection, or use `open(O_NOFOLLOW)` via platform interop if security posture requires it.

---

## 7. Summary of Findings

| ID | Severity | Component | Title |
|---|---|---|---|
| FINDING-P04-01 | **P0** | RouterBackend | Dead `_inFlightRequests` dictionary; cancel always returns false |
| FINDING-P04-02 | **P0** | RouterSession | `DisposeAsync` races gate holders; `ObjectDisposedException` crash |
| FINDING-P04-03 | **P0** | ConfigStorage + RouterBackend | Raw exception objects logged; zero-leak invariant violated |
| FINDING-P04-04 | **P1** | RouterSession | Dynamic readiness guard causes silent state desynchronization |
| FINDING-P04-05 | **P1** | ProtocolConstants + Dispatcher + Backend | `snapshot` classification triple-inconsistency |
| FINDING-P04-06 | **P1** | CustomConfigFeature | Non-atomic custom config removal; orphaned state on conflict |
| FINDING-P04-07 | **P1** | RouterBackend | Unhandled mutation cancellation; partial writes without rollback |
| FINDING-P04-08 | **P1** | SubscriptionFeature | Refresh loop aborts on first failure; data loss |
| FINDING-P04-09 | **P1** | RouterSession | Readiness guard execution under lock; deadlock risk |
| FINDING-P04-10 | **P2** | ConfigStorage | Global `AppPaths.OverrideDataDir` mutation in constructor |
| FINDING-P04-11 | **P2** | CustomConfigStorage | Best-effort permissions vs. ConfigStorage fail-closed |
| FINDING-P04-12 | **P2** | ProtocolConstants + OutputQueue | Dead code: unreferenced methods |
| FINDING-P04-13 | **P2** | ConfigStorage + CustomConfigStorage | Symlink TOCTOU race (narrow exploitation) |

---

## 8. Cross-Reference: Inventory Sheet Alignment

| Inventory Finding | This Review | Disposition |
|---|---|---|
| H02 6.1: Dead `IsUrgentOrReadOnlyMethod` | FINDING-P04-12 | Confirmed, subsumed into broader P1 triple-inconsistency (P04-05) |
| H02 6.2: Reader backpressure suspension | Section 2.3 | Confirmed as accepted tradeoff; documented |
| H02 6.3: Progress stamping race | Section 2.2 | Confirmed; mitigated by current handler patterns |
| H02 6.4: Event handler retention | Section 3.3 | Confirmed; minimal impact |
| H05 6.1: Dead `_inFlightRequests` | **FINDING-P04-01** | **Elevated to P0** (always-false cancel result is worse than dead code) |
| H05 6.2: Unsynchronized disposal race | **FINDING-P04-02** | **Elevated to P0** (crash, not just race) |
| H05 6.3: Guard under `_stateLock` | **FINDING-P04-09** | Confirmed P1 |
| H05 6.4: Dynamic `State`/`ErrorCode` divergence | **FINDING-P04-04** | Confirmed P1 |
| H07 Finding 3: Non-atomic sub refresh | **FINDING-P04-08** | Confirmed P1 |
| H07 Finding 4: Sub server remove ambiguity | Noted | Not elevated; correct behavior, error message could be clearer |
| H08 Defect 1: Raw exception logging mutex | **FINDING-P04-03** | Confirmed P0 |
| H08 Defect 2: Global `AppPaths` mutation | **FINDING-P04-10** | Confirmed P2 |
| H08 Defect 3: Unhandled IO in CustomConfigStorage | Merged into P04-11 | Confirmed P2 |
| H08 Defect 4: Asymmetric custom config removal | **FINDING-P04-06** | Confirmed P1 |
| H08 Defect 5: Symlink TOCTOU | **FINDING-P04-13** | Confirmed P2 |

---

**Review completed: 2026-09-26T14:35Z**
**Reviewer:** Opus adversarial worker (Lane P04)
**Status:** SUCCESS - 3 P0 / 5 P1 / 5 P2 findings identified
