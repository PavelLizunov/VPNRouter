using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.Core.Services;

public class ZapretManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private bool _disposed;

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public bool IsRunning => _handle != null && !_handle.HasExited;
    public int? Pid => IsRunning ? _handle?.Pid : null;

    public static bool IsWinwsRunning() => ProcessQuery.AnyAlive("winws");

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

    public event Action? ImmediateExitDetected;

    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(2);

    public ZapretManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

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
            $"\"%BIN%winws.exe\" {parsedArgs}\r\n";
        File.WriteAllText(wrapperPath, wrapper);

        _logger.Information("[Zapret] Launching silent wrapper: {Path}", wrapperPath);

        _handle = StartCmdBat(wrapperPath, zapretDir);

        var startedAt = DateTime.UtcNow;
        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            var runtime = DateTime.UtcNow - startedAt;
            _logger.Warning("[Zapret] Wrapper exited (exit code: {Code})", code);
            DetectImmediateExit(runtime, code);
        };

        _logger.Information("[Zapret] Silent wrapper started (PID {Pid})", startedHandle.Pid);
    }

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

        var batPath = Path.Combine(binDir, "_vpnrouter_launch.bat");
        var batContent = BuildCygwinLaunchBat(binDir, ZapretUpdater.ListsDir, args);
        File.WriteAllText(batPath, batContent);

        _handle = StartCmdBat(batPath, workingDir: null);

        var startedAt = DateTime.UtcNow;
        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            var runtime = DateTime.UtcNow - startedAt;
            _logger.Warning("[Zapret] Process exited (exit code: {Code})", code);
            DetectImmediateExit(runtime, code);
        };

        _logger.Information("[Zapret] Started (PID {Pid})", startedHandle.Pid);
    }

    private IProcessHandle StartCmdBat(string batPath, string? workingDir)
    {
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new List<string> { "/c", batPath },
            WorkingDirectory: workingDir,
            CaptureStdout: false,
            CaptureStderr: false);

        return _runner.Start(request);
    }

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

    internal static string BuildCygwinLaunchBat(string binDir, string listsDir, string args)
    {
        if (args.Any(c => c is '\r' or '\n' or '&' or '|' or '^' or '<' or '>' or '%'))
            throw new ArgumentException("Zapret arguments contain disallowed shell metacharacters", nameof(args));

        return "@echo off\r\n" +
            $"set \"BIN={binDir}{Path.DirectorySeparatorChar}\"\r\n" +
            $"set \"LISTS={listsDir}{Path.DirectorySeparatorChar}\"\r\n" +
            "cd /d \"%BIN%\"\r\n" +
            $"\"%BIN%winws.exe\" {args}\r\n";
    }

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
        if (_handle == null || _handle.HasExited)
        {
            _handle?.Dispose();
            _handle = null;
            return;
        }

        var handle = _handle;
        _logger.Information("[Zapret] Stopping (PID {Pid})", handle.Pid);

        try
        {
            handle.SuppressExitedEvent();
            handle.Kill(entireProcessTree: true);

            using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
            try
            {
                handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("[Zapret] WaitForExitAsync timeout (3s) — proceeding to dispose");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Zapret] Error stopping");
        }
        finally
        {
            try { handle.Dispose(); } catch {  }
            _handle = null;
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
