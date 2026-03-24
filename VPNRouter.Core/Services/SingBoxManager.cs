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

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public SingBoxState State { get; private set; } = SingBoxState.Stopped;
    public int? Pid => _process?.HasExited == false ? _process.Id : null;
    public event EventHandler? Crashed;

    public SingBoxManager(SingBoxSettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger ?? Log.Logger;
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

        var exePath = Environment.ExpandEnvironmentVariables(_settings.ExecutablePath);

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"sing-box not found at: {exePath}");

        RotateSingBoxLog();
        _currentConfigPath = WriteJsonToDisk(configJson);

        _logger.Information("[SingBoxManager] Starting sing-box with config: {Config}", _currentConfigPath);

        State = SingBoxState.Starting;
        LaunchProcess(exePath);
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited)
        {
            State = SingBoxState.Stopped;
            return;
        }

        _logger.Information("[SingBoxManager] Stopping sing-box (PID {Pid})", Pid);

        // Disable events BEFORE Kill — this prevents the Exited callback from
        // firing and falsely reporting a crash. Simple and race-free.
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
            _logger.Information("[SingBoxManager] sing-box stopped");
        }
    }

    public void Restart()
    {
        _logger.Information("[SingBoxManager] Restarting sing-box");
        State = SingBoxState.Restarting;
        Stop();

        var exePath = Environment.ExpandEnvironmentVariables(_settings.ExecutablePath);
        LaunchProcess(exePath);
    }

    /// <summary>
    /// Hot-reload: sends new config to sing-box via Clash API PUT /configs.
    /// No process restart — TUN interface stays up, connections are not dropped.
    /// Falls back to kill+restart if hot-reload fails.
    /// </summary>
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

    /// <summary>
    /// Attempts hot-reload only (no fallback to full restart).
    /// Returns true if hot-reload succeeded, false otherwise.
    /// Use this for debounce-triggered reloads to avoid restart storms.
    /// </summary>
    public bool TryReloadConfig(SingBoxConfig config) =>
        TryReloadConfigJson(ConfigGenerator.Serialize(config));

    public bool TryReloadConfigJson(string configJson)
    {
        _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
        _currentConfigPath = WriteJsonToDisk(configJson);
        return TryHotReload();
    }

    public bool IsRunning() => State == SingBoxState.Running && _process?.HasExited == false;

    public bool IsHealthy()
    {
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

    /// <summary>
    /// Rotates singbox.log if it exceeds MaxLogSizeBytes.
    /// Renames current log to singbox.old.log (overwriting previous backup).
    /// </summary>
    private void RotateSingBoxLog()
    {
        try
        {
            var logPath = Environment.ExpandEnvironmentVariables(
                @"%ProgramData%\VPNRouter\logs\singbox.log");

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

    /// <summary>
    /// Attempts hot-reload via Clash API: PUT http://{clash_api}/configs?force=true
    /// Returns true on HTTP 2xx, false on any error.
    /// </summary>
    private bool TryHotReload()
    {
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
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"run -c \"{_currentConfigPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

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
    }

    /// <summary>
    /// Only fires for genuine crashes — Stop() disables EnableRaisingEvents
    /// before Kill(), so intentional stops never reach here.
    /// Exit code 0 = sing-box exited cleanly (e.g. network not ready at boot).
    /// Non-zero = actual crash. Both trigger restart via Crashed event.
    /// </summary>
    private void OnProcessExited()
    {
        int? exitCode = null;
        try
        {
            if (_process is { HasExited: true } p)
                exitCode = p.ExitCode;
        }
        catch { /* process disposed or access denied */ }

        if (exitCode == 0)
        {
            _logger.Warning("[SingBoxManager] sing-box exited unexpectedly (exit code 0) — will attempt restart");
        }
        else
        {
            _logger.Error("[SingBoxManager] sing-box crashed (exit code: {Code})",
                exitCode?.ToString() ?? "unknown");
        }

        State = SingBoxState.Failed;
        Crashed?.Invoke(this, EventArgs.Empty);
    }

    private static string WriteJsonToDisk(string json)
    {
        var dir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "current.json");
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
