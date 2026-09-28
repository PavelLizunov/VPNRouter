#nullable enable

using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<RunMatcher> _runMatchers = new();
    private readonly List<StartMatcher> _startMatchers = new();
    private readonly List<ProcessRequest> _runCalls = new();
    private readonly List<ProcessRequest> _startCalls = new();
    private readonly object _gate = new();

    public IReadOnlyList<ProcessRequest> RunCalls
    {
        get { lock (_gate) return _runCalls.ToList(); }
    }

    public IReadOnlyList<ProcessRequest> StartCalls
    {
        get { lock (_gate) return _startCalls.ToList(); }
    }

    public FakeProcessRunner OnRun(Func<ProcessRequest, bool> predicate, ProcessResult result)
    {
        lock (_gate) _runMatchers.Add(new RunMatcher(predicate, _ => Task.FromResult(result)));
        return this;
    }

    public FakeProcessRunner OnRun(
        Func<ProcessRequest, bool> predicate,
        Func<ProcessRequest, Task<ProcessResult>> handler)
    {
        lock (_gate) _runMatchers.Add(new RunMatcher(predicate, handler));
        return this;
    }

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

    public int KillCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;
    public event EventHandler<int>? Exited;

    public Task<int> WaitForExitAsync(CancellationToken ct)
    {
        return _exit.Task.WaitAsync(ct);
    }

    public void Kill(bool entireProcessTree = true)
    {
        KillCallCount++;
        SignalExit(exitCode: -1);
    }

    public int SuppressExitedEventCallCount { get; private set; }

    public void SuppressExitedEvent()
    {
        SuppressExitedEventCallCount++;
        _exitedSuppressed = true;
    }

    private bool _exitedSuppressed;

    public bool SimulateExitedRaceLost { get; set; }

    public ProcessSnapshot? SnapshotStub { get; set; }

    public int SnapshotCallCount { get; private set; }

    public ProcessSnapshot? TryGetSnapshot()
    {
        SnapshotCallCount++;
        return SnapshotStub;
    }

    public void EmitOutput(string line) => OutputLine?.Invoke(this, line);

    public void EmitError(string line) => ErrorLine?.Invoke(this, line);

    public void SignalExit(int exitCode)
    {
        if (_exit.TrySetResult(exitCode) && (!_exitedSuppressed || SimulateExitedRaceLost))
            Exited?.Invoke(this, exitCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisposeCallCount++;
        if (!HasExited) SignalExit(exitCode: -1);
    }
}
