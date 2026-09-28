#if PLATFORM_WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

public static class WindowsServiceHelper
{
    public const string ServiceName = "VPNRouter";
    public const string DisplayName = "VPN Process Router";
    public const string Description = "Routes selected application traffic through VPN using sing-box TUN mode.";

    public record ServiceResult(bool Success, string Message);

    public static bool IsInstalled()
    {
        var (code, _) = RunSc("query", ServiceName);
        return code == 0;
    }

    public static bool IsRunning()
    {
        var (code, output) = RunSc("query", ServiceName);
        if (code != 0) return false;
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static ServiceResult Install(string? exePath = null)
    {
        exePath ??= ResolveServiceExePath();
        if (exePath == null || !File.Exists(exePath))
            return new ServiceResult(false, $"Service executable not found: {exePath}");

        if (IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is already installed.");

        var (code, output) = RunSc(
            WindowsServiceCommand.BuildCreateArguments(
                ServiceName, exePath, DisplayName));
        if (code != 0)
            return new ServiceResult(false, $"sc create failed (exit {code}): {output}");

        RunSc("description", ServiceName, Description);
        RunSc(WindowsServiceCommand.BuildFailureRecoveryArguments(ServiceName));

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

        var (code, output) = RunSc("delete", ServiceName);
        return code == 0
            ? new ServiceResult(true, $"Service '{ServiceName}' uninstalled.")
            : new ServiceResult(false, $"sc delete failed (exit {code}): {output}");
    }

    public static ServiceResult Start()
    {
        if (!IsInstalled())
            return new ServiceResult(false, $"Service '{ServiceName}' is not installed.");
        if (IsRunning())
            return new ServiceResult(true, $"Service '{ServiceName}' is already running.");

        var (code, output) = RunSc("start", ServiceName);
        if (code != 0)
            return new ServiceResult(false, $"sc start failed (exit {code}): {output}");

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

        var (code, output) = RunSc("stop", ServiceName);
        if (code != 0)
            return new ServiceResult(false, $"sc stop failed (exit {code}): {output}");

        for (int i = 0; i < 30; i++)
        {
            System.Threading.Thread.Sleep(500);
            if (!IsRunning())
                return new ServiceResult(true, $"Service '{ServiceName}' stopped.");
        }
        return new ServiceResult(false, "Service did not stop within 15 seconds.");
    }

    public static string? GetBinPath()
    {
        var (code, output) = RunSc("qc", ServiceName);
        if (code != 0) return null;

        foreach (var line in output.Split('\n'))
        {
            var label = "BINARY_PATH_NAME";
            var idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var colon = line.IndexOf(':', idx);
            if (colon < 0) continue;

            return line[(colon + 1)..].Trim();
        }
        return null;
    }

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

        if (WindowsServiceCommand.IsCurrentImagePath(installed, currentServiceExePath))
            return new ServiceResult(true, "binPath already correct, no-op.");

        if (!WindowsServiceCommand.IsRecognizedVpnRouterImagePath(installed, out _))
        {
            return new ServiceResult(
                false,
                $"Service '{ServiceName}' has an unrecognized ImagePath; refusing to overwrite it.");
        }

        var expected = WindowsServiceCommand.FormatImagePath(currentServiceExePath);
        var (code, output) = RunSc(
            "config", ServiceName, "binPath=", expected);
        if (code != 0)
            return new ServiceResult(false, $"sc config binPath= failed (exit {code}): {output}");

        return new ServiceResult(true, $"binPath updated: {installed} → {expected} (effective next service start).");
    }

    public static string? ResolveServiceExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        var sameDir = Path.Combine(baseDir, "VPNRouter.Service.exe");
        if (File.Exists(sameDir)) return sameDir;

        var subDir = Path.Combine(baseDir, "service", "VPNRouter.Service.exe");
        if (File.Exists(subDir)) return subDir;

        return null;
    }

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WindowsServiceCommand.GetSystemScPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

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
