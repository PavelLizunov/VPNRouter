#if PLATFORM_WINDOWS
using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.App.Services;

/// <summary>
/// Windows Service helper using only sc.exe (no System.ServiceProcess dependency,
/// works on net8.0 without the -windows TFM suffix).
///
/// All methods require admin rights — Program.cs auto-elevates.
/// </summary>
public static class WindowsServiceHelper
{
    public const string ServiceName = "VPNRouter";
    public const string DisplayName = "VPN Process Router";
    public const string Description = "Routes selected application traffic through VPN using sing-box TUN mode.";

    public record ServiceResult(bool Success, string Message);

    // ─── Status ───────────────────────────────────────────────────────────────

    public static bool IsInstalled()
    {
        var (code, _) = RunSc($"query {ServiceName}");
        // sc query returns 0 if service exists, 1060 (ERROR_SERVICE_DOES_NOT_EXIST) if not
        return code == 0;
    }

    public static bool IsRunning()
    {
        var (code, output) = RunSc($"query {ServiceName}");
        if (code != 0) return false;
        // Parse "STATE : 4 RUNNING" or "STATE : 1 STOPPED"
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Install ──────────────────────────────────────────────────────────────

    public static ServiceResult Install(string? exePath = null)
    {
        exePath ??= ResolveServiceExePath();
        if (exePath == null || !File.Exists(exePath))
            return new ServiceResult(false, $"Service executable not found: {exePath}");

        if (IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is already installed.");

        var args = $"create {ServiceName} " +
                   $"binPath= \"{exePath} --service\" " +
                   $"start= auto " +
                   $"obj= LocalSystem " +
                   $"DisplayName= \"{DisplayName}\"";

        var (code, output) = RunSc(args);
        if (code != 0)
            return new ServiceResult(false, $"sc create failed (exit {code}): {output}");

        // Set description + failure recovery
        RunSc($"description {ServiceName} \"{Description}\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");

        return new ServiceResult(true, $"Service installed at: {exePath}");
    }

    public static ServiceResult Uninstall()
    {
        if (!IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is not installed.");

        if (IsRunning())
        {
            var stopResult = Stop();
            if (!stopResult.Success)
                return new ServiceResult(false, $"Cannot stop before uninstall: {stopResult.Message}");
        }

        var (code, output) = RunSc($"delete {ServiceName}");
        return code == 0
            ? new ServiceResult(true, $"Service '{ServiceName}' uninstalled.")
            : new ServiceResult(false, $"sc delete failed (exit {code}): {output}");
    }

    // ─── Start / Stop ─────────────────────────────────────────────────────────

    public static ServiceResult Start()
    {
        if (!IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is not installed.");
        if (IsRunning())
            return new ServiceResult(true, $"Service '{ServiceName}' is already running.");

        var (code, output) = RunSc($"start {ServiceName}");
        if (code != 0)
            return new ServiceResult(false, $"sc start failed (exit {code}): {output}");

        // Poll for up to 10s waiting for RUNNING state
        for (int i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(500);
            if (IsRunning())
                return new ServiceResult(true, $"Service '{ServiceName}' started.");
        }
        return new ServiceResult(false, "Service did not reach RUNNING state within 10 seconds.");
    }

    public static ServiceResult Stop()
    {
        if (!IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is not installed.");
        if (!IsRunning())
            return new ServiceResult(true, $"Service '{ServiceName}' is already stopped.");

        var (code, output) = RunSc($"stop {ServiceName}");
        if (code != 0)
            return new ServiceResult(false, $"sc stop failed (exit {code}): {output}");

        // Poll up to 15s for STOPPED
        for (int i = 0; i < 30; i++)
        {
            System.Threading.Thread.Sleep(500);
            if (!IsRunning())
                return new ServiceResult(true, $"Service '{ServiceName}' stopped.");
        }
        return new ServiceResult(false, "Service did not stop within 15 seconds.");
    }

    // ─── Config query + self-heal ─────────────────────────────────────────────

    /// <summary>
    /// Query the currently-configured binary path from `sc qc VPNRouter`.
    /// Returns null if the service isn't installed or the line couldn't be
    /// parsed. The returned string includes arguments (e.g. `... --service`)
    /// exactly as sc reported them, unwrapped from outer quotes.
    /// </summary>
    public static string? GetBinPath()
    {
        var (code, output) = RunSc($"qc {ServiceName}");
        if (code != 0) return null;

        // sc qc emits a line like:
        //     BINARY_PATH_NAME   : "C:\...\VPNRouter.Service.exe --service"
        // Find that line, strip the label+colon, then trim whitespace and
        // surrounding quotes.
        foreach (var line in output.Split('\n'))
        {
            var label = "BINARY_PATH_NAME";
            var idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var colon = line.IndexOf(':', idx);
            if (colon < 0) continue;

            var value = line[(colon + 1)..].Trim().Trim('"').Trim();
            return value;
        }
        return null;
    }

    /// <summary>
    /// v2.26.0 — service binPath self-heal. Same problem as the Run-key
    /// ghost-path bug: if the user re-installs / moves the app, the
    /// service's registered binPath still points to the previous location
    /// and the service either starts the old binary (if it still exists)
    /// or fails silently at boot.
    ///
    /// Called from Program.Main() on every Windows app startup. If the
    /// service is installed AND the installed binPath doesn't match the
    /// currently-discovered VPNRouter.Service.exe, run
    /// `sc config VPNRouter binPath= "<new path> --service"`. The change
    /// takes effect on the next service start — we don't auto-stop/start
    /// here because:
    ///   • doing so would interrupt any in-flight VPN session;
    ///   • the common case is "user reinstalled while service was already
    ///     installed with old path" and the user will reboot/login soon
    ///     anyway.
    ///
    /// Idempotent — no-op when binPath already matches or the service
    /// isn't installed.
    /// </summary>
    public static ServiceResult EnsureCurrentBinPath(string? currentServiceExePath = null)
    {
        if (!IsInstalled())
            return new ServiceResult(true, "Service not installed; nothing to heal.");

        currentServiceExePath ??= ResolveServiceExePath();
        if (currentServiceExePath == null)
            return new ServiceResult(false, "VPNRouter.Service.exe not found near current app — skipping binPath heal.");

        var installed = GetBinPath();
        if (installed == null)
            return new ServiceResult(false, "Couldn't parse installed binPath from sc qc.");

        var expected = $"{currentServiceExePath} --service";

        // sc typically stores the path quoted but returns it unquoted after
        // our Trim('"'). Compare case-insensitively so we don't churn on
        // drive-letter casing differences (C:\ vs c:\).
        if (string.Equals(installed, expected, StringComparison.OrdinalIgnoreCase))
            return new ServiceResult(true, "binPath already correct, no-op.");

        var (code, output) = RunSc($"config {ServiceName} binPath= \"{expected}\"");
        if (code != 0)
            return new ServiceResult(false, $"sc config binPath= failed (exit {code}): {output}");

        return new ServiceResult(true, $"binPath updated: \"{installed}\" → \"{expected}\" (effective next service start).");
    }

    // ─── Path resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Looks for VPNRouter.Service.exe in standard locations relative to current exe.
    /// </summary>
    public static string? ResolveServiceExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // Try same directory as current exe (typical install layout)
        var sameDir = Path.Combine(baseDir, "VPNRouter.Service.exe");
        if (File.Exists(sameDir)) return sameDir;

        // Try service/ subfolder
        var subDir = Path.Combine(baseDir, "service", "VPNRouter.Service.exe");
        if (File.Exists(subDir)) return subDir;

        return null;
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    private static (int ExitCode, string Output) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "Failed to start sc.exe");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            return (proc.ExitCode, (stdout + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
#endif
