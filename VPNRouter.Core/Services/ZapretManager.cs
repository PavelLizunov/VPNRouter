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

    public ZapretManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// <summary>
    /// Start Flowseal strategy via its original .bat file. This runs the full
    /// prologue (service.bat load_user_lists, check_updates, etc.) before
    /// launching winws.exe with all correct env vars populated.
    /// </summary>
    public void StartFromBat(string batPath)
    {
        if (!File.Exists(batPath))
            throw new FileNotFoundException($"Strategy .bat not found: {batPath}");

        if (IsRunning)
        {
            _logger.Warning("[Zapret] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        _logger.Information("[Zapret] Launching .bat: {Path}", batPath);

        // Run the original Flowseal .bat with hidden window.
        // The .bat itself does `start /min winws.exe ...` which spawns winws
        // as a detached process, then the .bat exits. We then track winws
        // by process name via IsAnyRunning (below).
        var psi = new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(batPath)
        };

        _process = Process.Start(psi);
        if (_process == null)
            throw new InvalidOperationException("Failed to launch strategy .bat");

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Debug("[Zapret] Bat wrapper exited (exit code: {Code}). winws.exe runs separately.",
                _process?.ExitCode);
        };

        _logger.Information("[Zapret] .bat launched (wrapper PID {Pid}). winws.exe starts separately.", _process.Id);
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

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Warning("[Zapret] Process exited (exit code: {Code})", _process?.ExitCode);
        };

        _logger.Information("[Zapret] Started (PID {Pid})", _process.Id);
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
