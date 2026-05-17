#nullable enable
// ============================================================================
// IProcessRunner.cs — v3.0 Phase 2D abstraction (2026-05-17)
// ============================================================================
//
// Abstraction over System.Diagnostics.Process so that services calling out to
// netsh / sc / sing-box / winws / netstat / etc. can be unit-tested without
// invoking the real binary.
//
// Audit E (plans/test-coverage-audit-2026-05-17.md §"Missing abstractions")
// flagged four services as direct-Process consumers with no mocking seam:
// EtwProcessMonitor, ZapretActions, HostsManager (`runas` elevation),
// WindowsDnsHardening (netsh). This file is the first step of that fix.
//
// Two surface methods cover the two patterns we use in the codebase:
//
//   * RunAsync — fire-and-collect: spawn a process, wait, capture
//     stdout/stderr, return an exit code. For one-shot CLIs (sc query,
//     netsh show, sing-box check).
//
//   * Start — long-running: spawn a process, return a handle the caller
//     owns. For daemons (sing-box, winws, TgProxy). The handle exposes
//     the events Audit-D flagged: OutputLine, ErrorLine, Exited.
//
// Concrete impl lives in ProcessRunner.cs. Test fake lives in
// VPNRouter.Tests/Fakes/FakeProcessRunner.cs. This contract is intentionally
// cross-platform (no [SupportedOSPlatform] attribute) — Process itself is
// cross-platform and the seam needs to work on Linux/macOS once Phase 3
// finishes porting the desktop GUI.
//
// Brief: plans/phase2-2D-iprocessrunner-2026-05-17.md
// Follow-up: Phase 2G will route remaining Process call sites through this
// seam and write their tests.
// ============================================================================

namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over <see cref="System.Diagnostics.Process"/> for unit-testability.
/// Concrete impl <see cref="ProcessRunner"/> wraps <c>Process.Start</c> /
/// <c>WaitForExitAsync</c>; <c>FakeProcessRunner</c> (test helper in
/// <c>VPNRouter.Tests/Fakes/</c>) returns canned results without
/// invoking real processes.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run a process to completion. Captures stdout, stderr, exit code.
    /// Timeout via <see cref="ProcessRequest.Timeout"/> or
    /// <paramref name="ct"/> (process is killed on either).
    /// </summary>
    /// <param name="request">What to spawn — executable, args, env, capture flags.</param>
    /// <param name="ct">Caller cancellation. If signalled before the process
    /// exits, the process is killed and an <see cref="OperationCanceledException"/>
    /// is thrown. If the timeout fires first, the returned result has
    /// <see cref="ProcessResult.TimedOut"/> = true (no exception).</param>
    /// <returns>Result with exit code, captured streams, wall-clock duration.</returns>
    /// <exception cref="System.IO.FileNotFoundException">Executable not found.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation fired.</exception>
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Start a long-running process without waiting. Returns the spawned
    /// process for stream wiring + lifecycle control. For sing-box / Zapret /
    /// TgProxy / etc.
    /// </summary>
    /// <param name="request">What to spawn — see <see cref="ProcessRequest"/>.
    /// <see cref="ProcessRequest.Timeout"/> is ignored; the caller controls
    /// the lifecycle via the returned handle.</param>
    /// <returns>Live handle. Caller MUST dispose to release Process resources.</returns>
    /// <exception cref="System.IO.FileNotFoundException">Executable not found.</exception>
    IProcessHandle Start(ProcessRequest request);
}

