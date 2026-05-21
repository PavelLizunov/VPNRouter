# Phase 3+ — IProcessRunner adoption: TgProxyManager (long-lived spawn)

**Owner**: Claude session (Phase 3+ batch, second long-lived target)
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md` (leaf batch, commit `1242b9e`)
- `plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md` (leaf batch + ctor-injection pattern, commit `cdf8157`)

**Parallel batch**: VlessDeepVerifier migration (different file, no
conflict; commit unknown at write-time).

**Effort**: ~1 hour
**Risk**: MEDIUM — long-lived spawn with OutputDataReceived event wiring + a
2-second post-spawn sync probe + a stderr drain on early-exit. Three observable
behaviours had to be re-mapped onto the IProcessHandle event shape; each is
covered by a new wire-shape test below.
**Blast radius**: 1 service file · 1 callsite · 0 production behavior change
**Rollback**: `git revert <commit>` — the seam is additive; reverting restores
direct `Process.Start` + sync `WaitForExit(2000)` in `TgProxyManager.Start`
verbatim.

## Why

The Phase 3+ adoption sweep prioritised the heaviest **netsh** callers
(`FirewallManager` + `TunAdapterDiagnostics`, commit `1242b9e`) and the
**ipconfig** caller (`HostsManager`, commit `cdf8157`) first because those
are leaf-services with simple `RunAsync` semantics. The next tier the
audit flagged is **long-lived spawn services** — the python.exe child
(`TgProxyManager`), the sing-box child (`SingBoxManager`), and the Zapret
binary (`ZapretManager`).

`TgProxyManager` is the "medium complexity" entry in that tier per the
brief's task description: it spawns python.exe as a daemon, subscribes to
both stdout and stderr via a unified `OnOutputData` handler, AND runs a
2-second post-spawn watchdog probe (added in v2.31.10 — see existing
`TgProxyAutostartLoggingTests`) to surface Python embeddable startup
failures (missing wheels, broken `._pth`, port-in-use). Each of those
three behaviours has a wire-shape invariant that needs pinning so a
future refactor of the IProcessRunner seam doesn't silently break the
tg-proxy autostart.

This batch is the **second long-lived spawn** target migration (after
`HostsManager` which is leaf-leaning) and the natural prerequisite for
the harder `SingBoxManager` migration that comes next.

## What

### A. TgProxyManager.cs — 1 callsite migrated

Three observable behaviours rewired onto the IProcessHandle event shape:

1. **Spawn**: `Process.Start(psi)` → `_runner.Start(new ProcessRequest(...))`.
   The legacy single `Arguments` STRING (e.g. `"-m proxy.tg_ws_proxy
   --port 1443 --host 127.0.0.1 --secret abc"`) became a `List<string>`
   built by enumerating each positional token. Safe because the legacy
   string was already shell-parseable as a list of bare tokens — no value
   contains whitespace. ProcessStartInfo properties (UseShellExecute=false,
   CreateNoWindow=true, RedirectStandardOutput/Error=true) are now owned
   by `ProcessRunner.BuildStartInfo` via the request shape.

2. **Stream wiring**: `OutputDataReceived += OnOutputData` AND
   `ErrorDataReceived += OnOutputData` collapsed into separate
   `OnOutputLineHandler` (stdout) and `OnErrorLineHandler` (stderr) wired
   to `handle.OutputLine` / `handle.ErrorLine` respectively. Both still
   feed the same `StatsUpdated` event / `LastStats` property — pre-fix
   stats lines arrived on whichever stream Python happened to choose, so
   the fan-out is preserved.

3. **2s startup probe**: sync `_process.WaitForExit(2000)` → async
   `handle.WaitForExitAsync(probeCts.Token).GetAwaiter().GetResult()`
   with a 2-second linked CTS. The OperationCanceledException raised
   when the budget elapses takes the "process still alive after 2s"
   log path; the natural exit takes the "exited within 2s" log path.
   Same observable budget, same log lines, different sync mechanism.

