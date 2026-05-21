# Phase 3+ — IProcessRunner adoption: SingBoxManager (long-lived, FINAL target)

**Owner**: Claude session (Phase 3+ batch, FINAL long-lived spawn target in Core)
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md` (FirewallManager + TunAdapter, `1242b9e`)
- `plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md` (HostsManager + ZapretActions, `cdf8157`)
- `plans/phase3-iprocessrunner-tgproxymanager-2026-05-21.md` (TgProxyManager, `8a5079e`)
- `plans/phase3-iprocessrunner-vlessdeepverifier-2026-05-21.md` (VlessDeepVerifier, `34bbeae`)
**Effort**: ~1.5 hours
**Risk**: MEDIUM (heaviest target — Exited→Crashed event hop, Linux/macOS
elevation chain, GetMetrics introspection)
**Blast radius**: 1 service file · 1 spawn callsite · 2 callbacks · GetMetrics seam
**Rollback**: `git revert <commit>` — the seam is additive (IProcessHandle
gains `TryGetSnapshot`; ProcessRunner gains the impl; FakeProcessHandle
gains the stub). Reverting restores the direct `Process.Start` +
explicit `EnableRaisingEvents=false`-before-Kill pattern verbatim.

## Why

SingBoxManager is the LAST IProcessRunner migration target for the Core
long-lived spawn audit. Once it goes through the seam, every long-lived
spawn in Core is testable via `FakeProcessRunner` + `FakeProcessHandle`
— which UNBLOCKS Task #22 (`VpnEngine.StartAsync` invoke-test seam: the
full lifecycle becomes testable end-to-end without spawning real
sing-box).

Per the Phase 3+ long-lived spawn audit, SingBoxManager carries:

- `sing-box.exe` direct on Windows; `/usr/bin/sudo <exe>` on macOS;
  `/usr/bin/pkexec <exe>` or `<exe>` (capability mode) on Linux.
- Lifetime: minutes/hours, event-driven (Exited → Crashed → HealthMonitor
  auto-restart loop).
- **Load-bearing pattern at line 281 (pre-migration)**:
  `_process.EnableRaisingEvents = false; _process.Kill(entireProcessTree: true);`
  — suppresses false Crashed callback on graceful Stop. The migration
  makes this **implicit** via `ProcessHandle.Dispose` (ProcessRunner.cs
  lines 280-293) — no explicit set needed in StopInternal anymore.

## What

### A. `IProcessHandle` surface extension — `TryGetSnapshot()`

Minimal additive change: one new method + one new record type. Required
because `SingBoxManager.GetMetrics` / `IsHealthy` previously called
`_process.Refresh()` + `_process.WorkingSet64` / `TotalProcessorTime` /
`StartTime` directly on the underlying `Process`. The IProcessHandle
seam needs to surface this introspection through the seam itself —
otherwise the migration would require either an escape hatch (`Process?
Underlying { get; }`) or reading-stale-cached metrics.

```csharp
public interface IProcessHandle : IDisposable {
    // existing surface unchanged ...
    ProcessSnapshot? TryGetSnapshot();
}

public sealed record ProcessSnapshot(
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime,
    DateTime StartTime);
