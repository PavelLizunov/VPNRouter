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
*(filled by agent)*

**Follow-up**: Phase 2G refactor all remaining services to use `IProcessRunner` + write their tests. Phase 3D F-A..F-E consolidation also benefits.
