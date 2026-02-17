using System.Diagnostics;
using System.ServiceProcess;

namespace VPNRouter.Service;

/// <summary>
/// Programmatic Windows Service installer/uninstaller via sc.exe.
/// All methods require administrator rights.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "VPNRouter";
    public const string DisplayName = "VPN Process Router";
    public const string Description = "Routes selected application traffic through VPN using sing-box TUN mode.";

    // ─── Install ──────────────────────────────────────────────────────────────

    public static InstallResult Install(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current exe path");

        // Resolve to absolute path
        exePath = Path.GetFullPath(exePath);

        if (!File.Exists(exePath))
            return InstallResult.Fail($"Executable not found: {exePath}");

        if (IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is already installed. Run uninstall first.");

        // Create service: auto-start, runs as LocalSystem (needed for TUN + firewall)
        var (code, output) = RunSc(
            $"create {ServiceName} " +
            $"binPath= \"{exePath} --service\" " +
            $"start= auto " +
            $"obj= LocalSystem " +
            $"DisplayName= \"{DisplayName}\"");

        if (code != 0)
            return InstallResult.Fail($"sc create failed (exit {code}): {output}");

        // Set description
        RunSc($"description {ServiceName} \"{Description}\"");

        // Configure failure recovery: restart after 60s, 3 times, reset counter after 24h
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");

        // Use regular auto-start (not delayed) — VPN should be up ASAP after boot.
        // delayed-auto adds ~2 min delay which leaves traffic unprotected.

        return InstallResult.Ok($"Service '{ServiceName}' installed successfully.\nPath: {exePath}");
    }

    // ─── Uninstall ────────────────────────────────────────────────────────────

    public static InstallResult Uninstall()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed.");

        // Stop first if running
        if (IsRunning())
        {
            var stopResult = Stop();
            if (!stopResult.Success)
                return InstallResult.Fail($"Cannot stop service before uninstall: {stopResult.Message}");
        }

        var (code, output) = RunSc($"delete {ServiceName}");

        return code == 0
            ? InstallResult.Ok($"Service '{ServiceName}' uninstalled.")
            : InstallResult.Fail($"sc delete failed (exit {code}): {output}");
    }

    // ─── Start / Stop ─────────────────────────────────────────────────────────

    public static InstallResult Start()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed. Run install first.");

        if (IsRunning())
            return InstallResult.Ok($"Service '{ServiceName}' is already running.");

        var (code, output) = RunSc($"start {ServiceName}");

        if (code != 0)
            return InstallResult.Fail($"sc start failed (exit {code}): {output}");

        // Wait up to 10s for service to start
        return WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10))
            ? InstallResult.Ok($"Service '{ServiceName}' started.")
            : InstallResult.Fail("Service did not reach Running state within 10 seconds.");
    }

    public static InstallResult Stop()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed.");

        if (!IsRunning())
            return InstallResult.Ok($"Service '{ServiceName}' is already stopped.");

        var (code, output) = RunSc($"stop {ServiceName}");

        if (code != 0)
            return InstallResult.Fail($"sc stop failed (exit {code}): {output}");

        return WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15))
            ? InstallResult.Ok($"Service '{ServiceName}' stopped.")
            : InstallResult.Fail("Service did not reach Stopped state within 15 seconds.");
    }

    // ─── Status ───────────────────────────────────────────────────────────────

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status; // throws if not installed
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return sc.Status;
        }
        catch
        {
            return null;
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private static (int ExitCode, string Output) RunSc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
            // Note: caller must already be elevated (AdminHelper.IsAdmin() check in CLI)
        };

        using var proc = Process.Start(psi)
            ?? throw new Exception("Failed to start sc.exe");

        var output = proc.StandardOutput.ReadToEnd()
                   + proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);

        return (proc.ExitCode, output.Trim());
    }

    private static bool WaitForStatus(ServiceControllerStatus target, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.WaitForStatus(target, timeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}

public class InstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static InstallResult Ok(string message) =>
        new() { Success = true, Message = message };

    public static InstallResult Fail(string message) =>
        new() { Success = false, Message = message };
}