```

- **`ProcessRunner.ProcessHandle.TryGetSnapshot`**: refreshes the
  underlying Process via `Refresh()`, returns the snapshot. Catches
  all exceptions and returns null (post-exit / permission-denied races).
- **`FakeProcessHandle.TryGetSnapshot`**: returns the test-set
  `SnapshotStub` (default null = "metrics unavailable"). Tests pin
  GetMetrics empty-default via this default.

### B. SingBoxManager migration

1. **Field swap**: `Process? _process` → `IProcessHandle? _handle`. The
   handle owns Process lifetime, stream wiring, and the implicit
   EnableRaisingEvents=false-before-Kill pattern.
2. **Static `Runner` seam + ctor injection**: mirrors TgProxyManager.
   `internal static IProcessRunner Runner { get; set; } = new ProcessRunner();`
   + `IProcessRunner? runner = null` ctor parameter.
3. **`LaunchProcess` migration**: build `ProcessRequest` with executable
   + argv list (was `ProcessStartInfo` with single-string `Arguments`).
   Linux pkexec + macOS sudo path encoded as argv tokens (sudo + exe +
   args), NOT as a separate spawn branch — same IProcessRunner.Start
   call site.
4. **Event wiring**: replace `Process.OutputDataReceived/ErrorDataReceived`
   with `IProcessHandle.OutputLine/ErrorLine`. Replace `Process.Exited`
   adapter with `IProcessHandle.Exited`. Signature shift:
   `Process.Exited` → `EventHandler` (no args) becomes
   `IProcessHandle.Exited` → `EventHandler<int>` (exit code). The legacy
   `(_, _) => OnProcessExited()` adapter ignores the code; OnProcessExited
   reads it via `_handle.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult()`
   on the already-exited handle (returns sync with cached code).
5. **`StopInternal` migration**: remove the explicit
   `_process.EnableRaisingEvents = false;` line. Replace
   `_process.Kill(entireProcessTree: true)` + `_process.WaitForExit(5000)`
   with `_handle.Kill(true)` + `_handle.WaitForExitAsync(linkedCts.Token)`
   inside a 5 s CTS. `_handle.Dispose()` triggers the implicit
   `EnableRaisingEvents=false → Kill → Dispose` chain inside
   ProcessHandle.Dispose, preserving the no-spurious-Crashed invariant
   transitively.
6. **`Pid` / `IsRunning` / `IsHealthy` / `GetMetrics`**: update field
   access from `_process.` to `_handle.`. Metrics route through
   `_handle.TryGetSnapshot()`. Linux capability-mode + pkexec branches
   in StopInternal and OnProcessExited preserve the same semantic
   behaviour but through the handle abstraction.

### C. Test migration

1. **Source-pin moved out of SingBoxManagerStateMachineTests**: the
   `Stop_DisablesEventsBeforeKill_SourcePin` test is DELETED. The
   invariant moved to `ProcessHandle.Dispose` (ProcessRunner.cs); the
   new file `ProcessHandleDisposeOrderingTests.cs` pins it at the new
   location. Cleaner separation of concerns — the invariant belongs to
   the seam, not to one of its consumers.
2. **`TryHotReload_PutShape_PreservedAfter_3G2_Migration`**: updated
   to poke `_handle` with a `FakeProcessHandle` instead of `_process`
   with a real long-lived child process. Removes the
   `SpawnLongLivedChild()` reliance for this test.
3. **`Restart_PreservesTunLock_SourcePin`** and
   `IHttpClient_FieldIsNonStatic_SourcePin_3G2` stay unchanged.

### D. New test files

1. **`VPNRouter.Tests/ProcessHandleDisposeOrderingTests.cs`** — 2 tests
   pinning the EnableRaisingEvents=false-before-Kill ordering inside
   ProcessHandle.Dispose. Belt-and-braces companion pin verifies the
   standalone Kill() method does NOT touch EnableRaisingEvents (only
   Dispose flips it).
2. **`VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs`** — 7 tests
   covering:
   - LaunchProcess argv shape pin (Windows direct spawn).
   - `handle.Exited` → `Crashed` event mapping (sync via
     FakeProcessHandle).
   - Stop's kill+wait+dispose sequence on a running handle.
   - Stop idempotency post-migration (second call no-op).
   - Restart preserves TUN lock + spawns a fresh handle.
   - Default Runner is a production ProcessRunner.
   - Ctor `runner:` parameter overrides static default.

## Verification gates

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors
- [x] All 18 existing SingBoxManagerStateMachineTests stay green (was 19;
  −1 source-pin migrated to ProcessHandleDisposeOrderingTests).
- [x] All 7 existing SingBoxManagerRestartTunHandshakeTests stay green.
- [x] 7 new SingBoxManagerProcessRunnerTests green.
- [x] 2 new ProcessHandleDisposeOrderingTests green.
- [x] All 27 process-runner-related tests across SingBox/Zapret/VlessDeep
  stay green.
- [x] Full suite (excl. headless / page-screenshot / visual-diff):
  **1294 pass / 4 skip / 0 fail** (vs baseline 1279 + 9 new tests
  + intermediate adjustments = net +15).

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/IProcessRunner.cs` | +28 LOC. Added `IProcessHandle.TryGetSnapshot()` + `ProcessSnapshot` record. |
| `VPNRouter.Core/Services/ProcessRunner.cs` | +18 LOC. Implements `TryGetSnapshot` (refreshes Process, returns snapshot or null). |
| `VPNRouter.Core/Services/SingBoxManager.cs` | ~70 LOC delta (mostly substitution `_process → _handle` + Linux/macOS/Windows spawn path collapsed to single ProcessRequest construction). Removed explicit `EnableRaisingEvents=false` at 3 sites (pattern moved to ProcessHandle.Dispose). |
| `VPNRouter.Tests/Fakes/FakeProcessRunner.cs` | +18 LOC. `SnapshotStub` + `SnapshotCallCount` + `TryGetSnapshot()` stub. |
| `VPNRouter.Tests/SingBoxManagerStateMachineTests.cs` | −60 / +20 LOC. Deleted `Stop_DisablesEventsBeforeKill_SourcePin` (moved to new file). Refactored `TryHotReload_PutShape_PreservedAfter_3G2_Migration` to poke `_handle` instead of `_process`. |
| `VPNRouter.Tests/ProcessHandleDisposeOrderingTests.cs` | +165 LOC (new file). 2 source-pin tests for the centralised Dispose-ordering invariant. |
| `VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs` | +274 LOC (new file). 7 wire-shape tests for the IProcessRunner adoption. |
| `plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md` | This brief. |