/// <summary>
/// Input record describing what to spawn. Immutable so callers can build
/// it once and re-use across runs (e.g. retries).
/// </summary>
/// <param name="ExecutablePath">Path to the executable. Resolved by the OS
/// PATH if a bare name is given (e.g. <c>"sc"</c>, <c>"netsh"</c>); explicit
/// absolute paths are also fine.</param>
/// <param name="Arguments">Command-line arguments. Each element is one
/// argument (no shell-splitting). Use this instead of one long string so
/// we don't have to deal with shell quoting.</param>
/// <param name="WorkingDirectory">Optional working directory. <c>null</c>
/// inherits from the parent process.</param>
/// <param name="EnvironmentOverrides">Optional environment variable
/// overrides. Keys merged onto the parent process environment;
/// non-overridden vars are inherited.</param>
/// <param name="CaptureStdout">Whether to capture stdout (true) or let it
/// inherit (false). For RunAsync, true is the common case. For Start,
/// callers wiring up <see cref="IProcessHandle.OutputLine"/> events MUST
/// set this true.</param>
/// <param name="CaptureStderr">Same as <paramref name="CaptureStdout"/>
/// but for stderr / <see cref="IProcessHandle.ErrorLine"/>.</param>
/// <param name="StdinInput">Optional stdin payload. If non-null, stdin is
/// redirected, the string is written, and stdin is closed. RunAsync only;
/// Start ignores this.</param>
/// <param name="Timeout">Optional timeout. RunAsync only. When the timeout
/// fires, the process is killed and the result has
/// <see cref="ProcessResult.TimedOut"/> = true. Start ignores this.</param>
public sealed record ProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    bool CaptureStdout = true,
    bool CaptureStderr = true,
    string? StdinInput = null,
    TimeSpan? Timeout = null);

/// <summary>
/// Result of a <see cref="IProcessRunner.RunAsync"/> call.
/// </summary>
/// <param name="ExitCode">The process exit code. Native code on Windows,
/// the low 8 bits of the status on Unix.</param>
/// <param name="Stdout">Captured stdout. Empty string if
/// <see cref="ProcessRequest.CaptureStdout"/> was false.</param>
/// <param name="Stderr">Captured stderr. Empty string if
/// <see cref="ProcessRequest.CaptureStderr"/> was false.</param>
/// <param name="Duration">Wall-clock time from spawn to exit (or kill).</param>
/// <param name="TimedOut">True iff <see cref="ProcessRequest.Timeout"/>
/// fired before natural exit. The process was killed in this case.
/// Note: caller-cancellation via the <c>CancellationToken</c> throws
/// <see cref="OperationCanceledException"/> instead.</param>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>
/// Handle to a started long-running process. Caller owns disposal.
/// Disposing before <see cref="HasExited"/> kills the process tree.
/// </summary>
public interface IProcessHandle : IDisposable
{
    /// <summary>OS process id. Stable for the lifetime of the handle.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited (natural or killed).</summary>
    bool HasExited { get; }

    /// <summary>
    /// Wait for the process to exit. Returns the exit code.
    /// If <paramref name="ct"/> fires, the wait throws
    /// <see cref="OperationCanceledException"/> but the process is NOT
    /// killed — the caller must call <see cref="Kill"/> explicitly to
    /// stop it. (Symmetric with <see cref="System.Diagnostics.Process.WaitForExitAsync"/>.)
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>
    /// Kill the process. Idempotent — calling on an already-exited
    /// process is a no-op. Does NOT wait for exit; pair with
    /// <see cref="WaitForExitAsync"/> if you need synchronisation.
    /// </summary>
    /// <param name="entireProcessTree">If true, kills child processes too
    /// (best-effort; depends on OS support).</param>
    void Kill(bool entireProcessTree = true);

    /// <summary>Fires per captured stdout line. Only fires if the
    /// request had <see cref="ProcessRequest.CaptureStdout"/> = true.</summary>
    event EventHandler<string>? OutputLine;

    /// <summary>Fires per captured stderr line. Only fires if the
    /// request had <see cref="ProcessRequest.CaptureStderr"/> = true.</summary>
    event EventHandler<string>? ErrorLine;

    /// <summary>Fires once when the process exits, with the exit code.
    /// May fire on a background thread.</summary>
    event EventHandler<int>? Exited;
}
