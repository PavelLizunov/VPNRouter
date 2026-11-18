#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Text;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsRollbackRelaunch
{
    internal static UpdateBackup.RestoreResult RestoreAndStart(string applicationDir)
    {
        var installDir = UpdateBackup.SnapshotInstallDirectory(applicationDir);
        try { UpdateBackup.VerifyWindowsInstallDirectory(installDir ?? applicationDir); }
        catch (Exception ex) { return new(false, ex.Message) { RelaunchRejected = true }; }
        if (installDir == null) return UpdateBackup.CheckFlatInstall(applicationDir);

        return UpdateBackup.RestoreSnapshot(installDir, Directory.Move,
            candidate => RunHelper(UpdateBackup.VerifyWindowsApplicationTree(installDir, candidate), validateOnly: true),
            () => RunHelper(VerifyRestoredApplication(installDir), validateOnly: false),
            displaced => UpdateBackup.VerifyWindowsApplicationTree(installDir, displaced));
    }

    private static void RunHelper(string app, bool validateOnly)
    {
        var gui = Path.Combine(app, "VPNRouter.GUI.exe");
        bool trampoline = File.Exists(gui) && !IsStandaloneGui(app);
        // The normal stub exits only after CreateProcess(App) succeeds. Its bootstrap
        // and integrity-repair branches can replace files or exit without that handoff.
        if (trampoline && (Directory.Exists(Path.Combine(app, "_bootstrap")) ||
            !InstallHealthCheck.CheckApplicationFiles(app).IsHealthy))
            throw new IOException("The rollback snapshot requires bootstrap or repair before it can launch.");
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
        if (trampoline) start.Environment["VPNROUTER_ROLLBACK_TRAMPOLINE"] = "1";
        if (validateOnly) start.Environment["VPNROUTER_ROLLBACK_PREFLIGHT"] = "1";
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

    internal static string VerifyRestoredApplication(string installDir) =>
        UpdateBackup.VerifyWindowsApplicationTree(installDir, Path.Combine(installDir, "app"));

    // The historical WinForms GUI was a managed apphost. A native trampoline can
    // coexist with leftover GUI sidecars after an in-place update.
    private static bool IsStandaloneGui(string app)
    {
        var gui = Path.Combine(app, "VPNRouter.GUI.exe");
        var assembly = Path.Combine(app, "VPNRouter.GUI.dll");
        if (!File.Exists(assembly) || !File.Exists(Path.Combine(app, "VPNRouter.GUI.runtimeconfig.json")) ||
            !string.Equals(FileVersionInfo.GetVersionInfo(gui).OriginalFilename, "VPNRouter.GUI.dll",
                StringComparison.OrdinalIgnoreCase)) return false;
        // A single-file compatibility stub can have the same version resource as
        // a WinForms apphost, but loads its embedded program instead of GUI.dll.
        using var host = File.OpenRead(gui);
        using var hostPe = new PEReader(host);
        var headers = hostPe.PEHeaders;
        long imageEnd = headers.SectionHeaders.Max(section => (long)section.PointerToRawData + section.SizeOfRawData);
        if (host.Length != imageEnd)
        {
            var signature = headers.PEHeader!.CertificateTableDirectory;
            if (signature.Size == 0 || signature.RelativeVirtualAddress < imageEnd ||
                signature.RelativeVirtualAddress - imageEnd >= 8 ||
                (long)signature.RelativeVirtualAddress + signature.Size != host.Length) return false;
        }
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return false;
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Any(reference =>
            metadata.StringComparer.Equals(metadata.GetAssemblyReference(reference).Name, "System.Windows.Forms"));
    }
}
#endif
