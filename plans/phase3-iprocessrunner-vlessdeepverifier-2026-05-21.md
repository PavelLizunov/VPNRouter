# Phase 3+ — IProcessRunner adoption: VlessDeepVerifier (long-lived spawn)

**Owner**: Claude session (Phase 3+ batch, first long-lived spawn target)
**Branch**: main (direct commit)
**Predecessor briefs**:
- `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md` (FirewallManager + TunAdapter, `1242b9e`)
- `plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md` (HostsManager + ZapretActions, `cdf8157`)
**Effort**: ~1 hour
**Risk**: LOW (short lifetime ≤12 s, no Exited event subscribed, fully CTS-controlled)
**Blast radius**: 1 service file · 1 spawn callsite · 0 behaviour change
**Rollback**: `git revert <commit>` — the seam is additive; reverting
restores the direct `Process.Start` + Kill-in-finally pattern verbatim.

## Why

Predecessor batches migrated **leaf** netsh batch callers (one-shot fire-and-forget). The
Phase 3+ long-lived spawn audit (just shipped 2026-05-21) confirmed
`IProcessRunner.Start` already returns `IProcessHandle` with the full surface
needed: `Pid`, `HasExited`, `WaitForExitAsync(CancellationToken)`,
`Kill(bool entireProcessTree)`, events `OutputLine` / `ErrorLine` / `Exited`.
**No API extension needed.**

Per the audit, `VlessDeepVerifier` is the simplest long-lived migration
target:

- short lifetime (≤12 s overall timeout, ≤1.5 s warmup + ≤8 s HTTP probe);
- no `Exited` event wired (lifetime is fully CTS-controlled);
- single Kill-in-finally path (idempotent via `HasExited` check);
- already test-friendly via the existing test-only ctor that injects
  `_singBoxPath`.

Migrating this proves the predecessor `IProcessHandle` surface works for
long-lived sing-box spawns and unblocks subsequent batches for the heavier
targets (`SingBoxManager`, `ZapretManager`, `TgProxy*`, etc.).

## What

### A. VlessDeepVerifier.cs — 1 spawn callsite migrated

- New `private readonly IProcessRunner _runner;` field.
- New `internal static IProcessRunner Runner { get; set; } = new ProcessRunner();`
  seam (mirrors `HostsManager.Runner` / `FirewallManager.Runner`).
- Both public ctors gain an optional `IProcessRunner? runner = null` 4th
  positional parameter (backward compatible — existing 1-arg + 2-arg
  callsites continue to work).
- Inside `VerifyAsync` (the hot path), the legacy block
  `new Process { StartInfo = ... }` + `process.Start()` is replaced with:
  ```csharp
  var request = new ProcessRequest(
      ExecutablePath: _singBoxPath,
      Arguments: new[] { "run", "-c", tmpConfigPath },
      CaptureStdout: true,
      CaptureStderr: true);
  handle = _runner.Start(request);
  handle.ErrorLine += (_, line) => stderrBuffer.Append(line).Append('\n');
  ```
- The `EnableRaisingEvents = false` explicit set (legacy line 180) is
  **dropped** — `ProcessHandle.Dispose()` (ProcessRunner.cs lines
  280-293) sets the flag immediately before `Kill()` for every disposed
  handle, so the load-bearing intent (suppress spurious `Exited`) is
  preserved transitively. Note: this service didn't even subscribe to
  `Exited`, so dropping the explicit set is zero-impact.
- The Kill-in-finally path is preserved: `if (handle != null &&
  !handle.HasExited) handle.Kill(true);`. We dispose the handle in
  finally instead of the Process directly (the `ProcessHandle.Dispose`
  re-asserts kill, so the surface stays idempotent).

### B. Wire-shape invariants preserved

- argv: `_singBoxPath` + `["run", "-c", <temp-config-path>]` (was the
  single-string `run -c "..."`, semantically equivalent under
  `ArgumentList`-based quoting).
- `CaptureStdout: true` (legacy `RedirectStandardOutput = true`).
- `CaptureStderr: true` (legacy `RedirectStandardError = true`).
- Stderr buffer accumulation: subscribe to `handle.ErrorLine` event
  (replaces `process.ErrorDataReceived += ...`).
- Idempotent Kill via `handle.Kill(true)` (entireProcessTree:true
  matches `process.Kill(entireProcessTree: true)`).
