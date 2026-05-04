using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum SingBoxState { Stopped, Starting, Running, Restarting, Failed }

public class SingBoxManager : IDisposable
{
    private readonly SingBoxSettings _settings;
    private readonly ILogger _logger;

    private Process? _process;
    private string _currentConfigPath = string.Empty;
    private bool _disposed;
    private TunOwnershipLock _tunLock;

    /// <summary>
    /// Linux-only: did <see cref="LaunchProcess"/> elevate via pkexec?
    /// If false, sing-box was spawned as a plain user process (possible when
    /// the binary has CAP_NET_ADMIN + CAP_NET_BIND_SERVICE from the .deb
    /// postinst / update-helper's setcap call). In that case
    /// <see cref="Stop"/> can just kill the tracked PID directly —
    /// no pkexec round-trip, no password prompt.
    /// </summary>
    private bool _linuxUsedPkexec;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public SingBoxState State { get; private set; } = SingBoxState.Stopped;
    public int? Pid => _process?.HasExited == false ? _process.Id : null;
    public event EventHandler? Crashed;
    /// <summary>Fires after every successful LaunchProcess — initial start
    /// AND restart after crash. Listeners (e.g. CLI StateFile writer) use
    /// this to keep their persisted PID in sync with the live process.</summary>
    public event Action<int>? Started;

