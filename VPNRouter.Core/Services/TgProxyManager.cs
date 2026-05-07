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

        // v2.31.10: never put the secret in plaintext into logs. The redacted
        // copy is what gets emitted; the real one stays on the local PSI only.
        var redactedArgs = RedactSecretInArgs(args);

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

        _logger.Information(
            "[TgProxy] Spawn ProcessStartInfo: FileName={FileName}, Arguments={Arguments}, WorkingDirectory={WorkingDirectory}, CreateNoWindow={CreateNoWindow}, UseShellExecute={UseShellExecute}",
            psi.FileName, redactedArgs, psi.WorkingDirectory, psi.CreateNoWindow, psi.UseShellExecute);

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[TgProxy] Failed to start process (Process.Start returned null)");
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

        _logger.Information("[TgProxy] Spawned PID {Pid}", _process.Id);

        // v2.31.10 — short post-spawn watchdog. Python embeddable failures
        // (missing wheels, broken ._pth, port already in use) frequently
        // exit within ms. Without this probe, the only signal is the
        // generic "[TgProxy] Process exited" warning fired async, which
        // races with the autostart-success log line above and confuses
        // the trail. WaitForExit returns true when the process is gone
        // — log the exit code + stderr tail explicitly.
        try
        {
            if (_process.WaitForExit(2000))
            {
                var exitCode = _process.ExitCode;
                _logger.Error(
                    "[TgProxy] Process exited within 2s of spawn (PID {Pid}, ExitCode {ExitCode}) — likely startup failure",
                    _process.Id, exitCode);

                // StandardError may already be partially drained by ErrorDataReceived.
                // Best-effort: read whatever's left. ReadToEnd on an exited process
                // returns immediately with the remaining buffer.
                try
                {
                    var stderrTail = _process.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stderrTail))
                    {
                        _logger.Error(
                            "[TgProxy] StandardError tail (PID {Pid}): {Stderr}",
                            _process.Id, stderrTail.Trim());
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[TgProxy] StandardError drain after early exit failed");
                }
            }
            else
            {
                _logger.Information(
                    "[TgProxy] Process still alive after 2s probe (PID {Pid})",
                    _process.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[TgProxy] Post-spawn WaitForExit probe raised");
        }
    }

    /// <summary>
    /// v2.31.10 — strip the actual <c>--secret &lt;value&gt;</c> token from
    /// an args string, leaving <c>--secret REDACTED</c>. Used by log lines
    /// only; the real PSI keeps the original.
    /// </summary>
    internal static string RedactSecretInArgs(string args)
    {
        if (string.IsNullOrEmpty(args)) return args;
        // Match: --secret <non-space>+ . Replace with --secret REDACTED.
        return System.Text.RegularExpressions.Regex.Replace(
            args, @"--secret\s+\S+", "--secret REDACTED");
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

    /// <summary>
    /// v2.31.6-r4 (BUG #1 fix): does Windows have an app registered
    /// for the <c>tg://</c> URI scheme? Pre-fix the user got the OS
    /// dialog "We can't open this 'tg' link. Your device needs a new
    /// app to open this link." with no recourse from inside VPNRouter.
    ///
    /// Implementation: probe HKEY_CLASSES_ROOT for the "tg" key. The
    /// presence of any non-empty value or sub-key indicates a handler
    /// is registered. Telegram Desktop installs the registration; web
    /// Telegram + Telegram Web add HKCU shell associations.
    ///
    /// Returns true on non-Windows (no equivalent check makes sense
    /// — macOS/Linux deep-link routing fails through different
    /// pipes that have their own user-visible errors).
    /// </summary>
    public static bool IsTelegramSchemeRegistered()
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
#pragma warning disable CA1416 // Windows-only is guarded above.
            // HKEY_CLASSES_ROOT is the merged HKLM+HKCU classes view.
            using var hkcrTg = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("tg");
            if (hkcrTg != null) return true;

            // Newer Edge/Chrome installs sometimes register tg via
            // HKCU\SOFTWARE\Classes overlay only. Probe explicitly.
            using var hkcuTg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Classes\tg");
            return hkcuTg != null;
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[TgProxy] tg:// scheme probe failed (assume registered)");
            // Defensive: don't block the user just because we couldn't
            // read registry. If the deep-link still fails, the OS
            // dialog is the worst case — same as pre-fix.
            return true;
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
        catch (Exception ex)
        {
            // v2.20.2: surface netstat failures instead of swallowing.
            // If netstat can't run (unusual — it's a Windows built-in) the
            // whole kill-by-port path is dead; we want the log to explain
            // why the Stop button failed rather than leaving the proxy
            // alive with no breadcrumbs.
            Log.Warning(ex, "[TgProxy] KillByPortWindows: netstat invocation failed (port {Port})", port);
            return;
        }

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
                catch (Exception ex)
                {
                    Log.Debug(ex, "[TgProxy] KillByPortUnix: kill PID {Pid} failed", pid);
                }
            }
        }
        catch (Exception ex)
        {
            // v2.20.2: log the outer failure so "Stop did nothing" on Unix
            // has a breadcrumb. Most likely cause: `lsof` not on PATH
            // (unusual on macOS, possible on minimal Linux containers).
            Log.Warning(ex, "[TgProxy] KillByPortUnix: lsof invocation failed (port {Port})", port);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