### Wire-shape invariants preserved

- argv: `["run", "-c", <currentConfigPath>]` on Windows + Linux
  capability mode. macOS prepends `<exe>` after `/usr/bin/sudo`:
  `["<exe>", "run", "-c", <path>]`. Linux pkexec same shape but with
  `/usr/bin/pkexec`. (Legacy single-string `Arguments` → argv list,
  semantically equivalent under ArgumentList-based quoting.)
- `CaptureStdout: true` + `CaptureStderr: true` (legacy
  `RedirectStandardOutput/Error = true`).
- Stream subscription via `handle.OutputLine` / `handle.ErrorLine`
  events (replaces `OutputDataReceived` / `ErrorDataReceived`).
- Idempotent Kill via `handle.Kill(true)` + `Dispose()` (entireProcessTree:true matches legacy).
- 5 s wait-after-Kill bound preserved via `WaitForExitAsync(linkedCts)`.
- Linux capability-mode (no pkexec) + pkexec elevation chain selection
  driven by `HasNetCapability(exePath)` — unchanged.
- macOS sudo NOPASSWD chain — unchanged.
- Linux StopEscalationChain (pkill / pkexec pkill / sudo -n pkill)
  unchanged (not part of the seam — short-lived helper spawns left
  as direct Process.Start calls for now).

### Surprises encountered

1. **`TryGetSnapshot` was unavoidable.** Initial plan was "no surface
   extension". But `SingBoxManager.GetMetrics` reads
   `WorkingSet64 / TotalProcessorTime / StartTime` directly off the
   underlying Process — without a snapshot method on IProcessHandle,
   the only alternatives were (a) expose `Process? Underlying` (escape
   hatch — ugly), or (b) keep `_process` field alongside `_handle`
   (defeats the seam). One method + one record stays minimal and
   additive.
2. **Implicit-EnableRaisingEvents-via-Dispose pattern transferred
   cleanly.** All 17 existing SingBoxManager state-machine tests
   passed unmodified after the migration; the
   `SingBoxManagerRestartTunHandshakeTests` (7 source-string pins for
   the Wave-38 TUN cleanup) ALL stayed green — they pin
   `LaunchProcess`'s outer behaviour (PreStartCleanup,
   EnsureAdapterEnabledOrAbsent absence), which the migration didn't
   touch.
3. **OnProcessExited adapter signature**. Process.Exited's
   `(object?, EventArgs)` callback became IProcessHandle.Exited's
   `EventHandler<int>` (exit code as arg). I wired the adapter as
   `(_, _) => OnProcessExited()` to preserve the legacy
   parameterless signature, then re-read the exit code via
   `_handle.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult()`
   inside OnProcessExited. This is a sync no-op on an already-exited
   handle (returns the cached code immediately) — same observable
   shape as the pre-migration `_process.ExitCode` read.
4. **Source-pin migration was cleaner than expected.** The deleted
   `Stop_DisablesEventsBeforeKill_SourcePin` test had ~60 LOC of
   commentary explaining the historical race contexts; the
   replacement `Dispose_DisablesEventsBeforeKill_SourcePin` in the new
   file is leaner because the invariant lives in one place now
   (ProcessHandle.Dispose, ~10 LOC). Belt-and-braces companion pin
   covers the inverse: standalone Kill() does NOT flip
   EnableRaisingEvents (only Dispose does).

### Follow-ups spawned

- **Task #22**: `VpnEngine.StartAsync` invoke-test seam now unblocked.
  With SingBoxManager spawning through IProcessRunner, the full Apply
  / StartAsync lifecycle can be driven through a FakeProcessRunner +
  FakeProcessHandle pair without invoking real sing-box.
- **LinuxStopEscalationChain** + `TrySpawnAndWait` + `IsSingBoxAlive`
  (pgrep): short-lived stop-side helpers still call `Process.Start`
  directly. Not in this brief's scope; migration is a follow-up batch
  (low priority — they're fire-and-forget kill helpers with no
  observable race against the long-lived sing-box spawn).
- **`HasNetCapability(exePath)`**: Linux-only getcap probe spawns
  `/usr/sbin/getcap` directly. Could go through IProcessRunner.RunAsync
  in a future cleanup batch. Low value — it's a one-shot startup
  probe, already wrapped in defensive try/catch.

### Brief

`plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md` (this file).
