# Phase 3+ — IProcessRunner adoption: ZapretManager (long-lived spawn)

**Owner**: Claude session (Phase 3+ batch, third long-lived target)
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md` (leaf, `1242b9e`)
- `plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md` (leaf, `cdf8157`)
- `plans/phase3-iprocessrunner-tgproxymanager-2026-05-21.md` (long-lived, `8a5079e`)
- `plans/phase3-iprocessrunner-vlessdeepverifier-2026-05-21.md` (long-lived, `34bbeae`)

**Parallel batch**: SingBoxManager migration (different file, ran concurrently
in the same working tree; parallel agent owned `IProcessRunner.cs`
`ProcessRunner.cs` `FakeProcessRunner.cs` `SingBoxManager.cs`).

**Effort**: ~1.5 hours
**Risk**: MEDIUM-HIGH — long-lived spawn with the highest design-constraint
load of any Phase 3+ migration so far. The legacy spawn used
`UseShellExecute=true` to launch a `.bat` file directly; ProcessRunner
hardwires `UseShellExecute=false`, which cannot exec a `.bat`. Migration
introduces an explicit `cmd.exe /c <bat>` wrapper. Three constraints had
to hold simultaneously:

1. Wrapper must invoke the existing `.bat` content **unchanged** (Cygwin
   `SET BIN=`/`SET LISTS=` contract from CLAUDE.md "Zapret (DPI Bypass)"
   §critical).
2. winws.exe child of cmd.exe must inherit a **real (hidden) console**
   — the Cygwin POSIX path resolver fails when stdout is pipe-redirected.
   Routing this through `CaptureStdout=false, CaptureStderr=false` keeps
   the runner from redirecting cmd.exe streams; `CreateNoWindow=true` on
   the runner side gives us the hidden-console requirement.
3. ImmediateExit detection must remain wired (Bug-r9-G AV-kill toast)
   with the **same 2s window** and **same non-zero-code gate**. Mapped
   onto `IProcessHandle.Exited` event.

**Blast radius**: 1 service file · 2 callsites (Start + StartFromBat) · 0
production behavior change · `OutputReceived` event preserved (dead
surface — never invoked anywhere, both pre- and post-migration).
**Rollback**: `git revert <commit>` — the seam is additive; reverting
restores direct `Process.Start` + `UseShellExecute=true` + sync
`WaitForExit(3000)` in `ZapretManager.Start` / `.StartFromBat` verbatim.

## Why

Per the Phase 3+ long-lived spawn audit, `ZapretManager` was the third
remaining direct-`Process` consumer in Core (after `TgProxyManager`
`8a5079e` and `VlessDeepVerifier` `34bbeae`). `SingBoxManager` was the
fourth, migrated in parallel by a sibling agent in the same batch.

`ZapretManager` is the most constrained migration target so far because
its legacy spawn relied on `UseShellExecute=true` (the only way Windows
will exec a `.bat` directly) combined with `WindowStyle=Hidden`. The
runner's hardwired `UseShellExecute=false` (a deliberate security
invariant — see ProcessRunner.cs:172 "we use ArgumentList not Arguments
string …") cannot exec a `.bat`. Wrapping with `cmd.exe /c <bat>` is the
canonical workaround, but it raises a second concern: the Cygwin-built
winws.exe binary requires a **real console** at its stdout/stderr file
descriptors (per the existing comments at lines 153-156 + CLAUDE.md
"Zapret (DPI Bypass)" §critical). If the runner redirects cmd.exe's
streams, winws.exe inherits pipe-FDs instead of console-FDs and exits
silently with "cannot access file" errors.

Solution: set `CaptureStdout=false, CaptureStderr=false` in the
ProcessRequest so the runner skips redirection. With `CreateNoWindow=true`
still in effect, cmd.exe gets a hidden console, which propagates to
winws.exe at exec time. Pinned by
`Start_RoutesThroughCmdBat_DoesNotRedirectStreams`.

## What

### A. ZapretManager.cs — 2 callsites migrated

Two spawn paths exist:

- `Start(string args)` — synth a `_vpnrouter_launch.bat` with the
  Cygwin SET BIN=/SET LISTS= wrapper, then invoke it.
- `StartFromBat(string batPath, string parsedArgs)` — synth a
  `_vpnrouter_silent.bat` next to the user-supplied strategy `.bat`
  (which calls `service.bat status_zapret` etc. for prologue), then
  invoke it with `WorkingDirectory=zapretDir` so the relative
  `call service.bat` invocations inside the wrapper resolve.

Both now route through a new private helper:

```csharp
private IProcessHandle StartCmdBat(string batPath, string? workingDir)
{
    var request = new ProcessRequest(
        ExecutablePath: "cmd.exe",
        Arguments: new List<string> { "/c", batPath },
        WorkingDirectory: workingDir,
        CaptureStdout: false,    // Cygwin "real console" requirement
        CaptureStderr: false);   // same

    return _runner.Start(request);
}
```

### B. Seam plumbing (mirrors TgProxyManager pattern verbatim)

```csharp
internal static IProcessRunner Runner { get; set; } = new ProcessRunner();
private readonly IProcessRunner _runner;
private IProcessHandle? _handle;

