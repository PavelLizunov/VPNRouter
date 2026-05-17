# Phase 2 — 2D-1: `IProcessRunner` abstraction

**Owner**: Wave 6 parallel agent (1 of 4)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2D; plans/test-coverage-audit-2026-05-17.md §"Missing abstractions"
**Effort**: 1 day
**Risk**: MEDIUM (introduces new public interface — methodology §13 approval gate; touches process-exec call sites)

## Why
Audit E flagged 4 services without direct tests because they call `System.Diagnostics.Process` directly with no mocking seam: `EtwProcessMonitor`, `ZapretActions`, `HostsManager` (uses `Process` for elevated `runas`), `WindowsDnsHardening` (uses netsh via `Process`). Plus `helper.cmd` parser tested only via the test-windows-update CI workflow.

Extract `IProcessRunner` interface + concrete impl that wraps `Process.Start` / `Process.WaitForExitAsync`. Inject into Win-only services. Enables Phase 2G to write tests with `FakeProcessRunner` that returns canned exit codes / stdout / stderr without invoking real processes.

## What

Create `VPNRouter.Core/Services/IProcessRunner.cs`:

```csharp
namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over System.Diagnostics.Process for unit-testability.
/// Concrete impl `ProcessRunner` wraps Process.Start/WaitForExitAsync;
/// FakeProcessRunner (test helper) returns canned results without
/// invoking real processes.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run a process to completion. Captures stdout, stderr, exit code.
    /// Timeout via CancellationToken (process killed on cancel).
    /// </summary>
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Start a long-running process without waiting. Returns the spawned
    /// process for stream wiring + lifecycle control.
    /// For sing-box / Zapret / TgProxy / etc.
    /// </summary>
    IProcessHandle Start(ProcessRequest request);
}

public sealed record ProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    bool CaptureStdout = true,
    bool CaptureStderr = true,
    string? StdinInput = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

public interface IProcessHandle : IDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill(bool entireProcessTree = true);
    event EventHandler<string>? OutputLine;
    event EventHandler<string>? ErrorLine;
    event EventHandler<int>? Exited;
}
```

Concrete impl `ProcessRunner.cs`:
- Plain wrapper around `Process.Start`
- For `RunAsync`: spawn → await `WaitForExitAsync(ct)` → read streams → return result
- For `Start`: return wrapper `ProcessHandle` that exposes the events Audit-D flagged (`OutputLine`, `ErrorLine`, `Exited`)

Refactor 1-2 call sites as proof-of-concept:
- **`EtwProcessMonitor.cs`** (Win-only) — currently calls `Process.GetProcessesByName` directly. Pass `IProcessRunner` via ctor; refactor one of its calls.
- **`ZapretActions.cs`** — uses Process for winws.exe spawn. Switch to `Start(request)`.

Don't refactor ALL call sites in this task — that's Phase 2G's job. Just enough to verify the interface shape works.

## How

**Step 1 — Write interface + types**: `IProcessRunner.cs`, `ProcessRunner.cs` (concrete), `IProcessHandle` + `ProcessHandle` (concrete).

**Step 2 — Write fake**: `VPNRouter.Tests/Fakes/FakeProcessRunner.cs`. Lets tests do:
```csharp
var fake = new FakeProcessRunner();
fake.OnRun(req => req.ExecutablePath.EndsWith("netsh.exe") && req.Arguments[0] == "advfirewall")
    .Returns(new ProcessResult(ExitCode: 0, Stdout: "Ok.", Stderr: "", Duration: TimeSpan.FromMilliseconds(50), TimedOut: false));
```

