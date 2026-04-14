using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages TgWsProxy process lifecycle.
/// Local MTProto proxy for Telegram — listens on localhost:port.
/// Not Cygwin — standard exe, can use CreateNoWindow=true.
/// </summary>
public class TgWsProxyManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    public TgWsProxyManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>Start TgWsProxy on the given port.</summary>
    public void Start(int port = 1443)
    {
        if (IsRunning)
        {
            _logger.Warning("[TgWsProxy] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        var exePath = TgWsProxyUpdater.ExePath;
        if (!File.Exists(exePath))
        {
            _logger.Error("[TgWsProxy] exe not found at {Path}", exePath);
            throw new FileNotFoundException("TgWsProxy not found. Download it first.");
        }

        var args = $"--port {port} --host 127.0.0.1";
        _logger.Information("[TgWsProxy] Starting: {Exe} {Args}", exePath, args);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[TgWsProxy] Failed to start process");
            throw new InvalidOperationException("Failed to start TgWsProxy");
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Warning("[TgWsProxy] Process exited (exit code: {Code})", _process?.ExitCode);
        };

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.Information("[TgWsProxy] Started (PID {Pid}) on port {Port}", _process.Id, port);
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited)
        {
            _process = null;
            return;
        }

        _logger.Information("[TgWsProxy] Stopping (PID {Pid})", _process.Id);

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TgWsProxy] Error stopping");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _logger.Information("[TgWsProxy] Stopped");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
