#if PLATFORM_WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

public static class WindowsServiceHelper
{
    public const string ServiceName = "VPNRouter";
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
        var expected = EditionExe;
        var config = registration.Config;
        var image = config.Image.Trim();
        var quoted = $"\"{expected}\"";
        bool Matches(string path) => image.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            image.Equals(path + " --service", StringComparison.OrdinalIgnoreCase);
        if (config.Type != 0x10 || !(Matches(quoted) || (!expected.Any(char.IsWhiteSpace) && Matches(expected))))
            throw new InvalidOperationException(VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
    }

    private static (bool Running, int? Pid) ReadStatus()
    {
        var (code, output) = SystemSc(["queryex", ServiceName]);
        if (code != 0) throw new IOException(output);
        var running = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        int? pid = null;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals("PID", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1].Trim(), out int id) && id > 0) pid = id;
        }
        return (running, running ? pid : null);
    }

    internal static (bool Installed, bool Running, int? Pid, string? Error) GetState()
    {
        try
        {
            using var registration = OpenRegistration(ServiceName, 0);
            if (registration == null) return (false, false, null, null);
            CheckOwner(registration);
            var (running, pid) = ReadStatus();
            return (true, running, pid, null);
        }
        catch (Exception ex) { return (false, false, null, ex.Message); }
    }

    public static bool IsInstalled() => GetState().Installed;
    public static bool IsRunning() => GetState().Running;

    private static ServiceResult WithOwned(uint access, Func<IWindowsRegistration, ServiceResult> action)
    {
        try
        {
            using var registration = OpenRegistration(ServiceName, access);
            if (registration == null) return new(false, $"Service '{ServiceName}' is not installed.");
            CheckOwner(registration);
            return action(registration);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public static ServiceResult Install(string? exePath = null)
    {
        exePath ??= EditionExe;
        if (!string.Equals(exePath, EditionExe, StringComparison.OrdinalIgnoreCase))
            return new(false, VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
        try
        {
            using var existing = OpenRegistration(ServiceName, 0);
            if (existing != null)
            {
                CheckOwner(existing);
                return new(false, $"Service '{ServiceName}' is already installed.");
            }
        }
        catch (Exception ex) { return new(false, ex.Message); }

        if (!ServiceExeExists(exePath)) return new(false, $"Service executable not found: {exePath}");

        // SCM create also refuses a registration that appeared after the read.
        var (code, output) = SystemSc(WindowsServiceCommand.BuildCreateArguments(ServiceName, exePath, DisplayName));
        if (code != 0) return new(false, $"sc create failed (exit {code}): {output}");
        return WithOwned(ChangeConfig, registration =>
        {
            var description = SystemSc(["description", ServiceName, Description]);
            if (description.ExitCode != 0) return new(false, description.Output);
            CheckOwner(registration);
            var recovery = SystemSc(WindowsServiceCommand.BuildFailureRecoveryArguments(ServiceName));
            return recovery.ExitCode == 0 ? new(true, $"Service installed at: {exePath}") : new(false, recovery.Output);
        });
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

    public static ServiceResult Uninstall() => WithOwned(StopService | DeleteService, registration =>
    {
        StopAndWait(registration);
        CheckOwner(registration);
        registration.Delete();
        return new(true, $"Service '{ServiceName}' uninstalled.");
    });

    public static ServiceResult Start() => WithOwned(StartService, registration =>
    {
        registration.Start();
        for (var i = 0; i < 20; i++)
        {
            if (ReadStatus().Running) return new(true, $"Service '{ServiceName}' started.");
            System.Threading.Thread.Sleep(500);
        }
        return new(false, "Service did not reach RUNNING state within 10 seconds.");
    });

    public static ServiceResult Stop() => WithOwned(StopService, registration =>
    {
        StopAndWait(registration);
        return new(true, $"Service '{ServiceName}' stopped.");
    });

    public static ServiceResult EnsureCurrentBinPath(string? currentServiceExePath = null)
    {
        if (currentServiceExePath != null && !string.Equals(currentServiceExePath, EditionExe, StringComparison.OrdinalIgnoreCase))
            return new(false, VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict);
        try
        {
            using var existing = OpenRegistration(ServiceName, 0);
            if (existing == null) return new(true, "Service not installed.");
            CheckOwner(existing);
            if (WindowsServiceCommand.IsCurrentImagePath(existing.Config.Image, EditionExe))
                return new(true, "binPath already correct.");
            if (!ServiceExeExists(EditionExe)) return new(false, $"Service executable not found: {EditionExe}");
            return WithOwned(ChangeConfig, registration =>
            {
                registration.Configure(WindowsServiceCommand.FormatImagePath(EditionExe), registration.Config.Start);
                return new(true, $"binPath updated: {EditionExe}");
            });
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

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