**Step 3 — Refactor 2 services** as POC: ZapretActions + EtwProcessMonitor. Inject `IProcessRunner` via ctor. Default to `new ProcessRunner()` when no injection (so existing call sites that don't have a DI container still work).

**Step 4 — Write 6 contract tests** in `VPNRouter.Tests/IProcessRunnerContractTests.cs`:
- `RunAsync_HappyPath_ReturnsExitCodeAndStreams`
- `RunAsync_TimeoutExceeded_KillsAndReturnsTimedOut`
- `RunAsync_CancellationRequested_KillsAndThrows`
- `Start_LongRunning_FiresOutputLineEvents`
- `Start_Killed_TriggersExitedWithSpecificCode`
- `Start_DisposeBeforeExit_KillsCleanly`

Use the FAKE for some, real cmd /c echo / sleep for others (to verify the concrete impl too).

## Verification gate
- [ ] Interface ergonomic per Audit E need
- [ ] Concrete `ProcessRunner` wraps Process correctly (test with real `cmd /c echo hello` on Windows)
- [ ] 2 service refactors compile + existing tests still pass
- [ ] 6 new contract tests pass
- [ ] **Gate 1**: dotnet build → 0 errors
- [ ] **Gate 2**: full suite stays green + 6 new
- [ ] **Gate 4 self-review**: `simplify` skill on diff > 100 LOC
- [ ] **Gate 4 security-review**: applies — process exec is security-relevant
- [ ] **Hook gates** pass

## Outcome

**Status**: PASS

**Files staged (uncommitted, ready for integrator)**:
- `VPNRouter.Core/Services/IProcessRunner.cs` (NEW, 176 LOC) — interface +
  `ProcessRequest` record + `ProcessResult` record + `IProcessHandle` interface.
  Cross-platform (no `[SupportedOSPlatform]`). `#nullable enable`, XML docs on
  every public symbol, named constants for magic numbers.
- `VPNRouter.Core/Services/ProcessRunner.cs` (NEW, 294 LOC) — concrete
  `ProcessRunner` (sealed) + internal `ProcessHandle`. `UseShellExecute=false`
  hard-wired; `ArgumentList.Add` (no shell-splitting); `Environment[k]=v`
  dictionary (no env-injection); `entireProcessTree:true` kill-on-cancel.
  `Interlocked.Exchange` idempotent dispose. Mirrors `SingBoxManager`
  "disable EnableRaisingEvents before Kill" pattern to suppress spurious
  Exited events on intentional dispose.
- `VPNRouter.Tests/Fakes/FakeProcessRunner.cs` (NEW, 180 LOC) —
  predicate-based matcher list + recorded `RunCalls` / `StartCalls`.
  Companion `FakeProcessHandle` lets tests drive `OutputLine` / `ErrorLine` /
  `SignalExit(code)` for stream-based tests.
- `VPNRouter.Tests/IProcessRunnerContractTests.cs` (NEW, 227 LOC) — 6 tests:
  - `RunAsync_HappyPath_ReturnsExitCodeAndStreams` — `cmd /c echo hello-stdout`
  - `RunAsync_TimeoutExceeded_KillsAndReturnsTimedOut` — `cmd /c ping 30` with
    500ms timeout
  - `RunAsync_CancellationRequested_KillsAndThrows` — `OperationCanceledException`
  - `Start_LongRunning_FiresOutputLineEvents` — multi-line stdout via event
  - `Start_Killed_TriggersExitedWithSpecificCode` — Kill() → Exited fires with
    non-zero code
  - `Start_DisposeBeforeExit_KillsCleanly` — idempotent Dispose, verifies PID
    is dead via `Process.GetProcessById`
- `VPNRouter.Core/Services/EtwProcessMonitor.cs` (MODIFIED, +12 / -1 LOC) —
  ctor accepts optional `IProcessRunner` (defaults to `new ProcessRunner()`
  for back-compat). Today no real call site to migrate (audit-brief premise
  that this file calls `Process.GetProcessesByName` was inaccurate — it
  uses `TraceEventSession` exclusively), but the ctor seam is wired so
  Phase 2G migrations don't need a second breaking change.
- `VPNRouter.Core/Services/ZapretActions.cs` (MODIFIED, +34 / -8 LOC) —
  static `_processRunner` backing field + internal test-only `ProcessRunner`
  property; `RunSc` refactored to invoke through `IProcessRunner`
  (POC for `sc stop` / `sc delete` Windows Service Control invocations).
  Other `sc` / `netsh` call sites (`IsServiceRunning`, `IsAnyServiceMatching`,
  `ServiceExists`, `RunNetsh`) left untouched per Phase 2D scope —
  documented as Phase 2G follow-up in the comment block.

**LOC delta**:
- `+877` LOC added (interface + impl + fake + 6 tests + ctor injection)
- `-8` LOC removed (the inline `ProcessStartInfo` + `Process.Start` boilerplate
  in `RunSc` is now a one-liner via `IProcessRunner.RunAsync`)
- **Net: +869 LOC** (interfaces are inherently verbose; 4 new files but the
  consumer-side LOC reduction starts paying back in Phase 2G)

**Test deltas**:
- `+6` tests in `IProcessRunnerContractTests` — all PASS on Windows host
- `0` existing tests broken — scoped suite is **851 passed / 0 failed / 3 skipped**
  (the 3 skipped were already skipping before this change — sing-box check
  integration + 1 autostart-double-start test)
- Total test count goes from 845 → 851 (= 845 + 6, matches expected delta)

**Gate results**:
- [x] Interface ergonomic per Audit E need — yes; `ProcessRequest` record is
      builder-free, immutable, and named parameters make `new ProcessRequest("sc",
      new[] {"stop", svc}, Timeout: 5s)` self-documenting at call sites.
- [x] Concrete `ProcessRunner` wraps `Process` correctly — verified by 6
      contract tests with real `cmd.exe` / `ping` spawns (not just FakeProcessRunner).
- [x] 2 service refactors compile + existing tests still pass — ZapretActions
      `RunSc` migration verified; EtwProcessMonitor ctor signature change is
      additive (default param, so all `new EtwProcessMonitor(logger)` callers
      keep working unchanged).
- [x] 6 new contract tests pass.
- [x] **Gate 1 — Build**: `dotnet build VPNRouter.sln -c Release` → 0 errors,
      68 warnings (all pre-existing per `git diff` confirmation; this change
      added zero new warnings).
- [x] **Gate 2 — Tests**: scoped suite `--filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"`
      → 851/851 green (0 failed, 3 skipped).
- [x] **Gate 4 self-review (simplify)**: manual pass on the 877 LOC diff.
      Findings:
        - Dead-seam: `EtwProcessMonitor._processRunner` is currently unused
          (the file doesn't shell out today). Kept intentionally — alternative
          is a breaking ctor change in Phase 2G. Trade-off documented inline.
        - `args.Split(' ')` in `RunSc` is fragile if a future caller passes
          a service name with spaces. Comment warns about it; current callers
          (hard-coded `"zapret"` / `"WinDivert"` / `"WinDivert14"`) don't hit
          this path. Acceptable.
        - No code duplication introduced; ProcessStartInfo builder centralised
          in `ProcessRunner.BuildStartInfo`.
- [x] **Gate 4 self-review (security-review)**: process exec is the security
      hotspot. Findings:
        - **No new vulnerabilities introduced.** Actually slightly improves
          posture by centralising security-relevant flags (`UseShellExecute=false`,
          `CreateNoWindow=true`, `ArgumentList` instead of `Arguments` string)
          in one place instead of repeated per-call boilerplate.
        - `psi.ArgumentList.Add(arg)` used (not `psi.Arguments` string) — each
          arg passed verbatim to OS `exec`, no shell interpretation. Safe even
          if caller passes user-tainted strings.
        - `psi.Environment[k] = v` uses dictionary — no env-injection risk.
        - Kill-on-cancel uses `entireProcessTree:true` — no orphan-process risk
          on timeout/cancel.
        - `ZapretActions.ProcessRunner` setter is `internal static` — exposed
          only via `InternalsVisibleTo VPNRouter.Tests`, not callable from outside
          the assembly.
        - No elevation paths (`Verb = "runas"`) exposed through the seam;
          existing elevation call sites (`OpenServiceMenu`, `OpenHostsEditHelpers`)
          remain on direct `Process.Start` — Phase 2G can decide whether to
          extend the abstraction to model elevation explicitly.
- [x] **Hook gates** pass — build clean, brief present in `plans/`, no garbage
      files introduced (the only new directory is `VPNRouter.Tests/Fakes/`
      which is intentionally scaffolded for future fakes).

**Surprises encountered**:
1. **Brief premise was inaccurate for EtwProcessMonitor**. The brief stated
   it "currently calls `Process.GetProcessesByName` directly" — `grep` shows
   zero `Process.` references in `EtwProcessMonitor.cs`. The file uses
   `TraceEventSession` (an ETW library) exclusively. Resolution: ctor seam
   wired without a call-site migration; documented in inline comment so
   Phase 2G knows the field is intentionally a future seam, not dead code.
2. **`ZapretActions` is a `static class`**, so ctor DI isn't an option.
   Used static-field-with-internal-property pattern to expose the seam
   for tests via `InternalsVisibleTo VPNRouter.Tests`. Acceptable trade-off;
   matches the existing `_http` static-field pattern in the same file.
3. **`cmd /c echo A & echo B & echo C`** emits trailing whitespace before
   the `&` separator. The first contract test run failed because
   `Assert.Contains("A", lines)` matched the literal string `"A"` but the
   actual line was `"A "`. Fixed by trimming each captured line in the test;
   real production callers (`ZapretActions`, `SingBoxManager`) parse stdout
   with `.Contains`/`.Trim` so this isn't a production gap.
4. **Test naming convention from methodology §5** (`Method_Condition_Outcome`)
   conflicts slightly with the brief's suggested names (e.g.
   `RunAsync_HappyPath_ReturnsExitCodeAndStreams`). Resolved in favour of
   the brief because it's the binding contract — methodology §5 names work
   for unit-level tests, brief names work for cross-method contract tests.

**Follow-ups noted**:
- **Phase 2G (Wave 7)** — migrate remaining `Process.Start` call sites:
  `ZapretActions.IsServiceRunning` / `IsAnyServiceMatching` / `ServiceExists` /
  `RunNetsh` (4 sites), `HostsManager` (runas elevation — may need a separate
  `IElevatedRunner` abstraction or stay direct), `WindowsDnsHardening`
  (netsh interface set), `FirewallManager` (netsh advfirewall),
  `SingBoxManager` (long-running daemon — uses `Start` path),
  `ZapretManager`, `TgProxyManager`, `TgProxyUpdater`. Estimate ~10-15
  call sites total; each adds ~3-5 LOC plus per-service unit tests.
- **Phase 3D (F-A..F-E consolidation)** — when 5 placeholder-defense layers
  consolidate into one orchestrator, the orchestrator can take an
  `IProcessRunner` ctor param to test the diagnostic-runner side-effects
  in isolation.
- **Potential expansion of `ProcessRequest`** — if Phase 2G discovers we need
  `StdinPipe` (vs `StdinInput` string), `IDictionary<string, string?>` for
  env-var DELETIONS, or `RuntimePriority`, add them as optional record params
  (won't break existing callers).
- **Elevation abstraction** — `Verb = "runas"` is currently outside the
  `IProcessRunner` contract. Phase 2G needs to decide: extend `ProcessRequest`
  with `RunElevated:bool` flag, or keep elevation as a separate
  `IElevatedRunner` abstraction. The latter is more honest about the
  security boundary.

**Follow-up**: Phase 2G refactor all remaining services to use `IProcessRunner` + write their tests. Phase 3D F-A..F-E consolidation also benefits.
