#nullable enable
// ============================================================================
// ProcessRunner.cs — concrete IProcessRunner backed by System.Diagnostics.Process
// ============================================================================
//
// Plain wrapper. No retries, no shell, no globbing. Streams are read
// asynchronously via Process.OutputDataReceived / ErrorDataReceived so we
// don't deadlock on a child that fills its pipe buffer mid-execution
// (the classic "Process won't exit" gotcha).
//
// Security notes (Gate 4 security-review focus):
//
//   * We use ProcessStartInfo.ArgumentList (not Arguments string), so
//     each argument is passed verbatim to the OS exec without shell
//     interpretation. Callers can pass user-tainted strings safely
//     (e.g. a server hostname) without manual quoting.
//
//   * UseShellExecute is hard-wired to false. The shell would introduce
//     PATH-resolution semantics + command-line splitting; we don't want
//     either when the caller has already given us a parsed argument list.
//     (Side note: this means `Verb = "runas"` UAC paths won't work via
//     this seam — those still go through Process directly. Phase 2G will
//     decide whether to extend the abstraction or keep elevation special.)
//
//   * CreateNoWindow is true; no console flash on Win-form callers.
//
//   * EnvironmentOverrides are applied via StartInfo.Environment (key/value
//     dictionary), not by string concatenation, so there's no env-injection
//     risk if a value contains '=' or newlines.
//
//   * Process killing on cancellation uses entireProcessTree:true to
//     guarantee no orphans on timeout; that's required for sc/netsh which
//     can fork children.
// ============================================================================

using System.Diagnostics;
using System.Text;

namespace VPNRouter.Core.Services;

/// <summary>
/// Default <see cref="IProcessRunner"/>. Wraps <see cref="Process"/>.
/// Stateless and safe to share across threads — every call creates a
/// fresh process; no class-level mutable state.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How long to wait for stream-reader tasks to drain after the
    /// process has exited. Streams may have a small backlog still in flight
    /// when WaitForExitAsync returns; without a short drain we'd return
    /// truncated stdout. 1s is plenty for "small CLI" use cases (sc query,
    /// netsh show); never observed exceeded in practice.</summary>
    private const int StreamDrainTimeoutMs = 1_000;

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = BuildStartInfo(request);
        if (request.StdinInput != null) psi.RedirectStandardInput = true;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        if (request.CaptureStdout)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };
        }
        if (request.CaptureStderr)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
        }

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Process.Start returned false for '{request.ExecutablePath}'.");
        }

        if (request.CaptureStdout) process.BeginOutputReadLine();
        if (request.CaptureStderr) process.BeginErrorReadLine();

        if (request.StdinInput != null)
        {
            await process.StandardInput.WriteAsync(request.StdinInput.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        // Combine caller cancellation with optional timeout. If the timeout
        // fires first we record TimedOut=true; if the caller cancels we
        // re-throw OperationCanceledException after killing the process.
        using var timeoutCts = request.Timeout.HasValue
            ? new CancellationTokenSource(request.Timeout.Value)
            : null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts?.Token ?? CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Always kill on cancel — the OS keeps the process alive after
            // WaitForExitAsync's task is cancelled. entireProcessTree:true
            // guarantees any sc/netsh forks die too.
            TryKill(process);

            // Distinguish "caller cancelled" vs "our timeout fired" so the
            // contract is: caller-cancel = throw, timeout = return TimedOut.
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        // Drain stream readers. WaitForExitAsync can return slightly before
        // the OutputDataReceived/ErrorDataReceived tasks have flushed final
        // lines (Process internals run those on the threadpool).
        try { process.WaitForExit(StreamDrainTimeoutMs); }
        catch { /* defensive — drain best-effort */ }

        sw.Stop();

        var exitCode = -1;
        try { exitCode = process.ExitCode; }
        catch { /* killed before exit code available */ }

        return new ProcessResult(
            ExitCode: exitCode,
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            Duration: sw.Elapsed,
            TimedOut: timedOut);
    }

    /// <inheritdoc />
    public IProcessHandle Start(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = BuildStartInfo(request);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var handle = new ProcessHandle(process, request);
        handle.Begin();
        return handle;
    }

    /// <summary>
    /// Common ProcessStartInfo builder. Keeps the security-relevant flags
    /// in one place: UseShellExecute=false, CreateNoWindow=true,
    /// ArgumentList (no shell-splitting).
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(ProcessRequest r)
    {
        var psi = new ProcessStartInfo
        {
            FileName = r.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = r.CaptureStdout,
            RedirectStandardError = r.CaptureStderr,
            WorkingDirectory = r.WorkingDirectory ?? string.Empty,
        };
        foreach (var arg in r.Arguments) psi.ArgumentList.Add(arg);
        if (r.EnvironmentOverrides != null)
        {
            foreach (var kv in r.EnvironmentOverrides) psi.Environment[kv.Key] = kv.Value;
        }
        return psi;
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { /* race with natural exit — fine */ }
    }
}