    public SingBoxManager(SingBoxSettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger ?? Log.Logger;
        _tunLock = TunOwnershipLock.Instance(_logger);

        // Release lock on ungraceful process exit (Environment.Exit, Ctrl+C, crash).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _tunLock.Dispose();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

    public void Start(SingBoxConfig config) =>
        StartWithJson(ConfigGenerator.Serialize(config));

    public void StartWithJson(string configJson)
    {
        if (State == SingBoxState.Running)
        {
            _logger.Warning("[SingBoxManager] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        // Take exclusive ownership of the TUN adapter. If another VPNRouter
        // instance (desktop UI / Windows Service / CLI) already owns it,
        // bail out instead of fighting over the same TUN device.
        if (!_tunLock.TryAcquire())
        {
            throw new TunOwnershipException(
                "Another VPNRouter instance already owns the TUN adapter. " +
                "Stop the other instance (e.g. disable Windows Service autostart) and try again.");
        }

        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
            : AppPaths.SingBoxExePath;

        if (!File.Exists(exePath))
        {
            _tunLock.Release();
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }

        RotateSingBoxLog();
        _currentConfigPath = WriteJsonToDisk(configJson);

        _logger.Information("[SingBoxManager] Starting sing-box with config: {Config}", _currentConfigPath);

        State = SingBoxState.Starting;
        try
        {
            LaunchProcess(exePath);
        }
        catch
        {
            _tunLock.Release();
            throw;
        }
    }

    public void Stop() => StopInternal(releaseLock: true);

    private void StopInternal(bool releaseLock)
    {
        _logger.Information("[SingBoxManager] Stopping sing-box (PID {Pid})", Pid);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            // sing-box runs as root — killed via privilege-escalation tool.
            // macOS: sudo (NOPASSWD from sudoers, set up at first Connect).
            // Linux: pkexec (polkit GUI prompt — same path used to start it).
            //
            // v2.21.3: Linux was previously falling through to the Windows
            // path below. That path checks _process?.HasExited and returns
            // early when true — which it always is on Linux, because our
            // _process reference is the short-lived pkexec wrapper that
            // exits immediately after spawning the root sing-box child.
            // Result: Stop was a no-op and sing-box kept running. Same
            // bug macOS would have had before we routed it here.
            //
            // v2.28.0: Linux capability-mode path — if LaunchProcess spawned
            // sing-box as a plain user process (it has CAP_NET_ADMIN via
            // setcap), then `_process` points at the REAL sing-box (not a
            // short-lived pkexec wrapper), and WE OWN that PID. Just Kill()
            // it directly — no elevation needed, no password prompt.
            if (OperatingSystem.IsLinux() && !_linuxUsedPkexec)
            {
                try
                {
                    if (_process != null)
                    {
                        _process.EnableRaisingEvents = false;
                        if (!_process.HasExited)
                        {
                            _process.Kill(entireProcessTree: true);
                            _process.WaitForExit(5000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SingBoxManager] Linux capability-mode Stop failed, falling back to pkill");
                    // Best-effort fallback — try plain pkill as user. Will
                    // succeed because we own the process in capability mode.
                    try
                    {
                        using var pk = Process.Start(new ProcessStartInfo("/usr/bin/pkill", "-f sing-box")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        });
                        pk?.WaitForExit(3000);
                    }
                    catch { /* swallow, State will be Stopped anyway */ }
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                    State = SingBoxState.Stopped;
                    if (releaseLock) _tunLock.Release();
                    _logger.Information("[SingBoxManager] sing-box stopped (Linux capability mode, no pkexec)");
                }
                return;
            }

            // pkexec path (pre-v2.28 behaviour on Linux + always on macOS).
            //
            // v2.29.0-r5: stop became unreliable on Linux. User report
            // 2026-04-29: «не могу остановить vpn ... кнопа stop не убивает
            // sing-box». Pre-r5 the code fired ONE pkexec pkill and trusted
            // its WaitForExit without checking the exit code or whether
            // sing-box actually died. Failure modes that silently presented
            // as "Stopped" in the UI:
            //   - User dismissed the pkexec password prompt → exit 126,
            //     sing-box still alive.
            //   - Polkit agent not running (minimal WMs) → exit 127.
            //   - pkill matched the wrong PID list (sing-box killed via
            //     unexpected signal handler / refused).
            // r5 escalation chain:
            //   1. Try plain user pkill (works in capability mode + .deb
            //      installs that drop us into the cgroup as the owner).
            //   2. If still alive, pkexec pkill -KILL (SIGKILL — survives
            //      most signal masks).
            //   3. If still alive, sudo pkill -KILL (NOPASSWD if the
            //      sudoers entry was set up at first connect).
            //   4. Verify after each step that sing-box is gone (no
            //      Clash API + no pgrep hit). Log each step's outcome.
            if (_process != null) _process.EnableRaisingEvents = false;

            try
            {
                LinuxStopEscalationChain();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Error stopping sing-box");
            }
            finally
            {
                _process?.Dispose();
                _process = null;
                State = SingBoxState.Stopped;
                if (releaseLock) _tunLock.Release();
                _logger.Information("[SingBoxManager] sing-box stopped");
            }
            return;
        }

        if (_process == null || _process.HasExited)
        {
            // v2.30.1-r5: log this branch — pre-r5 it was silent, which
            // made the user-reported "Stop pressed but no log lines and
            // adapter remained" problem hard to diagnose. Explicitly
            // mark that we're in the post-crash cleanup path.
            _logger.Information(
                "[SingBoxManager] Stop called but sing-box already exited (process={ProcState}) — running cleanup-only path",
                _process == null ? "null" : "HasExited");
            State = SingBoxState.Stopped;
            if (releaseLock) _tunLock.Release();

            // v2.30.1-r5: belt-and-braces orphan cleanup. OnProcessExited
            // (above) already does this when the Exited callback fires,
            // but if the process was force-killed AND the callback was
            // suppressed (EnableRaisingEvents=false from a prior Stop),
            // we'd skip the disable. Run it again here so the orphan
            // can't slip through.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    TunAdapterDiagnostics.DisableOrphanedAdapter(
                        _logger, "VPNRouter-TUN", "SingBoxManager.StopInternal.early");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
                }
            }
            return;
        }

        _process.EnableRaisingEvents = false;

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Error while stopping process");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            State = SingBoxState.Stopped;
            _tunLock.Release();
            _logger.Information("[SingBoxManager] sing-box stopped");
        }
    }

    public void Restart()
    {
        _logger.Information("[SingBoxManager] Restarting sing-box");
        State = SingBoxState.Restarting;
        // Keep the TUN lock across restart so another instance can't slip in
        // during the brief window between Stop and LaunchProcess.
        StopInternal(releaseLock: false);

        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
            : AppPaths.SingBoxExePath;
        LaunchProcess(exePath);
    }

    public void ReloadConfig(SingBoxConfig config, bool forceRestart = false) =>
        ReloadConfigJson(ConfigGenerator.Serialize(config), forceRestart);

    /// <summary>
    /// Apply a new sing-box config. By default tries hot-reload first
    /// (Clash API <c>PUT /configs</c>) and only falls back to a full
    /// kill+restart if hot-reload is unavailable.
    ///
    /// <para>v2.31.7-r1: <paramref name="forceRestart"/> bypasses the
    /// hot-reload attempt entirely and goes straight to kill+restart.
    /// Required for structural changes — namely RoutingMode and TUN
    /// fingerprint flips — where hot-reload swaps the in-memory config
    /// successfully (HTTP 204) but does NOT re-lay TUN routes / DNS
    /// settings, so the OS-level routing keeps the old behaviour even
    /// though sing-box reports the new config. Brat-2026-05-04 logs
    /// caught the silent failure on a split → full mode switch:
    /// VpnEngine logged «Forced full restart» but PID stayed the same
    /// because <c>ReloadConfigJson</c> ran <c>TryHotReload</c> first
    /// regardless of caller intent.</para>
    /// </summary>
    public void ReloadConfigJson(string configJson, bool forceRestart = false)
    {
        _logger.Information("[SingBoxManager] Reloading config{Mode}",
            forceRestart ? " (force restart, no hot-reload attempt)" : "");
        _currentConfigPath = WriteJsonToDisk(configJson);

        if (!forceRestart && TryHotReload())
            return;

        if (!forceRestart)
            _logger.Warning("[SingBoxManager] Hot-reload unavailable — restarting sing-box");
        Restart();
    }

    public bool TryReloadConfig(SingBoxConfig config) =>
        TryReloadConfigJson(ConfigGenerator.Serialize(config));

    public bool TryReloadConfigJson(string configJson)
    {
        _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
        _currentConfigPath = WriteJsonToDisk(configJson);
        return TryHotReload();
    }

    public bool IsRunning()
    {
        // v2.21.5: on Unix (macOS + Linux) the Clash API is the authoritative
        // signal. Previously we short-circuited on State != Running, which
        // forced false when:
        //   • The app was restarted and a sing-box from a previous session
        //     is still alive (no process tracked by this VM instance).
        //   • sing-box was started by the Windows Service / external
        //     autostart path and our local _process reference was never
        //     populated.
        //   • Linux pkexec wrapper exited after spawning the root child —
        //     _process.HasExited=true even though sing-box is alive.
        // In all three cases Clash API still answers if the tunnel is up,
        // which is what the UI actually cares about. Drop the State gate
        // on Unix and trust the HTTP probe.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return IsClashApiAlive();

        if (State != SingBoxState.Running) return false;
        return _process?.HasExited == false;
    }

    public bool IsHealthy()
    {
        if (OperatingSystem.IsMacOS())
            return State == SingBoxState.Running && IsClashApiAlive();

        if (_process == null || _process.HasExited)
            return false;

        try
        {
            _process.Refresh();
            var memoryMb = _process.WorkingSet64 / 1024 / 1024;

            if (memoryMb > 500)
                _logger.Warning("[SingBoxManager] sing-box memory usage: {Mem}MB (threshold: 500MB)", memoryMb);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public ProcessMetrics GetMetrics()
    {
        if (_process == null || _process.HasExited)
            return new ProcessMetrics();

        try
        {
            _process.Refresh();
            return new ProcessMetrics
            {
                MemoryMb = _process.WorkingSet64 / 1024 / 1024,
                CpuTime = _process.TotalProcessorTime,
                StartTime = _process.StartTime
            };
        }
        catch
        {
            return new ProcessMetrics();
        }
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    private void RotateSingBoxLog()
    {
        try
        {
            var logPath = AppPaths.SingBoxLogPath;

            if (!File.Exists(logPath))
                return;

            var fileInfo = new FileInfo(logPath);
            if (fileInfo.Length <= MaxLogSizeBytes)
                return;

            var oldPath = Path.ChangeExtension(logPath, ".old.log");
            if (File.Exists(oldPath))
                File.Delete(oldPath);

            File.Move(logPath, oldPath);
            _logger.Information("[SingBoxManager] Rotated singbox.log ({Size:F1} MB → singbox.old.log)",
                fileInfo.Length / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Failed to rotate singbox.log");
        }
    }

    private bool TryHotReload()
    {
        // Pre-check: don't attempt an HTTP call to a dead sing-box. Without
        // this, a crash-recovery path that tries hot-reload first (because
        // a debounced process rescan landed between Crashed and our state
        // update) dumps a 20-line HttpRequestException stack into the log
        // — every single time. Checking HasExited gives us a fast, clean
        // "hot-reload unavailable, restarting" log line instead.
        if (_process == null || _process.HasExited)
        {
            _logger.Debug("[SingBoxManager] Hot-reload skipped — sing-box process not alive");
            return false;
        }

        try
        {
            // v2.31.0-r1 (CO-3 audit fix): the previous sync-over-async pattern
            // (`PutAsync(...).GetAwaiter().GetResult()` on a static HttpClient)
            // is mitigated by HttpClient.Timeout=3s, but on saturated
            // threadpools the awaiter's continuation could land on a starved
            // worker, extending the wait beyond Timeout. Solutions:
            //   1. Explicit CancellationToken with hard 3s deadline → enforces
            //      cancellation at .NET layer, not HttpClient internals.
            //   2. Future: convert to async signature and propagate awaits up
            //      to HealthMonitor.OnDebounceElapsed / AttemptRestart.
            // For now (1) is non-invasive and bounds the worst case explicitly.
            var url = $"http://{_settings.ClashApi}/configs?force=true";
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = _http.PutAsync(url, content, cts.Token).GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                _logger.Information("[SingBoxManager] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    (int)response.StatusCode);
                return true;
            }

            var respBody = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            _logger.Warning("[SingBoxManager] Hot-reload HTTP {Code}: {Body}",
                (int)response.StatusCode, respBody);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[SingBoxManager] Hot-reload timed out after 3s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Linux-only: does the binary at <paramref name="exePath"/> have
    /// <c>cap_net_admin</c> + <c>cap_net_bind_service</c> set via setcap?
    /// If yes, we can launch it as a plain user process instead of through
    /// pkexec. Parses <c>getcap</c> output defensively — any unexpected
    /// format or missing tool returns false (safe fallback to pkexec).
    ///
    /// <para>Expected matching <c>getcap</c> output looks like:
    /// <c>/opt/vpnrouter/sing-box cap_net_admin,cap_net_bind_service=eip</c>
    /// (spacing and flag order can vary between libcap versions).</para>
    /// </summary>
    private bool HasNetCapability(string exePath)
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/getcap", $"\"{exePath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            if (!p.HasExited || p.ExitCode != 0) return false;

            // getcap prints nothing if no capabilities are set.
            // When set, we need BOTH cap_net_admin and cap_net_bind_service
            // active ('e' = effective) so sing-box can actually use them.
            // Be lenient on order / whitespace.
            if (string.IsNullOrWhiteSpace(output)) return false;
            if (output.IndexOf("cap_net_admin", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (output.IndexOf("cap_net_bind_service", StringComparison.OrdinalIgnoreCase) < 0) return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] getcap probe failed — falling back to pkexec path");
            return false;
        }
    }

    private void LaunchProcess(string exePath)
    {
        ProcessStartInfo psi;

        if (OperatingSystem.IsMacOS())
        {
            // sudo with NOPASSWD — sudoers configured by UI on first Connect
            psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/sudo",
                Arguments = $"\"{exePath}\" run -c \"{_currentConfigPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            // v2.28.0: Capability-first launch path (passwordless-by-default).
            //
            // If the sing-box binary has CAP_NET_ADMIN + CAP_NET_BIND_SERVICE
            // set via `setcap` (.deb postinst does this automatically; manual
            // for AppImage / tar.gz), we can spawn it as a plain user process:
            // it'll still be able to create a TUN adapter and bind low
            // ports, but everything else runs at normal user privilege.
            // No pkexec → no password prompt → same UX as Windows Service
            // or macOS after first-run sudoers setup.
            //
            // If the capability is missing (AppImage first run, broken
            // install, or xattr-less filesystem), fall back to the
            // v2.21-era pkexec path, which pops a polkit GUI prompt in
            // desktop sessions. Headless / no-agent systems get a clear
            // exit-127 error and a one-time hint from the UI about running
            // `sudo setcap ...` themselves.
            //
            // See plans/vpnrouter-v2.28-linux-passwordless.md for rationale
            // vs. polkit-policy-based alternative (we picked capabilities
            // because it's strictly least-privilege — sing-box runs as the
            // user, not root — and doesn't require a polkit agent at all).
            if (HasNetCapability(exePath))
            {
                _logger.Information("[SingBoxManager] Linux: launching as user (CAP_NET_ADMIN present, no pkexec needed)");
                _linuxUsedPkexec = false;
                psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"run -c \"{_currentConfigPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                _logger.Information("[SingBoxManager] Linux: falling back to pkexec (sing-box lacks CAP_NET_ADMIN — install via .deb or run 'sudo setcap cap_net_admin,cap_net_bind_service=+eip {Exe}' once)",
                    exePath);
                _linuxUsedPkexec = true;
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pkexec",
                    Arguments = $"\"{exePath}\" run -c \"{_currentConfigPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"run -c \"{_currentConfigPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.Debug("[sing-box] {Line}", e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.Warning("[sing-box] {Line}", e.Data);
        };

        _process.Exited += (_, _) => OnProcessExited();

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        State = SingBoxState.Running;
        _logger.Information("[SingBoxManager] sing-box started (PID {Pid})", _process.Id);
        Started?.Invoke(_process.Id);
    }

    /// <summary>Check if sing-box Clash API responds (macOS: sing-box runs as root child of sudo).</summary>
    private bool IsClashApiAlive()
    {
        try
        {
            using var response = _http.GetAsync($"http://{_settings.ClashApi}/configs").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void OnProcessExited()
    {
        // v2.31.0-r1 (CO-8 audit fix): the previous catch { } empty
        // block swallowed any failure to read ExitCode — but the
        // failure cause (process handle disposed, race with Stop, etc.)
        // never reached the log. Worse, `exitCode == 0` and "couldn't
        // read" both fell into the same null-display branch on the
        // user-visible error path. Now we log the cause so post-mortems
        // can distinguish "exited cleanly" vs "exit info unavailable".
        int? exitCode = null;
        Exception? exitCodeError = null;
        try
        {
            if (_process is { HasExited: true } p)
                exitCode = p.ExitCode;
        }
        catch (Exception ex)
        {
            exitCodeError = ex;
        }

        if (exitCode == 0)
        {
            _logger.Warning("[SingBoxManager] sing-box exited unexpectedly (exit code 0) — will attempt restart");
        }
        else if (exitCode.HasValue)
        {
            _logger.Error("[SingBoxManager] sing-box crashed (exit code: {Code})", exitCode.Value);
        }
        else
        {
            _logger.Error(exitCodeError,
                "[SingBoxManager] sing-box exited but ExitCode could not be read ({ErrType})",
                exitCodeError?.GetType().Name ?? "no exception");
        }

        // v2.31.6-r20 — self-diagnosing crash. Pre-r20 we had to ask the
        // user to copy %ProgramData%\VPNRouter\logs\singbox.log every time
        // a crash happened on their machine, then root-cause from there.
        // Now we read the tail of singbox.log into vpnrouter.log right at
        // the crash boundary so the next log dump the user sends already
        // contains the relevant sing-box context. Best-effort; never throws.
        LogSingBoxCrashTail();

        State = SingBoxState.Failed;
        Crashed?.Invoke(this, EventArgs.Empty);

        // v2.30.1-r5: aggressive cleanup of the orphaned wintun adapter
        // after silent crash. User report 2026-05-01: "у пользователя
        // периодически не убивается сетевой интерфейс и ему приходится
        // перезагружать Windows". When sing-box dies via Windows
        // TerminateProcess (e.g. on wake-from-sleep), it doesn't get
        // a chance to release the wintun handle cleanly. The adapter
        // hangs around in netsh inventory holding the default routes
        // and DNS settings, so the user's network stays "stuck". Disable
        // the adapter explicitly so Windows drops those routes; the
        // adapter will be re-enabled on next sing-box start.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // The interface name is set in ConfigGenerator from
                // settings.Tun.InterfaceName which defaults to
                // "VPNRouter-TUN". Hard-coding the default here keeps
                // the SingBoxManager API surface unchanged (it knows
                // only SingBoxSettings, not AppSettings.Tun); on the
                // off-chance a user customised it, the netsh disable
                // simply returns "not found" and we skip the cleanup
                // — the worst case is the same orphan-adapter problem
                // the user already sees today.
                TunAdapterDiagnostics.DisableOrphanedAdapter(
                    _logger, "VPNRouter-TUN", "SingBoxManager.OnProcessExited");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
            }
        }
    }

    /// <summary>
    /// Read the tail of singbox.log and emit it line-by-line into the
    /// vpnrouter.log so a single log dump contains both engine state and
    /// sing-box's last words before the crash. Best-effort: returns
    /// silently on any I/O error. Tail is bounded to keep vpnrouter.log
    /// readable.
    /// </summary>
    private void LogSingBoxCrashTail()
    {
        try
        {
            var path = AppPaths.SingBoxLogPath;
            if (!File.Exists(path)) return;

            // Open with full sharing in case sing-box (or the OS) hasn't
            // released the write handle yet on a hard kill.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);

            // Bounded ring buffer — last 50 lines is enough to catch the
            // typical sing-box panic + a handful of preceding INFO lines
            // for context, without flooding vpnrouter.log on every crash.
            const int TailLines = 50;
            var buffer = new string[TailLines];
            var count = 0;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                buffer[count % TailLines] = line;
                count++;
            }

            if (count == 0)
            {
                _logger.Warning("[SingBoxManager] singbox.log was empty — no crash context to capture");
                return;
            }

            var keep = Math.Min(count, TailLines);
            var start = count >= TailLines ? count % TailLines : 0;
            _logger.Warning("[SingBoxManager] === sing-box crash tail (last {Keep} of {Total} lines) ===", keep, count);
            for (var i = 0; i < keep; i++)
            {
                var idx = (start + i) % TailLines;
                _logger.Warning("[singbox] {Line}", buffer[idx]);
            }
            _logger.Warning("[SingBoxManager] === end sing-box crash tail ===");
        }
        catch (Exception ex)
        {
            // Diagnostics layer must never break crash handling itself.
            _logger.Debug(ex, "[SingBoxManager] Failed to capture sing-box crash tail");
        }
    }

    private static string WriteJsonToDisk(string json)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);

        var path = AppPaths.CurrentConfigPath;
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _process?.Dispose();
    }

    /// <summary>
    /// v2.29.0-r5 — Linux stop escalation chain. Tries kill methods from
    /// least-privileged to most-privileged, verifying after each step that
    /// sing-box is actually gone. Mac uses a separate sudo path; this method
    /// is Linux-only.
    ///
    /// <para>Steps:</para>
    /// <list type="number">
    /// <item>Plain user pkill (works in capability mode + .deb installs
    ///   where sing-box runs as user via setcap CAP_NET_ADMIN).</item>
    /// <item>pkexec pkill -KILL (polkit GUI prompt; SIGKILL bypasses any
    ///   signal mask sing-box might have set).</item>
    /// <item>sudo pkill -KILL (NOPASSWD if sudoers entry was set up at
    ///   first Connect; falls through if not configured).</item>
    /// </list>
    ///
    /// <para>Each attempt is followed by IsSingBoxAlive() check (Clash API
    /// probe + pgrep) so we know immediately if it worked. Logs each step
    /// for postmortem.</para>
    /// </summary>
    private void LinuxStopEscalationChain()
    {
        // Step 1: plain user pkill. Cheap and works in capability mode.
        if (TrySpawnAndWait("/usr/bin/pkill", "-TERM -f sing-box", 3000, "user pkill -TERM"))
        {
            // Wait briefly for graceful exit (sing-box on SIGTERM should
            // tear down TUN cleanly within ~1 s).
            System.Threading.Thread.Sleep(800);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: user pkill -TERM succeeded");
                return;
            }
        }

        _logger.Information("[SingBoxManager] Linux stop: user pkill didn't kill sing-box, escalating to pkexec");

        // Step 2: pkexec with SIGKILL. GUI prompt — user might dismiss.
        if (TrySpawnAndWait("/usr/bin/pkexec", "pkill -KILL -f sing-box", 30000, "pkexec pkill -KILL"))
        {
            System.Threading.Thread.Sleep(500);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: pkexec pkill -KILL succeeded");
                return;
            }
        }

        _logger.Warning("[SingBoxManager] Linux stop: pkexec didn't kill sing-box, trying sudo");

        // Step 3: sudo with -n (non-interactive — fail if password needed
        // rather than block forever). If user set up NOPASSWD sudoers, this
        // works without prompt; otherwise it fails fast and we give up
        // (better to surface the failure than hang forever).
        if (TrySpawnAndWait("/usr/bin/sudo", "-n pkill -KILL -f sing-box", 5000, "sudo -n pkill -KILL"))
        {
            System.Threading.Thread.Sleep(500);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: sudo -n pkill -KILL succeeded");
                return;
            }
        }

        if (IsSingBoxAlive())
        {
            _logger.Error("[SingBoxManager] Linux stop: ALL escalation steps failed — sing-box still alive. " +
                          "Manual intervention required: `sudo pkill -KILL -f sing-box`. " +
                          "Possible causes: pkexec/polkit agent not installed; sudoers NOPASSWD not set up; " +
                          "sing-box running under a different uid we can't kill.");
        }
    }

    /// <summary>v2.29.0-r5: spawn an external process, wait, return true
    /// iff exit code 0. Used by Linux stop escalation chain. Errors logged
    /// but never thrown.</summary>
    private bool TrySpawnAndWait(string fileName, string args, int timeoutMs, string label)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} — Process.Start returned null", label);
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} timed out after {Ms} ms", label, timeoutMs);
                try { p.Kill(true); } catch { }
                return false;
            }
            _logger.Information("[SingBoxManager] Linux stop: {Label} exit={Code}", label, p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Linux stop: {Label} threw", label);
            return false;
        }
    }

    /// <summary>v2.29.0-r5: check if sing-box is still running.
    /// Two-signal test: Clash API at 127.0.0.1:9090 + pgrep -f sing-box.
    /// Returns true if EITHER signal says alive (defensive — false
    /// negative on Clash API alone could leave a zombie).</summary>
    private bool IsSingBoxAlive()
    {
        if (IsClashApiAlive()) return true;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pgrep", "-f sing-box")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return false; }
            // pgrep exit 0 = found at least one process; 1 = none.
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
