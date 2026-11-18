#if PLATFORM_WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

public static class WindowsServiceHelper
{
    public const string ServiceName = VPNRouter.Core.ProductEdition.DataDirectoryName;
    public const string DisplayName = "VPN Process Router";
    public const string Description = "Routes selected application traffic through VPN using sing-box TUN mode.";

    public record ServiceResult(bool Success, string Message);

    private const uint ChangeConfig = 2, StartService = 16, StopService = 32, DeleteService = 0x10000;

    internal static Func<string, uint, IWindowsRegistration?> OpenRegistration { get; set; } =
        (name, access) => WindowsServiceRegistration.OpenWithAccess(name, access);
    internal static Func<string, bool> ServiceExeExists { get; set; } = File.Exists;
    internal static Func<string[], (int ExitCode, string Output)> SystemSc { get; set; } = RunSc;

    private static string EditionExe
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!Path.IsPathFullyQualified(root)) throw new IOException("Could not resolve Program Files.");
            return Path.Combine(root, ServiceName, "app", "VPNRouter.Service.exe");
        }
    }

    private static void CheckOwner(IWindowsRegistration registration)
    {
        if (!MainServiceRegistration.Matches(registration.Config, EditionExe))
            throw new InvalidOperationException(VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
    }

    private static string? ResolveName() =>
        MainServiceRegistration.Resolve(EditionExe, VPNRouter.Core.ProductEdition.IsBusiness, OpenRegistration);

    internal static (bool Installed, bool Running, int? Pid, string? Error, string? Name) GetState()
    {
        try
        {
            using var service = MainServiceRegistration.OpenVerified(ResolveName(), EditionExe, 0, OpenRegistration);
            if (service == null) return (false, false, null, null, null);
            var status = service.Handle.ProcessStatus;
            return (true, status.State == 4, status.State == 4 && status.Pid > 0 ? status.Pid : null, null, service.Name);
        }
        catch (Exception ex) { return (false, false, null, ex.Message, null); }
    }

    public static bool IsInstalled() => GetState().Installed;
    public static bool IsRunning() => GetState().Running;

    private static ServiceResult WithOwned(uint access, Func<MainServiceRegistration, ServiceResult> action, string? name = null)
    {
        try
        {
            using var service = MainServiceRegistration.OpenVerified(name ?? ResolveName(), EditionExe, access, OpenRegistration);
            return service == null ? new(false, "Service is not installed.") : action(service);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public static ServiceResult Install(string? exePath = null) => InstallNamed(ServiceName, exePath);

    private static ServiceResult InstallNamed(string name, string? exePath = null)
    {
        exePath ??= EditionExe;
        if (!string.Equals(exePath, EditionExe, StringComparison.OrdinalIgnoreCase))
            return new(false, VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
        try
        {
            using var requested = OpenRegistration(name, 0);
            if (requested != null) CheckOwner(requested);
            if (MainServiceRegistration.Resolve(EditionExe, VPNRouter.Core.ProductEdition.IsBusiness, OpenRegistration,
                    ignoreForeign: true) is string existing)
                return new(false, $"Service '{existing}' is already installed.");
        }
        catch (Exception ex) { return new(false, ex.Message); }

        if (!ServiceExeExists(exePath)) return new(false, $"Service executable not found: {exePath}");

        // SCM create also refuses a registration that appeared after the read.
        var (code, output) = SystemSc(WindowsServiceCommand.BuildCreateArguments(name, exePath, DisplayName));
        if (code != 0) return new(false, $"sc create failed (exit {code}): {output}");
        return WithOwned(ChangeConfig, service =>
        {
            var description = SystemSc(["description", name, Description]);
            if (description.ExitCode != 0) return new(false, description.Output);
            CheckOwner(service.Handle);
            var recovery = SystemSc(WindowsServiceCommand.BuildFailureRecoveryArguments(name));
            return recovery.ExitCode == 0 ? new(true, $"Service installed at: {exePath}") : new(false, recovery.Output);
        }, name);
    }

    private static void StopAndWait(IWindowsRegistration registration)
    {
        try { registration.Stop(); }
        catch (TimeoutException)
        {
            // The native helper waits five seconds. Allow the main VPN service its full shutdown budget.
            if (!System.Threading.SpinWait.SpinUntil(() => !registration.IsActive, TimeSpan.FromSeconds(10))) throw;
        }
    }

    public static ServiceResult Uninstall() => WithOwned(StopService | DeleteService, Remove);

    private static ServiceResult Remove(MainServiceRegistration service)
    {
        StopAndWait(service.Handle);
        CheckOwner(service.Handle);
        service.Handle.Delete();
        return new(true, $"Service '{service.Name}' uninstalled.");
    }

    internal static ServiceResult Reinstall()
    {
        string? name = null;
        var removed = WithOwned(StopService | DeleteService, service => { name = service.Name; return Remove(service); });
        return removed.Success ? InstallNamed(name!) : removed;
    }

    public static ServiceResult Start() => WithOwned(StartService, service =>
    {
        service.Handle.Start();
        for (var i = 0; i < 20; i++)
        {
            if (service.Handle.ProcessStatus.State == 4) return new(true, $"Service '{service.Name}' started.");
            System.Threading.Thread.Sleep(500);
        }
        return new(false, "Service did not reach RUNNING state within 10 seconds.");
    });

    public static ServiceResult Stop() => WithOwned(StopService, service =>
    {
        StopAndWait(service.Handle);
        return new(true, $"Service '{service.Name}' stopped.");
    });

    public static string? GetBinPath()
    {
        using var service = MainServiceRegistration.OpenVerified(ResolveName(), EditionExe, 0, OpenRegistration);
        return service?.Handle.Config.Image;
    }

    public static ServiceResult EnsureCurrentBinPath(string? currentServiceExePath = null)
    {
        if (currentServiceExePath != null && !string.Equals(currentServiceExePath, EditionExe, StringComparison.OrdinalIgnoreCase))
            return new(false, VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
        try
        {
            using var existing = MainServiceRegistration.OpenVerified(ResolveName(), EditionExe, 0, OpenRegistration);
            if (existing == null) return new(true, "Service not installed.");
            if (WindowsServiceCommand.IsCurrentImagePath(existing.Handle.Config.Image, EditionExe))
                return new(true, "binPath already correct.");
            if (!ServiceExeExists(EditionExe)) return new(false, $"Service executable not found: {EditionExe}");
            return WithOwned(ChangeConfig, service =>
            {
                service.Handle.Configure(WindowsServiceCommand.FormatImagePath(EditionExe), service.Handle.Config.Start);
                return new(true, $"binPath updated: {EditionExe}");
            }, existing.Name);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public static string? ResolveServiceExePath() => ServiceExeExists(EditionExe) ? EditionExe : null;

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WindowsServiceCommand.GetSystemScPath(),
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var windows = Path.GetDirectoryName(psi.WorkingDirectory)!;
            psi.Environment.Clear();
            psi.Environment["SystemRoot"] = psi.Environment["WINDIR"] = windows;
            psi.Environment["PATH"] = psi.WorkingDirectory;
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
