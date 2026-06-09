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

    /// <summary>Test-side: how many times <see cref="Kill"/> has been
    /// invoked on this handle. Used by wire-shape tests asserting the
    /// kill-in-finally path actually fires (e.g. on probe timeout or
    /// caller cancellation). Production ProcessHandle.Kill is idempotent
    /// for already-exited processes, so multiple-call observability here
    /// is behaviourally inert.</summary>
    public int KillCallCount { get; private set; }

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

    /// <summary>v2.36.0-r4 (brat 2026-05-24 — intentional-stop regression
    /// fix): track Suppress calls so tests can pin the call ordering
    /// (must precede Kill on intentional-Stop paths).</summary>
    public int SuppressExitedEventCallCount { get; private set; }

    public void SuppressExitedEvent()
    {
        SuppressExitedEventCallCount++;
        // Test side: also flip a flag so subsequent SignalExit decides
        // whether to fire Exited or not. The real ProcessHandle disables
        // the OS Exited callback; we mirror by gating SignalExit's
        // Exited?.Invoke on this flag.
        _exitedSuppressed = true;
    }

    private bool _exitedSuppressed;

    /// <summary>
    /// Test-side: when true, a <see cref="Kill"/> / <see cref="SignalExit"/>
    /// AFTER <see cref="SuppressExitedEvent"/> STILL raises the
    /// <see cref="Exited"/> event — modelling the late OS callback that was
    /// already queued before <c>EnableRaisingEvents=false</c> took effect (the
    /// ~14-33ms race brat / ekko / Pavel field logs caught, which the production
    /// <c>SuppressExitedEvent</c> loses in ~15-30% of intentional stops).
    /// Default <c>false</c> = SuppressExitedEvent wins (the common case), so
    /// existing suppression tests are unaffected. Opt-in. Used by
    /// <c>SingBoxManagerReconnectStopSuppressionTests</c> to drive the
    /// reconnect-stop late-Exited race deterministically.
    /// </summary>
    public bool SimulateExitedRaceLost { get; set; }

    /// <summary>Test-side: stub the snapshot the production path reads via
    /// <see cref="TryGetSnapshot"/>. Default null mirrors the
    /// "process has exited / metrics unavailable" branch — production tests
    /// for SingBoxManager.GetMetrics empty-default rely on this default.</summary>
    public ProcessSnapshot? SnapshotStub { get; set; }

    /// <summary>Test-side: how many times <see cref="TryGetSnapshot"/> has
    /// been called. Used by wire-shape tests that pin the metrics-refresh
    /// callsite count.</summary>
    public int SnapshotCallCount { get; private set; }

    public ProcessSnapshot? TryGetSnapshot()
    {
        SnapshotCallCount++;
        return SnapshotStub;
    }

    /// <summary>Test-side: emit a stdout line.</summary>
    public void EmitOutput(string line) => OutputLine?.Invoke(this, line);

    /// <summary>Test-side: emit a stderr line.</summary>
    public void EmitError(string line) => ErrorLine?.Invoke(this, line);

    /// <summary>Test-side: signal that the fake process has exited.</summary>
    public void SignalExit(int exitCode)
    {
        // v2.36.0-r4 (brat fix): respect _exitedSuppressed flag. After
        // SuppressExitedEvent, the OS callback wouldn't fire — mirror by
        // not raising the C# Exited event. The exit Task still completes
        // so WaitForExitAsync unblocks (production OS behaviour is the
        // same — process IS dead, just no Exited event raised).
        //
        // v2.41.2-r4: SimulateExitedRaceLost overrides the suppression — it
        // models the OS callback that was ALREADY queued when EnableRaisingEvents
        // flipped, so it fires despite the suppression. _exit.TrySetResult still
        // gates on first-completion, so a subsequent Dispose()/Kill() won't
        // double-fire.
        if (_exit.TrySetResult(exitCode) && (!_exitedSuppressed || SimulateExitedRaceLost))
            Exited?.Invoke(this, exitCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Mimic ProcessHandle: dispose implies kill if still running.
        if (!HasExited) SignalExit(exitCode: -1);
    }
}
