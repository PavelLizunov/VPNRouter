#nullable enable

namespace VPNRouter.Core.Services;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken ct = default);

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

    void SuppressExitedEvent();

    ProcessSnapshot? TryGetSnapshot();
}

public sealed record ProcessSnapshot(
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime,
    DateTime StartTime);
