#nullable enable
// ============================================================================
// FakeProcessRunner.cs — IProcessRunner test double
// ============================================================================
//
// Pluggable fake for IProcessRunner so unit tests can dictate exit codes,
// stdout, stderr, and timing without invoking real binaries. The shape is
// intentionally light: predicate + canned result, list of matchers, fallback.
//
// Example:
//
//   var fake = new FakeProcessRunner();
//   fake.OnRun(
//       r => r.ExecutablePath == "netsh" && r.Arguments[0] == "advfirewall",
//       new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(50), false));
//
//   var result = await fake.RunAsync(new ProcessRequest("netsh",
//       new[] { "advfirewall", "show", "rule" }));
//
// Test classes own their FakeProcessRunner instance and configure it
// per test. No global singleton, no static state.
// ============================================================================

using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProcessRunner"/> for tests. Match incoming requests
/// against caller-supplied predicates; first match wins. No match → throws
/// to surface accidentally-unmocked call sites in tests.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<RunMatcher> _runMatchers = new();
    private readonly List<StartMatcher> _startMatchers = new();
    private readonly List<ProcessRequest> _runCalls = new();
    private readonly List<ProcessRequest> _startCalls = new();
    private readonly object _gate = new();

    /// <summary>All ProcessRequests passed to <see cref="RunAsync"/>,
    /// in call order. Tests use this to assert behaviour.</summary>
    public IReadOnlyList<ProcessRequest> RunCalls
    {
        get { lock (_gate) return _runCalls.ToList(); }
    }

    /// <summary>All ProcessRequests passed to <see cref="Start"/>,
    /// in call order.</summary>
    public IReadOnlyList<ProcessRequest> StartCalls
    {
        get { lock (_gate) return _startCalls.ToList(); }
    }

    /// <summary>Register a match: when <paramref name="predicate"/> returns
    /// true for an incoming request, return <paramref name="result"/>.</summary>
    public FakeProcessRunner OnRun(Func<ProcessRequest, bool> predicate, ProcessResult result)
    {
        lock (_gate) _runMatchers.Add(new RunMatcher(predicate, _ => Task.FromResult(result)));
        return this;
    }

    /// <summary>Async-aware overload — useful for tests that want to assert
    /// timing (e.g. "the runner respects the timeout").</summary>
    public FakeProcessRunner OnRun(
        Func<ProcessRequest, bool> predicate,
        Func<ProcessRequest, Task<ProcessResult>> handler)
    {
        lock (_gate) _runMatchers.Add(new RunMatcher(predicate, handler));
        return this;
    }

    /// <summary>Register a Start match. Returns a <see cref="FakeProcessHandle"/>
    /// the test controls.</summary>
    public FakeProcessRunner OnStart(
        Func<ProcessRequest, bool> predicate,
        Func<ProcessRequest, FakeProcessHandle> factory)
    {
        lock (_gate) _startMatchers.Add(new StartMatcher(predicate, factory));
        return this;
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        lock (_gate) _runCalls.Add(request);

        Func<ProcessRequest, Task<ProcessResult>>? handler = null;
        lock (_gate)
        {
            foreach (var m in _runMatchers)
                if (m.Predicate(request)) { handler = m.Handler; break; }
        }
        if (handler == null)
            throw new InvalidOperationException(
                $"FakeProcessRunner.RunAsync: no matcher for '{request.ExecutablePath}'. " +
                "Register one via OnRun(...) in the test setup.");

        ct.ThrowIfCancellationRequested();
        return await handler(request).ConfigureAwait(false);
    }

    public IProcessHandle Start(ProcessRequest request)
    {
        lock (_gate) _startCalls.Add(request);

        Func<ProcessRequest, FakeProcessHandle>? factory = null;
        lock (_gate)
        {
            foreach (var m in _startMatchers)
                if (m.Predicate(request)) { factory = m.Factory; break; }
        }
        if (factory == null)
            throw new InvalidOperationException(
                $"FakeProcessRunner.Start: no matcher for '{request.ExecutablePath}'. " +
                "Register one via OnStart(...) in the test setup.");

        return factory(request);
    }

    private sealed record RunMatcher(
        Func<ProcessRequest, bool> Predicate,
        Func<ProcessRequest, Task<ProcessResult>> Handler);

    private sealed record StartMatcher(
        Func<ProcessRequest, bool> Predicate,
        Func<ProcessRequest, FakeProcessHandle> Factory);
}

/// <summary>
/// Controllable <see cref="IProcessHandle"/> for tests. Tests drive the
/// fake by calling <see cref="EmitOutput"/>, <see cref="EmitError"/>, and
/// <see cref="SignalExit"/>.
/// </summary>
public sealed class FakeProcessHandle : IProcessHandle
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public FakeProcessHandle(int pid = 12345)
    {
        Pid = pid;
    }

    public int Pid { get; }

    public bool HasExited => _exit.Task.IsCompleted;

    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;
    public event EventHandler<int>? Exited;

    public Task<int> WaitForExitAsync(CancellationToken ct)
    {
        return _exit.Task.WaitAsync(ct);
    }

    public void Kill(bool entireProcessTree = true)
    {
        SignalExit(exitCode: -1);
    }

    /// <summary>Test-side: emit a stdout line.</summary>
    public void EmitOutput(string line) => OutputLine?.Invoke(this, line);

    /// <summary>Test-side: emit a stderr line.</summary>
    public void EmitError(string line) => ErrorLine?.Invoke(this, line);

    /// <summary>Test-side: signal that the fake process has exited.</summary>
    public void SignalExit(int exitCode)
    {
        if (_exit.TrySetResult(exitCode)) Exited?.Invoke(this, exitCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Mimic ProcessHandle: dispose implies kill if still running.
        if (!HasExited) SignalExit(exitCode: -1);
    }
}
