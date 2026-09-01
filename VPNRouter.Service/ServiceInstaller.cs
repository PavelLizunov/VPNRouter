using System.Diagnostics;
using System.ServiceProcess;
using VPNRouter.Core.Services;

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

    /// <summary>
    /// Dependencies required before VPNRouter starts at boot. These services
    /// form the base of the TCP/IP stack — without them sing-box cannot create
    /// the TUN adapter or resolve DNS. Declared via 'sc create depend=' so
    /// Windows doesn't race us against network initialization on cold boot.
    ///     Tcpip     — TCP/IP protocol driver (NetBT depends on this, etc)
    ///     Dnscache  — DNS client resolver
    ///     Dhcp      — DHCP client (needed to pick up LAN config on boot)
    /// Order is irrelevant — Windows treats this as a set.
    /// </summary>
    private const string ServiceDependencies = "Tcpip/Dnscache/Dhcp";

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

        // Create service: auto-start, runs as LocalSystem (needed for TUN + firewall).
        // depend= ensures we start AFTER network stack is ready, preventing race
        // conditions where sing-box fails to create TUN adapter on cold boot.
        var (code, output) = RunSc(
            WindowsServiceCommand.BuildCreateArguments(
                ServiceName, exePath, DisplayName, ServiceDependencies));

        if (code != 0)
            return InstallResult.Fail($"sc create failed (exit {code}): {output}");

        // Set description
        RunSc("description", ServiceName, Description);

        // Configure failure recovery: restart after 60s, 3 times, reset counter after 24h
        RunSc(WindowsServiceCommand.BuildFailureRecoveryArguments(ServiceName));

        // Use regular auto-start (not delayed) — VPN should be up ASAP after boot.
        // delayed-auto adds ~2 min delay which leaves traffic unprotected.

        return InstallResult.Ok($"Service '{ServiceName}' installed successfully.\nPath: {exePath}");
    }

    /// <summary>
    /// Update an already-installed service to pick up current dependency set
    /// without full uninstall/reinstall. Used after upgrades that add new
    /// 'depend=' values — e.g. v2.14.12 introduced Tcpip/Dnscache/Dhcp deps.
    /// No-op if service is not installed (returns error InstallResult).
    /// </summary>
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

    /// <summary>
    /// Read current dependency list from SCM. Returns null if service not installed
    /// or dependencies can't be read. Used by UI to show migration prompts.
    /// </summary>
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

        var (code, output) = RunSc("delete", ServiceName);

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

        var (code, output) = RunSc("start", ServiceName);

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

        var (code, output) = RunSc("stop", ServiceName);

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

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WindowsServiceCommand.GetSystemScPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
            // Note: caller must already be elevated (AdminHelper.IsAdmin() check in CLI)
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
