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
            var elevator = OperatingSystem.IsLinux() ? "/usr/bin/pkexec" : "/usr/bin/sudo";
            try
            {
                if (_process != null) _process.EnableRaisingEvents = false;

                var psi = new ProcessStartInfo(elevator, "pkill -f sing-box")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var killProc = Process.Start(psi);
                killProc?.WaitForExit(5000);
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
            State = SingBoxState.Stopped;
            if (releaseLock) _tunLock.Release();
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

    public void ReloadConfig(SingBoxConfig config) =>
        ReloadConfigJson(ConfigGenerator.Serialize(config));

    public void ReloadConfigJson(string configJson)
    {
        _logger.Information("[SingBoxManager] Reloading config");
        _currentConfigPath = WriteJsonToDisk(configJson);

        if (TryHotReload())
            return;

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
            var url = $"http://{_settings.ClashApi}/configs?force=true";
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = _http.PutAsync(url, content).GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                _logger.Information("[SingBoxManager] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    (int)response.StatusCode);
                return true;
            }

            var respBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _logger.Warning("[SingBoxManager] Hot-reload HTTP {Code}: {Body}",
                (int)response.StatusCode, respBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] Hot-reload unavailable ({Msg})", ex.Message);
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
            // v2.21.0: Linux elevation via pkexec (PolicyKit).
            // On standard desktop environments (GNOME / KDE / XFCE /
            // Cinnamon) a polkit authentication agent runs in the session,
            // so pkexec pops a native GUI password prompt and launches
            // sing-box as root. Same UX model as macOS sudo, no terminal
            // required.
            //
            // Fallback for headless or minimal distros (no polkit agent):
            // user can `sudo setcap cap_net_admin,cap_net_bind_service=+eip
            // <path/to/sing-box>` once, after which pkexec becomes a no-op
            // — plain exec works without root.
            // See plans/vpnrouter-linux-port-research.md.
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
        int? exitCode = null;
        try
        {
            if (_process is { HasExited: true } p)
                exitCode = p.ExitCode;
        }
        catch { }

        if (exitCode == 0)
            _logger.Warning("[SingBoxManager] sing-box exited unexpectedly (exit code 0) — will attempt restart");
        else
            _logger.Error("[SingBoxManager] sing-box crashed (exit code: {Code})",
                exitCode?.ToString() ?? "unknown");

        State = SingBoxState.Failed;
        Crashed?.Invoke(this, EventArgs.Empty);
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
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