/// <summary>
/// Concrete <see cref="IProcessHandle"/> wrapping a live <see cref="Process"/>.
/// Manages Process disposal, stream wiring, and the Exited event hop.
/// </summary>
internal sealed class ProcessHandle : IProcessHandle
{
    private readonly Process _process;
    private readonly ProcessRequest _request;
    private int _disposed;

    public ProcessHandle(Process process, ProcessRequest request)
    {
        _process = process;
        _request = request;
        _process.Exited += OnProcessExited;
    }

    public int Pid { get; private set; }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; /* disposed / killed */ }
        }
    }

    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;
    public event EventHandler<int>? Exited;

    internal void Begin()
    {
        if (_request.CaptureStdout)
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) OutputLine?.Invoke(this, e.Data);
            };
        if (_request.CaptureStderr)
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) ErrorLine?.Invoke(this, e.Data);
            };

        if (!_process.Start())
            throw new InvalidOperationException(
                $"Process.Start returned false for '{_request.ExecutablePath}'.");

        Pid = _process.Id;

        if (_request.CaptureStdout) _process.BeginOutputReadLine();
        if (_request.CaptureStderr) _process.BeginErrorReadLine();
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        return SafeExitCode();
    }

    public void Kill(bool entireProcessTree = true)
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: entireProcessTree);
        }
        catch { /* idempotent — race with natural exit is fine */ }
    }

    /// <inheritdoc />
    public ProcessSnapshot? TryGetSnapshot()
    {
        try
        {
            // Mirror the legacy SingBoxManager.GetMetrics pattern:
            // Refresh() snapshots the current Process counters from the OS;
            // without it WorkingSet64 etc. return cached (potentially stale)
            // values from the last refresh tick.
            if (_process.HasExited) return null;
            _process.Refresh();
            return new ProcessSnapshot(
                WorkingSetBytes: _process.WorkingSet64,
                TotalProcessorTime: _process.TotalProcessorTime,
                StartTime: _process.StartTime);
        }
        catch { return null; }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Fired by the runtime on a threadpool thread when the OS notifies
        // us the process has exited. Snapshot exit code before raising so
        // subscribers don't NRE on a racing Dispose.
        Exited?.Invoke(this, SafeExitCode());
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch { return -1; }
    }

    public void SuppressExitedEvent()
    {
        // v2.36.0-r4 (brat 2026-05-24 — intentional-stop regression fix).
        // Disable the OS-level Exited event subscription so a subsequent
        // Kill from intentional Stop path doesn't raise a spurious
        // "process crashed" event to subscribers. Pre-r4 only Dispose
        // did this (line 307 below), but Dispose ran in StopInternal's
        // `finally` AFTER Kill+WaitForExit had already completed and
        // OnProcessExited had fired. SingBoxManager.StopInternal now
        // calls this BEFORE Kill so the subscription is gone before
        // the OS can raise the event.
        //
        // Idempotent. Defensive try/catch — if _process was already
        // disposed by a racing path, we still no-op silently.
        try { _process.EnableRaisingEvents = false; } catch { /* defensive */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Mirror SingBoxManager.Stop pattern: disable Exited callback BEFORE
        // killing so we don't fire a spurious Exited event on intentional
        // disposal. (See VPNRouter.Core/CLAUDE.md "SingBoxManager intentional
        // stop" section.) — Also explicit via SuppressExitedEvent above for
        // callers that want to disable without disposing the handle.
        try { _process.EnableRaisingEvents = false; } catch { /* defensive */ }

        Kill(entireProcessTree: true);

        try { _process.Dispose(); } catch { /* defensive */ }
    }
}