public ZapretManager(ILogger? logger = null, IProcessRunner? runner = null)
{
    _logger = logger ?? Log.Logger;
    _runner = runner ?? Runner;
}
```

All three existing callsites (`new ZapretManager(Serilog.Log.Logger)` in
`VPNRouter.Service/VPNRouterService.cs:318`, `new ZapretManager(_logger)`
twice in `VPNRouter.App/ViewModels/MainWindowViewModel.cs:4105` +
`MainWindowViewModel.AutostartBootstrap.cs:241`) continue working
unchanged — backward compatible via optional `runner` parameter.

### C. ImmediateExit detection mapping

Legacy:
```csharp
_process.EnableRaisingEvents = true;
_process.Exited += (_, _) =>
{
    var runtime = DateTime.UtcNow - startedAt;
    var code = _process?.ExitCode;        // pulled from instance field
    DetectImmediateExit(runtime, code);
};
```

Post-migration:
```csharp
var startedHandle = _handle;
startedHandle.Exited += (_, code) =>     // code arrives as int payload
{
    var runtime = DateTime.UtcNow - startedAt;
    DetectImmediateExit(runtime, code);
};
```

`IProcessHandle.Exited` is `EventHandler<int>` (exit-code payload direct),
so the `_process?.ExitCode` lookup goes away. `EnableRaisingEvents=true`
is no longer needed at the SUT level — `ProcessRunner.Start`
(ProcessRunner.cs:155) wires it automatically. Same observable behaviour;
same 2s `ImmediateExitWindow`; same code==0 gate.

### D. Stop method migration

Legacy: `_process.Kill(entireProcessTree:true) + _process.WaitForExit(3000)`.
Post-migration: `handle.Kill(entireProcessTree:true) +
handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult()` with
a 3-second linked CTS. The `OperationCanceledException` on timeout is
caught and logged; `handle.Dispose()` in the finally block fires the
final kill (mirrors the `SingBoxManager.Stop` "disable Exited before
Kill" invariant, now owned transitively by `ProcessHandle.Dispose`
at ProcessRunner.cs:288-290).

### E. Cygwin `BuildCygwinLaunchBat` — untouched

The internal `BuildCygwinLaunchBat(binDir, listsDir, args)` helper is
not modified by this migration. The existing regression-prevention test
`ZapretActionsTests.BuildCygwinLaunchBat_UsesSetBinAndSetLists_NotLiteralPaths`
continues to pin the SET BIN=/SET LISTS=/cd /d %BIN% contract from the
v2.9.x lesson documented in CLAUDE.md "Zapret (DPI Bypass)".

### F. `OutputReceived` event — preserved as dead surface

`public event Action<string>? OutputReceived;` is declared but never
invoked anywhere in the codebase, both pre- and post-migration. Reason:
the legacy `UseShellExecute=true` spawn cannot redirect streams (Windows
disallows it), so OutputReceived had nothing to fire from. Migration
preserves the field for backward-compatibility (in case some external
consumer subscribes); no behavioural change.

### G. New unit tests

`VPNRouter.Tests/ZapretManagerProcessRunnerTests.cs` — **7 tests**:

| # | Test | What it pins |
|---|---|---|
| 1 | `Start_RoutesThroughCmdBat_DoesNotRedirectStreams` | cmd.exe + `/c` + bat path + CaptureStdout/Err = FALSE (Cygwin "real console" requirement) |
| 2 | `Start_HandleExitsWithin2sNonZero_FiresImmediateExitEvent` | ImmediateExit via handle.Exited within 2s + non-zero code → toast fires |
| 3 | `Start_HandleExitsWithin2sCodeZero_DoesNotFireImmediateExitEvent` | Code 0 = normal stop → no toast false-positive |
| 4 | `Stop_OnRunningManager_KillsHandleAndDisposes` | Kill + WaitForExitAsync + Dispose + IsRunning=false post-stop |
| 5 | `Stop_CalledTwice_SecondCallIsNoOp` | Idempotent — 2nd/3rd Stop don't throw or double-dispose |
| 6 | `Constructor_AcceptsCustomRunner_WiresUpInjection` | ctor accepts FakeProcessRunner; null falls back to static Runner |
| 7 | `StartFromBat_RoutesThroughCmdBat_WithZapretDirAsWorkingDir` | Flowseal-wrapper path: cmd.exe + /c + `_vpnrouter_silent.bat` + WorkingDirectory=zapretDir + streams non-redirected |

The Cygwin .bat content regression test
(`BuildCygwinLaunchBat_UsesSetBinAndSetLists_NotLiteralPaths`) already
lives in `ZapretActionsTests` and was NOT duplicated — duplicate
coverage adds no value.

Tests seed a stub `winws.exe` at the real `ZapretUpdater.WinwsExePath`
location so the `File.Exists(exePath)` guard at the top of `Start`
passes through to the runner. `_seededStub` tracking ensures the
IDisposable teardown only removes files we created (no clobbering a
real install on the dev machine).

## How

### Step 1: Seam plumbing (ZapretManager)

Mirrors TgProxyManager pattern verbatim: static `Runner` property + ctor
parameter + `_runner` field.

### Step 2: Both spawn paths

Legacy spawn (Start path):
```csharp
var psi = new ProcessStartInfo
{
    FileName = batPath,
    UseShellExecute = true,
    WindowStyle = ProcessWindowStyle.Hidden
};
_process = Process.Start(psi);
_process.EnableRaisingEvents = true;
_process.Exited += (_, _) => { ... };
```

Post-migration via shared helper:
```csharp
_handle = StartCmdBat(batPath, workingDir: null);
var startedHandle = _handle;
startedHandle.Exited += (_, code) => { ... };
```

### Step 3: Stop method migration

Same shape as `TgProxyManager.Stop`:
```csharp
handle.Kill(entireProcessTree: true);
using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
try
{
    handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult();
}
catch (OperationCanceledException) { /* 3s elapsed; Dispose finalises */ }
finally { handle.Dispose(); }
```

## Verification gate

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors, 0 warnings on
      the ZapretManager / new test files.
- [x] Zapret-related tests: **21/21 pass** (14 existing in
      ZapretActionsTests + 7 new in ZapretManagerProcessRunnerTests).
- [x] Full suite (excl. PageScreenshot / Headless / VisualDiff):
      **1284 pass / 4 skip / 2 fail** — the 2 failures are
      `SingBoxManagerStateMachineTests` (parallel agent's WIP scope, not
      this brief's surface). Verified by running with SingBoxManager.cs
      reset to HEAD: **1286 pass / 4 skip / 0 fail** (1279 baseline + 7
      new tests = 1286, exact match).

## Outcome

### Files touched (this brief only)

| File | Change |
|---|---|
| `VPNRouter.Core/Services/ZapretManager.cs` | +90 / −40 LOC. 2 callsites migrated (Start + StartFromBat). New `Runner` static seam + `IProcessRunner? runner` ctor parameter. `Process? _process` field replaced by `IProcessHandle? _handle`. New private `StartCmdBat(batPath, workingDir)` helper centralises the cmd.exe wrapping. ImmediateExit detection rewired to `handle.Exited` (code via int payload, no `_process?.ExitCode` lookup). Stop method uses Kill + WaitForExitAsync + Dispose mirroring TgProxyManager. |
| `VPNRouter.Tests/ZapretManagerProcessRunnerTests.cs` | +290 LOC (new file). 7 tests pinning cmd.exe + /c argv shape, Cygwin-required CaptureStdout/Err=false, ImmediateExit via handle.Exited (2s window, non-zero gate, zero-code negative case), Kill+WaitForExitAsync Stop, idempotent Stop, ctor injection, StartFromBat working-dir contract. |
| `plans/phase3-iprocessrunner-zapretmanager-2026-05-21.md` | This brief. |

### Files NOT touched (parallel agent's scope)

| File | Owner |
|---|---|
| `VPNRouter.Core/Services/IProcessRunner.cs` | Parallel SingBoxManager agent (added `TryGetSnapshot()` + `ProcessSnapshot` record). |
| `VPNRouter.Core/Services/ProcessRunner.cs` | Parallel agent (impl of `TryGetSnapshot`). |
| `VPNRouter.Core/Services/SingBoxManager.cs` | Parallel agent (full migration of sing-box spawn). |
| `VPNRouter.Tests/Fakes/FakeProcessRunner.cs` | Parallel agent (added `SnapshotStub` + `SnapshotCallCount` + `TryGetSnapshot` to `FakeProcessHandle`). |

These were committed by the parallel agent in their own commit; this
brief did not modify them. The IProcessHandle surface used by
ZapretManager is the pre-existing base (`Kill`, `Pid`, `HasExited`,
`WaitForExitAsync`, `Dispose`, `Exited` event) — no dependency on the
new `TryGetSnapshot` surface, so this migration would have worked on
either side of the parallel agent's seam expansion.

### Test deltas

- Baseline (predecessor batch `34bbeae`): 1279 pass / 4 skip / 0 fail.
- After this batch (in isolation, SingBoxManager.cs at HEAD): 1286 pass
  / 4 skip / 0 fail (**+7 tests**).
- After this batch + parallel SingBoxManager agent's WIP (current
  working tree state): 1284 pass / 4 skip / 2 fail. The 2 fails are
  SingBoxManagerStateMachineTests — owned by the parallel agent's
  commit, not this one.
- Zapret-related suites: 21/21 green (was 14/14 pre-batch).

### Surprises encountered

1. **`UseShellExecute=true` + `.bat` is structurally unreachable through
   IProcessRunner.** The runner's hardwired `UseShellExecute=false`
   (ProcessRunner.cs:172, a deliberate security invariant) cannot exec
   a `.bat` directly because `CreateProcess` doesn't know about
   Windows shell associations. Required wrapping: `cmd.exe /c <bat>`.
   Documented in the shared `StartCmdBat` helper.

2. **Cygwin "real console" requirement vs stream-redirected runner.**
   Per CLAUDE.md "Zapret (DPI Bypass)" §critical + the existing comments
   at ZapretManager.Start lines 153-156, winws.exe needs an inherited
   console — pipe-redirected stdout makes its Cygwin POSIX resolver
   fail silently. Solution: `CaptureStdout=false, CaptureStderr=false`
   in the ProcessRequest tells the runner not to redirect, so cmd.exe
   inherits the `CreateNoWindow=true` hidden console and propagates it
   to winws.exe at exec time. Pinned by 2 tests (one per spawn path).

3. **`OutputReceived` event is dead in both pre- and post-migration code.**
   The legacy spawn used `UseShellExecute=true` which cannot redirect
   streams (Windows rejects the PSI as inconsistent), so OutputReceived
   never had stdout to fire from. Post-migration we also don't redirect
   (Cygwin requirement), so the event remains dead. Preserved for
   backward compat — external consumers could subscribe, even if they'd
   never receive callbacks.

4. **ImmediateExit timing maps cleanly to `handle.Exited`**. The 2s
   `ImmediateExitWindow` is a SUT-side constant, not a runner-side
   concern; the rule "exit within 2s + non-zero code = AV kill" lives
   in `DetectImmediateExit`. The migration just rewires the event source
   from `_process.Exited` (EventArgs payload, code looked up via
   `_process?.ExitCode`) to `handle.Exited` (EventHandler<int>, code
   arrives as direct payload). Same observable semantics. Pinned by
   2 tests (positive case: code -1 within ~ms → toast fires; negative
   case: code 0 → no toast).

5. **Parallel agent expanded the IProcessHandle interface mid-batch.**
   The SingBoxManager agent added `TryGetSnapshot()` to `IProcessHandle`
   for the metrics-refresh path. ZapretManager doesn't use that surface,
   so the migrations are independent. The parallel agent owns the
   `FakeProcessHandle.TryGetSnapshot` impl and the `ProcessSnapshot`
   record; no coordination overhead from this side.

### Wire-shape invariants preserved

- ProcessRequest shape: cmd.exe + ["/c", batPath] (2-token argv). Pinned
  by 2 tests (one per spawn path).
- CaptureStdout / CaptureStderr = false. Pinned by 2 tests.
- ImmediateExit window: `TimeSpan.FromSeconds(2)`. Same constant
  (`ZapretManager.ImmediateExitWindow`).
- ImmediateExit gate: code != 0 within 2s. Pinned by 2 tests (positive
  + negative).
- Stop sync barrier: 3s. Pinned implicitly by FakeProcessHandle's Kill
  -> SignalExit pattern.
- Idempotent Stop: 2nd/3rd Stop calls no-op. Pinned by 1 test.
- Cygwin .bat content: SET BIN= / SET LISTS= / cd /d %BIN% / winws.exe.
  Untouched by this brief; pinned by the existing
  `BuildCygwinLaunchBat_UsesSetBinAndSetLists_NotLiteralPaths` in
  ZapretActionsTests.

### Follow-ups spawned

- **None directly from this brief.** The remaining audited direct-Process
  consumers (post-this-batch) are short-lived stop-side fire-and-forgets
  (Linux pkexec / sudo helpers, pgrep liveness probes). Those will be
  picked up in a separate phase-3+ wave.

## Coordination note

A parallel agent migrated `SingBoxManager` in the same Phase 3+ batch.
That agent extended the shared seam (added `TryGetSnapshot()` to
`IProcessHandle`, `ProcessSnapshot` record, `FakeProcessHandle` test
shim) for the SingBoxManager metrics path. This brief touched **only**
`VPNRouter.Core/Services/ZapretManager.cs` and
`VPNRouter.Tests/ZapretManagerProcessRunnerTests.cs`. **No modifications
to `IProcessRunner.cs`, `ProcessRunner.cs`, `FakeProcessRunner.cs`, or
`SingBoxManager.cs`** — those belong to the parallel agent's commit.
The ZapretManager migration would have worked unchanged on either side
of the seam expansion (it doesn't consume the new `TryGetSnapshot`
surface).
