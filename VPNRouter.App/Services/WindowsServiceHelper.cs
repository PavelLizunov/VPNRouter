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
