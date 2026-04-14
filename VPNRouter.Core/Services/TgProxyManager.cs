using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages tg-ws-proxy (Flowseal) process lifecycle for Telegram MTProto proxy.
/// Runs as a hidden background process — no GUI, no window. Captures stdout for stats.
/// </summary>
public class TgProxyManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    /// <summary>Last parsed stats line from stdout (e.g. "total=10 active=2 ...").</summary>
    public string? LastStats { get; private set; }

    /// <summary>Fired when a new line is received on stdout/stderr.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Fired when stats line is parsed from output.</summary>
    public event Action<string>? StatsUpdated;

    public TgProxyManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Start tg-ws-proxy.exe as a hidden background process.
    /// </summary>
    /// <param name="port">Listen port (default 1443).</param>
    /// <param name="secret">MTProto secret (32 hex chars).</param>
    /// <param name="verbose">Enable debug logging in tg-ws-proxy.</param>
    public void Start(int port, string secret, bool verbose = false)
    {
        if (IsRunning)
        {
            _logger.Warning("[TgProxy] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        var exePath = TgProxyUpdater.ExePath;
        if (!File.Exists(exePath))
        {
            _logger.Error("[TgProxy] tg-ws-proxy.exe not found at {Path}", exePath);
            throw new FileNotFoundException("tg-ws-proxy.exe not found. Download it first.");
        }

        var args = $"--port {port} --host 127.0.0.1 --secret {secret}";
        if (verbose) args += " --verbose";

        _logger.Information("[TgProxy] Starting: {Exe} {Args}", exePath, args);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = TgProxyUpdater.TgProxyDir
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[TgProxy] Failed to start process");
            throw new InvalidOperationException("Failed to start tg-ws-proxy.exe");
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Warning("[TgProxy] Process exited (exit code: {Code})", _process?.ExitCode);
        };

        // Capture stdout/stderr for stats and logging
        _process.OutputDataReceived += OnOutputData;
        _process.ErrorDataReceived += OnOutputData;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.Information("[TgProxy] Started (PID {Pid})", _process.Id);
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        OutputReceived?.Invoke(e.Data);

        // Parse stats line: "stats: total=X active=Y ws=Z ..."
        if (e.Data.Contains("stats:"))
        {
            LastStats = e.Data;
            StatsUpdated?.Invoke(e.Data);
        }
    }

    /// <summary>
    /// Build the tg://proxy link for Telegram Desktop configuration.
    /// dd prefix = random padding mode (standard).
    /// </summary>
    public static string BuildProxyLink(string host, int port, string secret)
    {
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited)
        {
            _process = null;
            return;
        }

        _logger.Information("[TgProxy] Stopping (PID {Pid})", _process.Id);

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TgProxy] Error stopping");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _logger.Information("[TgProxy] Stopped");
        }
    }

    /// <summary>Check if tg-ws-proxy is running (from previous session or manual start).</summary>
    public static bool IsAnyRunning()
    {
        return Process.GetProcessesByName("tg-ws-proxy").Length > 0
            || Process.GetProcessesByName("TgWsProxy_windows").Length > 0;
    }

    /// <summary>Kill ALL tg-ws-proxy processes system-wide.</summary>
    public static void KillAll()
    {
        foreach (var name in new[] { "tg-ws-proxy", "TgWsProxy_windows" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
                catch { }
                finally { proc.Dispose(); }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