4. **Early-exit stderr tail**: `_process.StandardError.ReadToEnd()` is
   **unreachable** through `IProcessHandle` by design (stderr is consumed
   exclusively via the `ErrorLine` event stream). Replacement: a
   `StringBuilder _capturedStderr` ring buffer (capped at 16 KiB) is
   appended by `OnErrorLineHandler` as lines arrive, and the early-exit
   log path emits the buffer's contents. Same observable result (an
   error tail in the log on Python startup failure), different sourcing.

5. **EnableRaisingEvents**: the legacy code set
   `_process.EnableRaisingEvents = true` explicitly. IProcessHandle's
   constructor wires this automatically (`ProcessRunner.cs:155`), so the
   line drops with no behavioural change.

6. **Stop**: `_process.Kill(entireProcessTree:true) +
   _process.WaitForExit(3000)` → `_handle.Kill(entireProcessTree:true) +
   _handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult()`
   with a 3-second linked CTS. Same kill semantics, same 3s
   synchronisation barrier. The implicit "disable Exited callback
   before Kill" invariant from `SingBoxManager.Stop` is now owned by
   `ProcessHandle.Dispose` (ProcessRunner.cs:288-290), called from
   the finally block.

### B. Seam plumbing (mirrors HostsManager pattern)

```csharp
// internal static seam (test fallback)
internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

// per-instance field for ctor injection
private readonly IProcessRunner _runner;

// ctor: appended optional parameter, backward compatible
public TgProxyManager(ILogger? logger = null, IProcessRunner? runner = null)
{
    _logger = logger ?? Log.Logger;
    _runner = runner ?? Runner;
}
```

All three existing callsites (`new TgProxyManager(Serilog.Log.Logger)` in
`VPNRouterService.cs:400`, `new TgProxyManager(_logger)` twice in
`VPNRouter.App/ViewModels/MainWindowViewModel.cs:4445` +
`MainWindowViewModel.AutostartBootstrap.cs:157`) continue working
unchanged — backward compatible.

### C. Existing tests — TgProxyAutostartLoggingTests source-pin update

`TgProxyManager_Start_LogsRedactedPsiAndPostSpawnProbe` (line 68-100) was
pinning the literal string `"WaitForExit(2000)"` to guarantee the 2s
probe didn't get accidentally dropped. The Phase 3+ migration explicitly
changes this to `WaitForExitAsync(probeCts.Token)`, so the pin had to
move. New pins:

- `"WaitForExitAsync"` — pins the new async sync mechanism survives.
- `"FromMilliseconds(2000)"` — pins the 2-second budget literal survives.
- `"within 2s"` — preserved (still in the log line).
- `"ExitCode"`, `"StandardError"` — preserved (both still in the log
  template).

Net effect on coverage: same — the assertions still catch any future
refactor that drops the probe entirely, just expressed in the post-Phase-3+
shape.

### D. New unit tests

`VPNRouter.Tests/TgProxyManagerProcessRunnerTests.cs` — **8 tests**:

| # | Test | What it pins |
|---|---|---|
| 1 | `Start_EmitsExpectedPythonArgvOnRunner` | Argv shape: `-m proxy.tg_ws_proxy --port <p> --host 127.0.0.1 --secret <s>` + working directory + Capture flags |
| 2 | `Start_WithVerbose_AppendsVerboseFlagToArgv` | `--verbose` is a tail-append (count=9 vs base 8) |
| 3 | `Start_ProcessSurvives2sProbe_LogsAliveAndContinues` | 2s linked-CTS budget — handle alive after Start returns, manager state = running |
| 4 | `Start_OutputLineWithStats_TriggersStatsUpdatedAndLastStats` | Both stdout and stderr feed StatsUpdated; non-stats lines don't update LastStats |
| 5 | `Stop_OnRunningManager_KillsHandleAndDisposes` | Stop kills + WaitForExitAsync + disposes; IsRunning=false post-stop |
| 6 | `Stop_CalledTwice_SecondCallIsNoOp` | Idempotent: second/third Stop calls don't throw |
| 7 | `Constructor_AcceptsCustomRunner_WiresUpInjection` | ctor accepts FakeProcessRunner; null falls back to static Runner |
| 8 | `RedactSecretInArgs_StillSanitisesLegacyArgsString` | Defence-in-depth: redaction helper still works on the legacy string used by the log call |

