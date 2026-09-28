using System.Diagnostics;
using System.ServiceProcess;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

public static class ServiceInstaller
{
    public const string ServiceName = "VPNRouter";
    public const string DisplayName = "VPN Process Router";
    public const string Description = "Routes selected application traffic through VPN using sing-box TUN mode.";

    private const string ServiceDependencies = "Tcpip/Dnscache/Dhcp";

    public static InstallResult Install(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current exe path");

        exePath = Path.GetFullPath(exePath);

        if (!File.Exists(exePath))
            return InstallResult.Fail($"Executable not found: {exePath}");

        if (IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is already installed. Run uninstall first.");

        var (code, output) = RunSc(
            WindowsServiceCommand.BuildCreateArguments(
                ServiceName, exePath, DisplayName, ServiceDependencies));

        if (code != 0)
            return InstallResult.Fail($"sc create failed (exit {code}): {output}");

        RunSc("description", ServiceName, Description);

        RunSc(WindowsServiceCommand.BuildFailureRecoveryArguments(ServiceName));

        return InstallResult.Ok($"Service '{ServiceName}' installed successfully.\nPath: {exePath}");
    }

    public static InstallResult UpdateDependencies()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed.");

        var (code, output) = RunSc(
            "config", ServiceName, "depend=", ServiceDependencies);

        return code == 0
            ? InstallResult.Ok($"Dependencies updated: {ServiceDependencies.Replace('/', ',')}")
            : InstallResult.Fail($"sc config failed (exit {code}): {output}");
    }

    public static string[]? GetDependencies()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.ServicesDependedOn.Select(s => s.ServiceName).ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static InstallResult Uninstall()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed.");

        if (IsRunning())
        {
            var stopResult = Stop();
            if (!stopResult.Success)
                return InstallResult.Fail($"Cannot stop service before uninstall: {stopResult.Message}");
        }

        var (code, output) = RunSc("delete", ServiceName);

        return code == 0
            ? InstallResult.Ok($"Service '{ServiceName}' uninstalled.")
            : InstallResult.Fail($"sc delete failed (exit {code}): {output}");
    }

    public static InstallResult Start()
    {
        if (!IsInstalled())
            return InstallResult.Fail($"Service '{ServiceName}' is not installed. Run install first.");

        if (IsRunning())
            return InstallResult.Ok($"Service '{ServiceName}' is already running.");

        var (code, output) = RunSc("start", ServiceName);

        if (code != 0)
            return InstallResult.Fail($"sc start failed (exit {code}): {output}");

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

        var (code, output) = RunSc("stop", ServiceName);

        if (code != 0)
            return InstallResult.Fail($"sc stop failed (exit {code}): {output}");

        return WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15))
            ? InstallResult.Ok($"Service '{ServiceName}' stopped.")
            : InstallResult.Fail("Service did not reach Stopped state within 15 seconds.");
    }

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
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

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
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
