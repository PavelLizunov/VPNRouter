using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Linq;
using System.Collections.Generic;
using System.Text;
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
    private readonly IProcessRunner _runner;
    // Phase 3+ (2026-05-21): IProcessRunner adoption (long-lived spawn).
    // The legacy `Process? _process` field is gone — the handle owns Process
    // lifetime now. Captured-stderr ring buffer feeds the early-exit log,
    // replacing the post-exit StandardError.ReadToEnd() drain (which is
    // unreachable through IProcessHandle by design — stderr is consumed
    // exclusively via ErrorLine events).
    private IProcessHandle? _handle;
    private readonly StringBuilder _capturedStderr = new();
    private readonly object _stderrGate = new();
    private bool _disposed;

    /// <summary>Test-only seam: swap in a fake for the long-lived
    /// python.exe spawn. Production paths use the default
    /// <see cref="ProcessRunner"/>. Not thread-safe — assumes serial
    /// xUnit execution within the fixture; tests reset in try/finally
    /// (or use the per-instance ctor injection below).</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public bool IsRunning => _handle != null && !_handle.HasExited;
    public int? Pid => IsRunning ? _handle?.Pid : null;

    /// <summary>Last parsed stats line from stdout.</summary>
    public string? LastStats { get; private set; }

    /// <summary>Fired when stats line is parsed from output.</summary>
    public event Action<string>? StatsUpdated;

    public TgProxyManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
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

        // Phase 3+ (2026-05-21): argv list mirrors the legacy `Arguments`
        // string verbatim. Building a List<string> by whitespace-splitting is
        // safe here because every value (port int, host literal, hex secret,
        // module path) contains no whitespace itself — the legacy string was
        // already shell-parseable as a list of bare tokens.
        var argv = new List<string>
        {
            "-m", "proxy.tg_ws_proxy",
            "--port", port.ToString(),
            "--host", "127.0.0.1",
            "--secret", secret,
        };
        if (verbose) argv.Add("--verbose");

        var request = new ProcessRequest(
            ExecutablePath: TgProxyUpdater.PythonExePath,
            Arguments: argv,
            WorkingDirectory: TgProxyUpdater.TgProxyDir,
            CaptureStdout: true,
            CaptureStderr: true);

        _logger.Information(
            "[TgProxy] Spawn ProcessStartInfo: FileName={FileName}, Arguments={Arguments}, WorkingDirectory={WorkingDirectory}, CreateNoWindow={CreateNoWindow}, UseShellExecute={UseShellExecute}",
            request.ExecutablePath, redactedArgs, request.WorkingDirectory, true, false);

        // Reset captured stderr for this spawn — the post-exit log on early
        // failure pulls from this ring buffer instead of StandardError.ReadToEnd
        // (which is unreachable via IProcessHandle).
        lock (_stderrGate) _capturedStderr.Clear();

        try
        {
            _handle = _runner.Start(request);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[TgProxy] Failed to start process");
            throw new InvalidOperationException("Failed to start tg-ws-proxy", ex);
        }

        // IProcessHandle wires EnableRaisingEvents = true at construction
        // (ProcessRunner.cs:155). The Exited callback fires on a threadpool
        // thread; capture the handle in a local so the lambda sees the right
        // instance even if Stop() nulls _handle mid-flight.
        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            _logger.Warning("[TgProxy] Process exited (exit code: {Code})", code);
        };

        // Single OnOutputLine handler subscribes to BOTH stdout and stderr —
        // mirrors the legacy `OutputDataReceived += OnOutputData;
        // ErrorDataReceived += OnOutputData;` pair. Stats lines come on stdout
        // in production; the unified subscription keeps stderr noise visible
        // via the same StatsUpdated channel if Python ever rotates which
        // stream stats land on.
        startedHandle.OutputLine += OnOutputLineHandler;
        startedHandle.ErrorLine += OnErrorLineHandler;

        _logger.Information("[TgProxy] Spawned PID {Pid}", startedHandle.Pid);

        // v2.31.10 — short post-spawn watchdog. Python embeddable failures
        // (missing wheels, broken ._pth, port already in use) frequently
        // exit within ms. Without this probe, the only signal is the
        // generic "[TgProxy] Process exited" warning fired async, which
        // races with the autostart-success log line above and confuses
        // the trail. WaitForExitAsync returns naturally when the process is
        // gone; the linked 2s CTS fires OperationCanceledException if it
        // doesn't — same semantics as the legacy `WaitForExit(2000)` bool.
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            try
            {
                // .GetAwaiter().GetResult() preserves the legacy sync Start
                // signature so callers don't need refactoring. The async
                // WaitForExitAsync is the natural shape for the new seam.
                var exitCode = startedHandle.WaitForExitAsync(probeCts.Token)
                    .GetAwaiter().GetResult();

                _logger.Error(
                    "[TgProxy] Process exited within 2s of spawn (PID {Pid}, ExitCode {ExitCode}) — likely startup failure",
                    startedHandle.Pid, exitCode);

                // Captured stderr ring buffer replaces the legacy
                // StandardError.ReadToEnd() — same observable effect
                // (an error-tail log line for the operator), now sourced
                // from the ErrorLine event stream which has been
                // accumulating since spawn.
                string stderrTail;
                lock (_stderrGate) stderrTail = _capturedStderr.ToString();

                if (!string.IsNullOrWhiteSpace(stderrTail))
                {
                    _logger.Error(
                        "[TgProxy] StandardError tail (PID {Pid}): {Stderr}",
                        startedHandle.Pid, stderrTail.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                // 2s elapsed without natural exit — process still alive.
                // Same path as legacy `WaitForExit(2000) == false`.
                _logger.Information(
                    "[TgProxy] Process still alive after 2s probe (PID {Pid})",
                    startedHandle.Pid);
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

    // Phase 3+ (2026-05-21): IProcessHandle event shape uses
    // `EventHandler<string>` where the string is the line directly (no
    // DataReceivedEventArgs wrapper). The line-empty guard from the legacy
    // OnOutputData(...) is preserved by the handle implementation itself —
    // ProcessHandle.Begin (ProcessRunner.cs:231-238) already filters
    // `e.Data != null` before raising OutputLine/ErrorLine, so subscribers
    // see only real lines.
    private void OnOutputLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Parse stats line: "stats: total=X active=Y ws=Z ..."
        if (line.Contains("stats:"))
        {
            LastStats = line;
            StatsUpdated?.Invoke(line);
        }
    }

    private void OnErrorLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Capture stderr to a ring buffer for the post-exit error log — the
        // legacy code drained StandardError.ReadToEnd() on early-exit, but
        // IProcessHandle exposes stderr only via the ErrorLine event stream.
        // Accumulating the lines as they arrive gives the early-exit log path
        // the same observable result (an error tail) without holding onto the
        // raw stream reader. Cap the buffer to keep memory bounded for
        // long-lived runs where stderr might keep emitting warnings.
        const int MaxStderrBuffer = 16 * 1024;
        lock (_stderrGate)
        {
            if (_capturedStderr.Length < MaxStderrBuffer)
            {
                _capturedStderr.AppendLine(line);
            }
        }

        // Stats lines historically arrived on either stdout OR stderr (the
        // legacy code subscribed both event types to the same OnOutputData
        // handler). Mirror that behaviour: stderr lines also feed the stats
        // parser so a Python rotation between streams doesn't kill stats UX.
        if (line.Contains("stats:"))
        {
            LastStats = line;
            StatsUpdated?.Invoke(line);
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
        if (_handle == null || _handle.HasExited)
        {
            _handle?.Dispose();
            _handle = null;
            return;
        }

        var handle = _handle;
        _logger.Information("[TgProxy] Stopping (PID {Pid})", handle.Pid);

        try
        {
            // v2.36.0-r5 (audit followup to brat r4 fix): suppress Exited
            // event BEFORE Kill so the OS notification doesn't fire as a
            // false "[TgProxy] Process exited (exit code: -1)" log entry
            // on intentional Stop. Same Phase 3+ refactor regression that
            // affected SingBoxManager (fixed in r4) — TgProxy wires
            // startedHandle.Exited just like SingBoxManager. Sibling bug.
            handle.SuppressExitedEvent();
            handle.Kill(entireProcessTree: true);

            // Symmetric replacement for the legacy `_process.WaitForExit(3000)`
            // synchronisation barrier. The .GetAwaiter().GetResult() keeps
            // Stop() sync-callable.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
            try
            {
                handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 3s elapsed — process may still be exiting. Dispose below
                // will fire the final kill via ProcessHandle.Dispose.
                _logger.Debug("[TgProxy] WaitForExitAsync timeout (3s) — proceeding to dispose");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TgProxy] Error stopping");
        }
        finally
        {
            try { handle.Dispose(); } catch { /* defensive */ }
            _handle = null;
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
    /// the current app instance still held an active handle (i.e. had
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
