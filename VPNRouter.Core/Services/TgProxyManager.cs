using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Linq;
using System.Collections.Generic;
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

    /// <summary>
    /// Kill ALL tg-ws-proxy processes system-wide.
    ///
    /// v2.20.0: the actual tg-ws-proxy runs as <c>python.exe -m
    /// proxy.tg_ws_proxy …</c> (see <see cref="StartAsync"/>). Enumerating
    /// <c>tg-ws-proxy</c> / <c>TgWsProxy_windows</c> by process name matched
    /// NOTHING — those names don't exist. The page's Stop button was calling
    /// this helper expecting processes to die, but nothing happened unless
    /// the current app instance still held <see cref="_process"/> (i.e. had
    /// launched the proxy in this session). If tg-ws-proxy was started by
    /// the Windows Service or a previous session, Stop was effectively a
    /// no-op and the proxy kept serving traffic.
    ///
    /// Fix: port-based kill. Whoever is listening on
    /// <paramref name="port"/> gets its PID resolved and killed. Covers
    /// every way the proxy could have been started.
    /// </summary>
    /// <param name="port">TgProxy port from settings. Pass the actual
    /// configured port; otherwise this is a no-op.</param>
    public static void KillAll(int port = 1443)
    {
        // Legacy path: still sweep the old-style names in case some user
        // has a really old build launching the proxy differently.
        foreach (var name in new[] { "tg-ws-proxy", "TgWsProxy_windows" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
                catch { }
                finally { proc.Dispose(); }
            }
        }

        // Port-based kill — the canonical path.
        KillByPort(port);
    }

    /// <summary>
    /// Find the PID listening on <paramref name="port"/> and terminate it.
    /// Cross-platform: netstat+taskkill on Windows, lsof+kill on Unix.
    /// Silent on failure; caller checks <see cref="IsAnyRunning"/> after.
    /// </summary>
    public static void KillByPort(int port)
    {
        if (port <= 0) return;

        try
        {
            // Quick guard — if nothing's listening, don't even spawn netstat.
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (!listeners.Any(l => l.Port == port)) return;
        }
        catch { /* fall through — still try to kill */ }

        if (OperatingSystem.IsWindows())
        {
            KillByPortWindows(port);
        }
        else
        {
            KillByPortUnix(port);
        }
    }

    private static void KillByPortWindows(int port)
    {
        // netstat -ano | findstr :PORT  → collect PIDs listening on that port
        var pids = new HashSet<int>();
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // Lines look like:
            //   TCP    0.0.0.0:1443    0.0.0.0:0    LISTENING       12345
            foreach (var line in stdout.Split('\n'))
            {
                if (!line.Contains("LISTENING")) continue;
                if (!line.Contains($":{port} ")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid))
                    pids.Add(pid);
            }
        }
        catch { return; }

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { /* process may have exited already */ }
        }
    }

    private static void KillByPortUnix(int port)
    {
        // lsof -iTCP:PORT -sTCP:LISTEN -t  → prints just the PID(s)
        try
        {
            var psi = new ProcessStartInfo("lsof", $"-iTCP:{port} -sTCP:LISTEN -t")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in stdout.Split('\n'))
            {
                if (!int.TryParse(line.Trim(), out var pid)) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