- Lifetime cancellation: `OverallTimeout` CTS (12 s) drives both the
  port-wait and HTTP probe paths; the inner `WaitForPortBoundAsync`
  remains unchanged (it polls TcpClient.Connect on loopback —
  not a process call).
- Temp config file cleanup: unchanged.

### C. New unit tests

`VPNRouter.Tests/VlessDeepVerifierProcessRunnerTests.cs` — 5 tests:

1. `BuildSpawnRequest_ArgvPin` — `[Fact]`. Verifies the ProcessRequest
   produced by `VerifyAsync` carries `["run", "-c", <temp-config>]` and
   that `CaptureStdout`/`CaptureStderr` are both true. The temp path is
   asserted by prefix (`sb-dv-`) since each call generates a fresh GUID.
2. `StderrAccumulation_FromErrorLineEvents` — `[Fact]`. Pin that
   stderr-buffer accumulation flows via the `handle.ErrorLine` event
   (not stdout, not stdin) — pump a few `EmitError` lines, fail the
   probe (port never binds), assert the snippet shows up in the
   resulting `DeepVerifyResult.Error`.
3. `Kill_Called_OnPortBindTimeout` — `[Fact]`. After the FakeProcessHandle
   signals neither exit nor a bound port within OverallTimeout, the
   handle MUST be killed via `Kill`. Asserted by FakeProcessHandle
   recording the Kill-call (we add that — minor extension to the existing
   fake).
4. `Kill_Called_OnCancellation` — `[Fact]`. Caller-side cancellation
   propagates to handle.Kill via the finally block. Pre-cancel the CTS,
   confirm `DeepVerifyResult.Error == "timeout"` (the catch
   `OperationCanceledException` branch returns this) and that Kill was
   invoked.
5. `Constructor_AcceptsCustomRunner_WiresUpInjection` — `[Fact]`. Smoke
   that the new ctor doesn't ignore the `runner:` argument; null falls
   back to the static `Runner` default.

The FakeProcessHandle gains a `KillCallCount` counter (test-side
observable) for tests 3 and 4. Behaviourally inert change — production
code uses `Kill()` the same way.

## How

### Step 1: Seam plumbing

```csharp
private readonly IProcessRunner _runner;

internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

public VlessDeepVerifier(ILogger logger, IProcessRunner? runner = null)
{
    _logger = logger;
    _singBoxPath = AppPaths.SingBoxExePath;
    _runner = runner ?? Runner;
}

internal VlessDeepVerifier(ILogger logger, string singBoxPath, IProcessRunner? runner = null)
{
    _logger = logger;
    _singBoxPath = singBoxPath;
    _runner = runner ?? Runner;
}
```

### Step 2: Spawn callsite

Replace lines 169-194 (the `new Process { StartInfo = ... }; process.Start()
+ subscriptions + BeginOutputReadLine/BeginErrorReadLine` block) with:

```csharp
IProcessHandle? handle = null;
// ... in the try block:
var request = new ProcessRequest(
    ExecutablePath: _singBoxPath,
    Arguments: new[] { "run", "-c", tmpConfigPath },
    CaptureStdout: true,
    CaptureStderr: true);
handle = _runner.Start(request);
handle.ErrorLine += (_, line) =>
{
    if (line != null) stderrBuffer.Append(line).Append('\n');
};
_logger.Debug("[VlessDeepVerifier] {Name}: sing-box spawned pid={Pid} socks={SocksPort}", label, handle.Pid, socksPort);
```

Finally block:

```csharp
finally
{
    try
    {
        if (handle != null && !handle.HasExited)
        {
            handle.Kill(entireProcessTree: true);
        }
        handle?.Dispose();
    }
    catch { }

    if (tmpConfigPath != null)
    {
        try { File.Delete(tmpConfigPath); } catch { }
    }
}
```

