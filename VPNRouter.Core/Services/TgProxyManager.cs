using System.Diagnostics;
using System.Net.NetworkInformation;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages tg-ws-proxy process lifecycle via Python embeddable.
/// Runs headless: python.exe -m proxy.tg_ws_proxy --port X --secret Y
/// No tray icon, no GUI — pure background process.
/// </summary>
public class TgProxyManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    /// <summary>Last parsed stats line from stdout.</summary>
    public string? LastStats { get; private set; }

    /// <summary>Fired when stats line is parsed from output.</summary>
    public event Action<string>? StatsUpdated;

    public TgProxyManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Start tg-ws-proxy via Python embeddable. No tray, no GUI.
    /// </summary>
    public void Start(int port, string secret, bool verbose = false)
    {
        if (IsRunning)
        {
            _logger.Warning("[TgProxy] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        if (!File.Exists(TgProxyUpdater.PythonExePath))
            throw new FileNotFoundException("Python not found. Download tg-ws-proxy first.");

        if (!Directory.Exists(TgProxyUpdater.ProxySourceDir))
            throw new FileNotFoundException("Proxy source not found. Download tg-ws-proxy first.");

        var args = $"-m proxy.tg_ws_proxy --port {port} --host 127.0.0.1 --secret {secret}";
        if (verbose) args += " --verbose";

        _logger.Information("[TgProxy] Starting: python.exe {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = TgProxyUpdater.PythonExePath,
            Arguments = args,
            WorkingDirectory = TgProxyUpdater.TgProxyDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[TgProxy] Failed to start process");
            throw new InvalidOperationException("Failed to start tg-ws-proxy");
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Warning("[TgProxy] Process exited (exit code: {Code})", _process?.ExitCode);
        };

        // Capture stdout/stderr for stats and error detection
        _process.OutputDataReceived += OnOutputData;
        _process.ErrorDataReceived += OnOutputData;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.Information("[TgProxy] Started (PID {Pid})", _process.Id);
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        // Parse stats line: "stats: total=X active=Y ws=Z ..."
        if (e.Data.Contains("stats:"))
        {
            LastStats = e.Data;
            StatsUpdated?.Invoke(e.Data);
        }
    }

    /// <summary>
    /// Build the tg://proxy deep link for Telegram Desktop.
    /// dd prefix = random padding mode (standard MTProto).
    /// </summary>
    public static string BuildProxyLink(string host, int port, string secret)
    {
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    /// <summary>Open tg://proxy link in Telegram Desktop.</summary>
    public static void OpenInTelegram(string host, int port, string secret)
    {
        var url = BuildProxyLink(host, port, secret);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TgProxy] Failed to open tg:// link");
        }
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

    /// <summary>Check if tg-ws-proxy is running by checking if the port is in use.</summary>
    public static bool IsAnyRunning(int port = 1443)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(l => l.Port == port);
        }
        catch { return false; }
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
