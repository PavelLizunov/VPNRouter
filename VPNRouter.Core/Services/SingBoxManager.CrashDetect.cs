using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    internal sealed class ChildExit(IProcessHandle child) : EventArgs
    {
        internal IProcessHandle Child { get; } = child;
    }

    private void OnProcessExited(IProcessHandle child, int exitCode)
    {
        // An OS callback must be able to return while another thread is disposing this handle.
        if (!Monitor.TryEnter(_lifecycleGate))
        {
            _ = Task.Run(() =>
            {
                bool crashed;
                lock (_lifecycleGate) crashed = RecordExit(child, exitCode);
                if (crashed) Crashed?.Invoke(this, new ChildExit(child));
            });
            return;
        }

        bool notify;
        try { notify = RecordExit(child, exitCode); }
        finally { Monitor.Exit(_lifecycleGate); }
        if (notify) Crashed?.Invoke(this, new ChildExit(child));
    }

    private bool RecordExit(IProcessHandle child, int exitCode)
    {
        if (!ReferenceEquals(child, _handle) || Volatile.Read(ref _disposed) != 0) return false;
        // An exit during an intentional Stop or Restart is expected, not a crash.
        if ((_restartInProgress || _stopInProgress) && (exitCode == -1 || exitCode == 137 || exitCode == 143))
        {
            _logger.Information(
                "[SingBoxManager] Expected exit during intentional {Phase:l} (exit code: {Code}) — suppressing Crashed event, late OS callback after SuppressExitedEvent",
                _restartInProgress ? "restart" : "stop",
                exitCode);
            DetectTunOrphanCrashSignature();
            return false;
        }

        if (exitCode == 0)
        {
            _logger.Warning("[SingBoxManager] sing-box exited unexpectedly (exit code 0) — will attempt restart");
        }
        else
        {
            _logger.Error("[SingBoxManager] sing-box crashed (exit code: {Code})", exitCode);
        }

        LogSingBoxCrashTail();

        DetectTunOrphanCrashSignature();

        State = SingBoxState.Failed;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                QueueTunAdapterRemoval("SingBoxManager.OnProcessExited.async");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
            }
        }
        return true;
    }

    private void LogSingBoxCrashTail()
    {
        try
        {
            var path = AppPaths.SingBoxLogPath;
            if (!File.Exists(path)) return;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);

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
            _logger.Debug(ex, "[SingBoxManager] Failed to capture sing-box crash tail");
        }
    }

    private void DetectTunOrphanCrashSignature()
    {
        try
        {
            string[] snapshot;
            int count;
            lock (_capturedStderrLock)
            {
                snapshot = (string[])_capturedStderr.Clone();
                count = _capturedStderrCount;
            }

            if (count == 0)
            {
                LastCrashWasTunOrphan = false;
                LastCrashWasLinuxTunPermissionFailure = false;
                return;
            }

            LastCrashWasTunOrphan = false;
            LastCrashWasLinuxTunPermissionFailure = false;

            var keep = Math.Min(count, StderrBufferSize);
            for (var i = 0; i < keep; i++)
            {
                var line = snapshot[i];
                if (string.IsNullOrEmpty(line)) continue;

                if (OperatingSystem.IsLinux() && IsLinuxTunPermissionFailure(line))
                {
                    LastCrashWasLinuxTunPermissionFailure = true;
                    _logger.Error(
                        "[SingBoxManager] Linux denied TUNSETIFF. Automatic restart is disabled until VPNRouter is launched outside the restricting sandbox or receives host TUN privileges.");
                    return;
                }

                if (OperatingSystem.IsWindows() &&
                    (line.IndexOf("Cannot create a file when that file already exists",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("configure tun interface:",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("open interface take too much time to finish",
                        StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    LastCrashWasTunOrphan = true;
                    _logger.Warning(
                        "[SingBoxManager] Detected TUN-orphan crash signature in stderr — " +
                        "HealthMonitor will fire netsh disable on VPNRouter-TUN before restart.");
                    return;
                }
            }

        }
        catch (Exception ex)
        {
            _logger.Debug(ex,
                "[SingBoxManager] DetectTunOrphanCrashSignature scan threw (non-fatal)");
            LastCrashWasTunOrphan = false;
            LastCrashWasLinuxTunPermissionFailure = false;
        }
    }

    internal static bool IsLinuxTunPermissionFailure(string? line) =>
        !string.IsNullOrEmpty(line) &&
        line.Contains("TUNSETIFF", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);

}