The explicit `process.WaitForExit(2000)` after Kill is dropped — the
`ProcessHandle.Dispose` path doesn't synchronously wait, but the
verifier doesn't observe the exit code; it only needs the kernel
process to be terminated. The OS-level Kill is fire-and-forget on
Windows; the 2 s wait was defensive (covered the WaitForExit-after-Kill
on dispose race that .NET 6 had, and which we don't observe with
.NET 8's WaitForExitAsync path).

### Step 3: Wire-shape tests

New file `VPNRouter.Tests/VlessDeepVerifierProcessRunnerTests.cs`. Tests
inject `FakeProcessRunner` via the ctor (`runner:`). Per-test setup
generates a fresh fake; the matcher returns a FakeProcessHandle whose
lifecycle the test controls. `FakeProcessHandle.KillCallCount` records
how many times `Kill` was invoked.

## Verification gate

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors
- [x] `VlessDeepVerifierTests` + `VlessDeepVerifierBehaviourTests` all
  green (17 existing tests — MUST stay green)
- [x] New `VlessDeepVerifierProcessRunnerTests` (5/5) green
- [x] Full suite (excl. headless / page-screenshot / visual-diff)
  passes with same baseline + new tests
- [x] Wire shape preserved per §B

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/VlessDeepVerifier.cs` | +21 / -19 LOC. 1 spawn callsite migrated to `IProcessRunner.Start`. New `Runner` static seam + `IProcessRunner? runner` ctor parameter on both public and internal test ctors. Dropped explicit `EnableRaisingEvents = false` — `ProcessHandle.Dispose` carries that intent transitively. |
| `VPNRouter.Tests/Fakes/FakeProcessRunner.cs` | +3 LOC. `FakeProcessHandle.KillCallCount` counter for assertions. |
| `VPNRouter.Tests/VlessDeepVerifierProcessRunnerTests.cs` | +194 LOC (new file). 5 wire-shape tests pinning ProcessRequest argv, ErrorLine subscription path, Kill-on-timeout, Kill-on-cancellation, ctor injection. |
| `plans/phase3-iprocessrunner-vlessdeepverifier-2026-05-21.md` | This brief. |

### Test deltas

- Baseline (per task brief, predecessor `cdf8157`): 1266 pass / 4 skip / 0 fail.
- Observed baseline at start of this batch: 1274 pass / 4 skip / 0 fail
  (delta vs brief baseline = +8 from intermediate non-IProcessRunner test additions).
- After this batch: **1279 pass / 4 skip / 0 fail** (+5 tests).
- All VlessDeepVerifier-related suites: 31/31 green (was 26/26 pre-batch,
  counting `[Theory]` InlineData rows individually).

### Surprises encountered

1. **Implicit-EnableRaisingEvents-via-Dispose worked as predicted**.
   The audit's hypothesis — that dropping the explicit
   `EnableRaisingEvents = false` is safe because (a) this service
   doesn't subscribe to `Exited`, and (b) `ProcessHandle.Dispose`
   sets the flag before Kill — held in practice. All 17 existing
   VlessDeepVerifier tests passed without modification.

2. **`process.WaitForExit(2000)` after Kill dropped**. Legacy code did
   a synchronous 2 s wait after kill to settle the OS handle. The
   `IProcessHandle.Kill` is fire-and-forget; the subsequent
   `handle.Dispose()` calls `Process.Dispose` internally which handles
   resource cleanup. No regression observed — the verifier doesn't
   read post-exit state so the wait was purely defensive.

3. **`FakeProcessHandle` needed a Kill counter**. The pre-existing fake
   only tracked emit/exit signals, not Kill invocations. Added a
   minimal `int KillCallCount` field. Behaviourally inert — production
   ProcessHandle.Kill is the same idempotent no-op on already-exited
   process.

4. **Stderr line termination**. Legacy code appended `'\n'` after each
   stderr line: `stderrBuffer.Append(e.Data).Append('\n');`. The new
   ErrorLine subscription mirrors this exactly:
   `if (line != null) stderrBuffer.Append(line).Append('\n');`. Pin
   would otherwise drift if a future change forgot the explicit newline.

### Wire-shape invariants preserved

- argv: `["run", "-c", <temp-config>]` (3 separate tokens via
  `ArgumentList`, semantically equivalent to legacy single-string).
- CaptureStdout/CaptureStderr both true (line 177-178 in legacy).
- Stderr buffer accumulation via event subscription (was
  `process.ErrorDataReceived +=`, now `handle.ErrorLine +=`).
- Kill-in-finally idempotent (HasExited guard preserved).
- OverallTimeout CTS (12 s) drives the probe lifetime exactly as before.

### Follow-ups spawned

- **Next long-lived spawn targets** (in order of risk, lightest first):
  - `FreeConfigDeepVerifier.cs` — same probe shape, can mirror this
    migration line-for-line.
  - `ZapretManager.cs` — winws.exe lifecycle.
  - `SingBoxManager.cs` — heaviest target, full Hot-reload + Restart
    paths. Audit'd separately.
  - `TgProxyUpdater.cs` — extracted release lifecycle.

