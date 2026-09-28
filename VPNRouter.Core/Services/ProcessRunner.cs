#nullable enable

using System.Diagnostics;
using System.Text;

namespace VPNRouter.Core.Services;

public sealed class ProcessRunner : IProcessRunner
{
    private const int StreamDrainTimeoutMs = 1_000;

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
            TryKill(process);

            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        try { process.WaitForExit(StreamDrainTimeoutMs); }
        catch { }

        sw.Stop();

        var exitCode = -1;
        try { exitCode = process.ExitCode; }
        catch { }

        return new ProcessResult(
            ExitCode: exitCode,
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            Duration: sw.Elapsed,
            TimedOut: timedOut);
    }

    public IProcessHandle Start(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = BuildStartInfo(request);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var handle = new ProcessHandle(process, request);
        handle.Begin();
        return handle;
    }

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
        catch { }
    }
}

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
            catch { return true;  }
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
        catch { }
    }

    public ProcessSnapshot? TryGetSnapshot()
    {
        try
        {
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
        Exited?.Invoke(this, SafeExitCode());
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch { return -1; }
    }

    public void SuppressExitedEvent()
    {
        try { _process.EnableRaisingEvents = false; } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _process.EnableRaisingEvents = false; } catch { }

        Kill(entireProcessTree: true);

        try { _process.Dispose(); } catch { }
    }
}
