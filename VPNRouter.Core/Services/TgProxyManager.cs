using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages tg-ws-proxy (Flowseal) process lifecycle for Telegram MTProto proxy.
/// The Windows exe ignores CLI args — it reads config from %APPDATA%/TgWsProxy/config.json.
/// We write that config before launching, then open tg:// deep link to auto-configure Telegram.
/// </summary>
public class TgProxyManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    public TgProxyManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Config path that tg-ws-proxy.exe reads: %APPDATA%/TgWsProxy/config.json
    /// </summary>
    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TgWsProxy");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>
    /// Write config.json so tg-ws-proxy uses our secret/port.
    /// Must be called BEFORE starting the exe.
    /// </summary>
    private void WriteConfig(int port, string secret)
    {
        Directory.CreateDirectory(ConfigDir);

        var config = new Dictionary<string, object>
        {
            ["port"] = port,
            ["host"] = "127.0.0.1",
            ["secret"] = secret,
            ["dc_ip"] = new[] { "2:149.154.167.220", "4:149.154.167.220" },
            ["verbose"] = false,
            ["check_updates"] = false, // we manage updates ourselves
            ["buf_kb"] = 256,
            ["pool_size"] = 4,
            ["cfproxy"] = true,
            ["cfproxy_priority"] = true,
            ["cfproxy_user_domain"] = "",
            ["log_max_mb"] = 5
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
        _logger.Information("[TgProxy] Wrote config to {Path}", ConfigPath);
    }

    /// <summary>
    /// Start tg-ws-proxy.exe. Writes config.json first, then launches exe.
    /// The exe will show a tray icon (can't suppress) — but we also open tg:// link.
    /// </summary>
    public void Start(int port, string secret)
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

        // Write config with OUR secret/port — the exe reads from %APPDATA%/TgWsProxy/config.json
        WriteConfig(port, secret);

        // Also mark first-run as done so the popup doesn't appear
        try
        {
            var marker = Path.Combine(ConfigDir, ".first_run_done_mtproto");
            if (!File.Exists(marker))
                File.WriteAllText(marker, "");
        }
        catch { }

        _logger.Information("[TgProxy] Starting: {Exe}", exePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
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

        _logger.Information("[TgProxy] Started (PID {Pid})", _process.Id);
    }

    /// <summary>
    /// Build the tg://proxy deep link. Opens Telegram and prompts to enable/disable this proxy.
    /// dd prefix = random padding mode (standard).
    /// </summary>
    public static string BuildProxyLink(string host, int port, string secret)
    {
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    /// <summary>
    /// Open tg://proxy link in default handler (Telegram Desktop).
    /// This triggers the "Enable proxy?" dialog in Telegram.
    /// </summary>
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
