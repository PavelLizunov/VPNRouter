#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsRollbackRelaunch
{
    internal static void Start(string installDir)
    {
        string app = VerifyRestoredApplication(installDir);
        using var environment = typeof(UpdateBackup).Assembly.GetManifestResourceStream("VPNRouter.Update.repair-environment.ps1")
            ?? throw new IOException("Missing the trusted repair environment helper.");
        using var launcher = typeof(WindowsRollbackRelaunch).Assembly.GetManifestResourceStream("VPNRouter.Windows.RollbackRelaunch")
            ?? throw new IOException("Missing the trusted rollback launcher.");
        using var environmentReader = new StreamReader(environment);
        using var launcherReader = new StreamReader(launcher);
        string script = environmentReader.ReadToEnd() + "\n" + launcherReader.ReadToEnd();

        var system = Environment.SystemDirectory;
        var shell = ZapretRuntimeDirectory.SystemFile(@"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, WorkingDirectory = system, CreateNoWindow = true };
        start.Environment.Clear();
        start.Environment["VPNROUTER_ROLLBACK_APP"] = app;
        start.Environment["SystemRoot"] = start.Environment["WINDIR"] = Directory.GetParent(system)!.FullName;
        start.Environment["SystemDrive"] = Path.GetPathRoot(system)!.TrimEnd('\\');
        start.Environment["PATH"] = system + ";" + Path.GetDirectoryName(shell);
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        start.Environment["ComSpec"] = Path.Combine(system, "cmd.exe");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);

        using var helper = Process.Start(start) ?? throw new IOException("Could not start the trusted rollback launcher.");
        helper.WaitForExit();
        if (helper.ExitCode != 0) throw new IOException($"Could not securely relaunch the restored application (exit {helper.ExitCode}).");
    }

    internal static string VerifyRestoredApplication(string installDir)
    {
        var drive = Path.GetPathRoot(installDir);
        if (!Path.IsPathFullyQualified(installDir) || drive == null || drive.Length != 3 ||
            !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\' ||
            new DriveInfo(drive).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("Rollback requires a protected local installation.");
        var install = new DirectoryInfo(installDir);
        var app = new DirectoryInfo(Path.Combine(install.FullName, "app"));
        ZapretRuntimeDirectory.CheckWindowsParents(install.FullName);
        ZapretRuntimeDirectory.CheckWindowsDirectory(install, allowCreatorOwnerTemplate: true);
        ZapretRuntimeDirectory.CheckWindowsTree(app, allowCreatorOwnerTemplate: true);
        return app.FullName;
    }
}
#endif
