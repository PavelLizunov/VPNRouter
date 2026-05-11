using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages zapret (winws.exe) process lifecycle for DPI bypass.
/// Accepts pre-built argument strings (from ZapretUpdater.ParseStrategies or custom).
/// Windows-only — uses WinDivert driver.
/// </summary>
public class ZapretManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    /// <summary>Check if winws.exe is running globally (handles .bat wrapper case).</summary>
    public static bool IsWinwsRunning() => Process.GetProcessesByName("winws").Length > 0;

    /// <summary>PID of running winws.exe process (for status display).</summary>
    public static int? WinwsPid
    {
        get
        {
            var procs = Process.GetProcessesByName("winws");
            if (procs.Length == 0) return null;
            try { return procs[0].Id; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    public event Action<string>? OutputReceived;

    /// <summary>
    /// Bug-r9-G (2026-05-11) — fired when winws.exe exits within
    /// <see cref="ImmediateExitWindow"/> with a non-zero code, which is
    /// almost always AV (Windows Defender or third-party) terminating
    /// it as suspicious. Stas's log showed
    /// <c>[Zapret] Wrapper exited (exit code: -1)</c> within milliseconds
    /// of launch with no other reason to fail. App's MainWindowViewModel
    /// subscribes and shows a toast with the AV whitelist path.
    /// </summary>
    public event Action? ImmediateExitDetected;

    /// <summary>
    /// Window during which an exit is classified as "immediate" and
    /// likely AV-induced. Healthy winws.exe runs indefinitely; even a
    /// strategy-misconfig exit takes ≥ 500 ms to log + terminate.
    /// 2 s is a conservative threshold that won't false-positive on
    /// slow systems while still capturing the sub-100 ms AV kill path.
    /// </summary>
    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(2);

    public ZapretManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// <summary>
    /// Start Flowseal strategy silently by generating a wrapper .bat that
    /// sources the original's prologue (service.bat calls) and runs winws.exe
    /// directly (no `start` cmd) so it inherits hidden parent window.
    /// Takes parsed args from ZapretUpdater.ParseStrategies.
    /// </summary>
    public void StartFromBat(string batPath, string parsedArgs)
    {
        if (!File.Exists(batPath))
            throw new FileNotFoundException($"Strategy .bat not found: {batPath}");

        if (IsRunning)
        {
            _logger.Warning("[Zapret] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        var zapretDir = Path.GetDirectoryName(batPath)!;
        var binDir = Path.Combine(zapretDir, "bin");
        var listsDir = Path.Combine(zapretDir, "lists");

        // Generate silent wrapper .bat: run prologue + winws.exe directly (no `start`)
        var wrapperPath = Path.Combine(zapretDir, "_vpnrouter_silent.bat");
        var wrapper = "@echo off\r\n" +
            "chcp 65001 > nul\r\n" +
            $"cd /d \"{zapretDir}\"\r\n" +
            "call service.bat status_zapret >nul 2>&1\r\n" +
            "call service.bat check_updates >nul 2>&1\r\n" +
            "call service.bat load_game_filter >nul 2>&1\r\n" +
            "call service.bat load_user_lists >nul 2>&1\r\n" +
            $"set \"BIN={binDir}{Path.DirectorySeparatorChar}\"\r\n" +
            $"set \"LISTS={listsDir}{Path.DirectorySeparatorChar}\"\r\n" +
            "cd /d \"%BIN%\"\r\n" +
            // No `start` — winws runs as child of hidden cmd, no separate window
            $"winws.exe {parsedArgs}\r\n";
        File.WriteAllText(wrapperPath, wrapper);

        _logger.Information("[Zapret] Launching silent wrapper: {Path}", wrapperPath);

        var psi = new ProcessStartInfo
        {
            FileName = wrapperPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = zapretDir
        };

        _process = Process.Start(psi);
        if (_process == null)
            throw new InvalidOperationException("Failed to launch silent wrapper");

        var startedAt = DateTime.UtcNow;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var runtime = DateTime.UtcNow - startedAt;
            var code = _process?.ExitCode;
            _logger.Warning("[Zapret] Wrapper exited (exit code: {Code})", code);
            DetectImmediateExit(runtime, code);
        };

        _logger.Information("[Zapret] Silent wrapper started (PID {Pid})", _process.Id);
    }

    /// <summary>
    /// Start winws.exe with pre-built argument string.
    /// Arguments come from ZapretUpdater.ParseStrategies() or custom user input.
    /// </summary>
    public void Start(string args)
    {
        if (IsRunning)
        {
            _logger.Warning("[Zapret] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        var binDir = ZapretUpdater.BinDir;
        var exePath = ZapretUpdater.WinwsExePath;

        if (!File.Exists(exePath))
        {
            _logger.Error("[Zapret] winws.exe not found at {Path}", exePath);
            throw new FileNotFoundException($"winws.exe not found. Download zapret first.");
        }

        _logger.Information("[Zapret] WorkingDir: {Dir}", binDir);
        _logger.Information("[Zapret] Args: {Args}", args);

        // Write a temporary .bat file and launch it — exactly like Flowseal.
        // Cygwin winws.exe REQUIRES:
        // 1. A real console (not pipe-redirected stdout)
        // 2. SET variables for paths (CMD variable expansion handles quoting
        //    correctly for Cygwin, direct literal paths fail with "cannot access")
        var batPath = Path.Combine(binDir, "_vpnrouter_launch.bat");
        var batContent = "@echo off\r\n" +
            $"set \"BIN={binDir}{Path.DirectorySeparatorChar}\"\r\n" +
            $"set \"LISTS={ZapretUpdater.ListsDir}{Path.DirectorySeparatorChar}\"\r\n" +
            $"cd /d \"%BIN%\"\r\n" +
            $"winws.exe {args}\r\n";
        File.WriteAllText(batPath, batContent);

        var psi = new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[Zapret] Failed to start process");
            throw new InvalidOperationException("Failed to start winws.exe");
        }

        var startedAt = DateTime.UtcNow;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var runtime = DateTime.UtcNow - startedAt;
            var code = _process?.ExitCode;
            _logger.Warning("[Zapret] Process exited (exit code: {Code})", code);
            DetectImmediateExit(runtime, code);
        };

        _logger.Information("[Zapret] Started (PID {Pid})", _process.Id);
    }

    /// <summary>
    /// Bug-r9-G — classify an Exited callback as "immediate, non-zero,
    /// likely AV-induced" and fire <see cref="ImmediateExitDetected"/>.
    /// Pulled out of both Start paths so the rule lives in one place.
    /// Exits with code 0 are normal stops (the .bat wrapper finishes
    /// after winws.exe is launched) — don't surface a hint for those.
    /// </summary>
    private void DetectImmediateExit(TimeSpan runtime, int? exitCode)
    {
        if (runtime >= ImmediateExitWindow) return;
        if (exitCode == 0) return;

        _logger.Warning(
            "[Zapret] Immediate exit detected (code={Code}, runtime={Ms}ms) — surfaced AV whitelist hint",
            exitCode, (int)runtime.TotalMilliseconds);
        try { ImmediateExitDetected?.Invoke(); }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Zapret] ImmediateExitDetected handler threw");
        }
    }

    /// <summary>Build arguments for legacy built-in strategies (no Flowseal needed).</summary>
    public static string BuildLegacyArgs(string strategy, int targetPort = 443)
    {
        return strategy switch
        {
            "multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",

            "fake+multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=fake,multisplit --dpi-desync-ttl=2 " +
                $"--dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 " +
                $"--dpi-desync-fake-tls=0x00000000000000000000",

            _ => throw new ArgumentException($"Unknown legacy strategy: {strategy}")
        };
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited)
        {
            _process = null;
            return;
        }

        _logger.Information("[Zapret] Stopping (PID {Pid})", _process.Id);

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Zapret] Error stopping");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _logger.Information("[Zapret] Stopped");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