Tests seed stub Python files under `%ProgramData%\VPNRouter\tg-proxy\python\python.exe`
(only if not already present from a real install — the IDisposable cleanup
only removes files it created) and inject `FakeProcessRunner` via the new
ctor parameter. The Start method runs on a worker thread because the 2s
probe is sync-blocking by design.

## How

### Step 1: Seam plumbing (TgProxyManager)

Mirrors HostsManager pattern verbatim: static `Runner` property + ctor
parameter + `_runner` field.

### Step 2: argv migration

Legacy:
```csharp
var args = $"-m proxy.tg_ws_proxy --port {port} --host 127.0.0.1 --secret {secret}";
if (verbose) args += " --verbose";
var psi = new ProcessStartInfo
{
    FileName = TgProxyUpdater.PythonExePath,
    Arguments = args,
    WorkingDirectory = TgProxyUpdater.TgProxyDir,
    ...
};
```

Post-migration:
```csharp
var args = $"-m proxy.tg_ws_proxy --port {port} --host 127.0.0.1 --secret {secret}";
if (verbose) args += " --verbose";
var redactedArgs = RedactSecretInArgs(args);  // legacy log path preserved

var argv = new List<string>
{
    "-m", "proxy.tg_ws_proxy",
    "--port", port.ToString(),
    "--host", "127.0.0.1",
    "--secret", secret,
};
if (verbose) argv.Add("--verbose");

var request = new ProcessRequest(
    ExecutablePath: TgProxyUpdater.PythonExePath,
    Arguments: argv,
    WorkingDirectory: TgProxyUpdater.TgProxyDir,
    CaptureStdout: true,
    CaptureStderr: true);

_handle = _runner.Start(request);
```

Both `args` and `argv` coexist: `args` (string) feeds the existing
redaction-aware log line; `argv` (list) feeds the runner. Future refactor
could collapse to a builder, but keeping them parallel preserves the
defence-in-depth pin (`RedactSecretInArgs_StillSanitisesLegacyArgsString`).

### Step 3: 2s probe migration

Legacy sync:
```csharp
if (_process.WaitForExit(2000)) {
    // exited early — log error + drain StandardError
} else {
    // still alive — log info
}
```

Post-migration async-sync:
```csharp
using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
try {
    var exitCode = startedHandle.WaitForExitAsync(probeCts.Token)
        .GetAwaiter().GetResult();
    // exited early — log error + emit stderr buffer
}
catch (OperationCanceledException) {
    // still alive — log info
}
```

### Step 4: Stop method migration

Legacy:
```csharp
_process.Kill(entireProcessTree: true);
_process.WaitForExit(3000);
// ...
_process.Dispose();
```

Post-migration:
```csharp
handle.Kill(entireProcessTree: true);
using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
try {
    handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult();
}
catch (OperationCanceledException) { /* 3s elapsed — Dispose fires final kill */ }
// ...
handle.Dispose();
```

## Verification gate

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors, 203 warnings
      (same as predecessor batch — xUnit1031/1051 style nits across the
      test suite, unchanged by this PR).
- [x] TgProxy-related tests: **31/31 pass** (1 skip — pre-existing
      `AutostartContractTests.App_TgProxyAutostart_GuardsAgainstServiceDoubleStart`,
      unrelated).
- [x] Full suite (excl. PageScreenshot / Headless / VisualDiff):
      **1274 pass / 4 skip / 0 fail** (+8 vs 1266 baseline).
- [x] Existing TgProxyAutostartLoggingTests stay green after source-pin
      update.

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/TgProxyManager.cs` | +90 / −45 LOC. 1 callsite migrated (Start spawn + Stop kill). New `Runner` static seam + `IProcessRunner? runner` ctor parameter. `Process? _process` field replaced by `IProcessHandle? _handle`. New `StringBuilder _capturedStderr` + `object _stderrGate` to replace `StandardError.ReadToEnd()` on early-exit. Single `OnOutputData(object, DataReceivedEventArgs)` handler split into `OnOutputLineHandler(object?, string)` + `OnErrorLineHandler(object?, string)`. |
| `VPNRouter.Tests/TgProxyManagerProcessRunnerTests.cs` | +380 LOC (new file). 8 tests pinning argv shape, --verbose append, 2s probe budget, dual-stream stats fanout, Stop kill+wait, Stop idempotency, ctor injection, redaction defence-in-depth. |
| `VPNRouter.Tests/TgProxyAutostartLoggingTests.cs` | Updated `TgProxyManager_Start_LogsRedactedPsiAndPostSpawnProbe` source-pin: `"WaitForExit(2000)"` literal replaced with `"WaitForExitAsync"` + `"FromMilliseconds(2000)"` pair. Equivalent coverage in the post-migration shape. |
| `plans/phase3-iprocessrunner-tgproxymanager-2026-05-21.md` | This brief. |

### Test deltas

- Baseline (predecessor batch `cdf8157`): 1266 pass / 4 skip / 0 fail.
- After this batch: **1274 pass / 4 skip / 0 fail** (**+8 tests**).
- TgProxy-related suites: 31/31 green (was 23/23 pre-batch).

### Surprises encountered

1. **StandardError.ReadToEnd() is structurally unreachable through
   IProcessHandle**. The legacy v2.31.10 fix relied on
   `_process.StandardError.ReadToEnd()` post-exit to grab any stderr that
   ErrorDataReceived hadn't drained yet. IProcessHandle exposes stderr
   exclusively via the `ErrorLine` event stream — there's no raw
   StreamReader handle. Replacement: a `StringBuilder _capturedStderr`
   ring buffer (16 KiB cap) accumulated by `OnErrorLineHandler` as lines
   arrive, then drained by the early-exit log path. **Observable result
   is identical** — operator still sees an error tail in the log on
   Python embeddable startup failure. The 16 KiB cap is generous for
   normal Python tracebacks (~1-2 KiB) while bounding memory for
   long-lived runs.

2. **The single OnOutputData handler had to split**. The legacy code
   subscribed `OutputDataReceived += OnOutputData` AND
   `ErrorDataReceived += OnOutputData` to the SAME `DataReceivedEventArgs`-
   shaped handler. IProcessHandle uses `EventHandler<string>` where the
   payload is the line directly (no wrapper). Splitting into
   `OnOutputLineHandler` (stdout) + `OnErrorLineHandler` (stderr) was
   trivial, but the latter now does two things at once: feed the stats
   parser AND accumulate the stderr ring buffer. Net behaviour for the
   stats path is identical.

3. **TgProxyAutostartLoggingTests had a source-pin on the old impl**. The
   existing test `TgProxyManager_Start_LogsRedactedPsiAndPostSpawnProbe`
   was pinning the literal string `"WaitForExit(2000)"` — which is the
   exact pattern this migration replaces. Migrated the pin to the
   post-migration equivalent (`"WaitForExitAsync"` + `"FromMilliseconds(2000)"`)
   so the assertion's INTENT (catch any refactor that drops the 2s probe)
   is preserved.

4. **Tests need stub Python files on disk**. TgProxyManager.Start
   short-circuits with `FileNotFoundException` if `PythonExePath` or
   `ProxySourceDir` don't exist. The tests create stub files under the
   real `%ProgramData%\VPNRouter\tg-proxy\python\python.exe` path,
   tracking via `_seededFiles` whether WE created them so cleanup
   doesn't clobber a real tg-proxy install. Non-elevated CI without
   ProgramData write access trips the catch and tests gracefully no-op
   via `if (!_seededFiles) return;` guards.

5. **The 2s probe is sync-blocking from the test's perspective**. The
   probe runs synchronously inside `Start()` via
   `.GetAwaiter().GetResult()`, so each test that exercises the full
   spawn path has to run Start on a worker thread + `Wait()` for it to
   return. This is acceptable test-side complexity, but it's noteworthy
   that `Start` ITSELF is sync (preserves the legacy signature) — the
   async machinery is purely internal.

### Wire-shape invariants preserved

- argv shape: 8 bare tokens for the canonical case (`-m`, module,
  `--port`, port int, `--host`, host literal, `--secret`, secret) + 1
  optional `--verbose` tail-append. Pinned by 2 tests.
- 2s post-spawn probe budget: `TimeSpan.FromMilliseconds(2000)`. Pinned
  by 1 behavioural test + 2 source-pins (in TgProxyAutostartLoggingTests).
- Stop sync barrier: 3s. Pinned implicitly by the kill-test's pass-time
  (no behavioural assertion on the exact 3s, but the FakeProcessHandle's
  Kill-signals-exit pattern ensures the wait elapses quickly in tests).
- Stats fan-out: lines containing `"stats:"` from EITHER stdout OR stderr
  feed `StatsUpdated` + `LastStats`. Pinned by 1 test.
- Idempotent Stop: 2nd/3rd Stop calls no-op. Pinned by 1 test.
- Secret redaction defence-in-depth: `RedactSecretInArgs` still works
  on the legacy args string used by the log call. Pinned by 1 test.
- IProcessHandle automatic event wiring (`EnableRaisingEvents = true`
  no longer needed at SUT level): **confirmed working** — the Exited
  callback fires correctly, the OutputLine/ErrorLine events fire on
  the captured lines. ProcessHandle.Begin (ProcessRunner.cs:228-249)
  wires these unconditionally as part of `_runner.Start()`.

### OutputDataReceived → OutputLine mapping

**Clean.** No line-batching or encoding gotchas — the IProcessHandle
implementation in ProcessRunner.cs:228-238 forwards every non-null
`e.Data` payload from the underlying `Process.OutputDataReceived` /
`Process.ErrorDataReceived` events directly. The single edge case
(`e.Data == null` indicates end-of-stream) is filtered upstream of
the subscriber, so the legacy `string.IsNullOrEmpty(e.Data)` guard in
`OnOutputData` is structurally guaranteed by the seam. The new
`OnOutputLineHandler` keeps its own defensive guard for the same
shape (`string.IsNullOrEmpty(line)`) — costs nothing, catches a
hypothetical future fake handle that emits an empty line.

### Follow-ups spawned

- **SingBoxManager migration** — next long-lived spawn target. Has the
  heaviest invariant load: the `_process.EnableRaisingEvents = false`
  BEFORE Kill pattern (the SingBoxManager intentional-stop invariant
  documented in `VPNRouter.Core/CLAUDE.md`). IProcessHandle.Dispose
  already wires that — so the migration is a 1:1 replacement, but the
  test surface is much heavier (HealthMonitor restart loop, hot-reload
  via Clash API, etc.).
- **ZapretManager migration** — long-lived winws.exe spawn. Likely
  similar shape to TgProxyManager (long-lived + early-exit probe). Audit
  next batch.
- **VlessDeepVerifier migration** — running in parallel as a sibling
  batch per the task brief's "Coordination note". Different file, no
  conflict.

## Coordination note

A parallel agent migrated `VlessDeepVerifier` in the same Phase 3+ batch.
This brief touched **only** `VPNRouter.Core/Services/TgProxyManager.cs`,
`VPNRouter.Tests/TgProxyManagerProcessRunnerTests.cs`, and the source-pin
update in `VPNRouter.Tests/TgProxyAutostartLoggingTests.cs`. **No
modifications to `IProcessRunner.cs` or `ProcessRunner.cs`** — the
existing surface area handled this migration cleanly without any
extension. The IProcessHandle's `EnableRaisingEvents = true`-automatic
wiring at ctor time (ProcessRunner.cs:155) was sufficient; no new
fields or methods were needed.
