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

    internal static UpdateBackup.RestoreResult BeginAfterExit(string applicationDir, out bool handedOff)
    {
        handedOff = false;
        try
        {
            var install = UpdateBackup.SnapshotInstallDirectory(applicationDir);
            UpdateBackup.VerifyWindowsInstallDirectory(install ?? applicationDir);
            if (install == null) return UpdateBackup.CheckFlatInstall(applicationDir);
            using var parent = Process.GetCurrentProcess();
            var start = SystemShell(applicationDir);
            start.Environment["VPNROUTER_ROLLBACK_DEFERRED"] = "1";
            start.Environment["VPNROUTER_ROLLBACK_PID"] = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["VPNROUTER_ROLLBACK_TICKS"] = parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["VPNROUTER_ROLLBACK_IMAGE"] = Environment.ProcessPath!;
            start.RedirectStandardInput = start.RedirectStandardOutput = true;
            start.StandardInputEncoding = start.StandardOutputEncoding = new UTF8Encoding(false);
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadLine()))))");
            using var worker = Process.Start(start) ?? throw new IOException("Could not start the rollback worker.");
            try
            {
                var script = LauncherScript() + "\n" + Resource("VPNRouter.Windows.RepairTarget")
                    + "\n" + Resource("VPNRouter.Windows.RollbackWorker");
                worker.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)));
                string? ReadStatus() => worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
                var status = ReadStatus();
                if (status == "BUSY") return new(false, "Another update is in progress.") { OperationInProgress = true };
                if (status != "LOCKED") throw new IOException("The rollback worker could not pin the installation and initiating process: " + status);
                var backup = Path.Combine(install, "app.bak");
                if (!Directory.Exists(backup)) return new(false, "No rollback snapshot.");
                UpdateBackup.VerifyWindowsApplicationTree(install, backup);
                if (!Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Skip(4).Any())
                    return new(false, "Rollback snapshot is truncated.");
                UpdateBackup.VerifyWindowsApplicationTree(install, applicationDir);
                worker.StandardInput.WriteLine("HISTORY");
                var history = ReadStatus()?.Split(' ');
                if (history == null || history.Length != 3 || history[0] != "HISTORY" ||
                    !Guid.TryParseExact(history[1], "N", out var generation) ||
                    !int.TryParse(history[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var failures))
                    throw new IOException("The rollback worker could not verify the snapshot failure history.");
                var scheduleRepair = LaunchFailureCounter.ObserveRollbackFailures(generation, failures);
                if (failures >= LaunchFailureCounter.SelfRepairThreshold)
                {
                    var reason = $"This snapshot failed to restore {failures} times. The current installation and snapshot are retained.";
                    if (!scheduleRepair) reason += " Recovery was already requested recently. Retry repair later or contact the administrator.";
                    return new(false, reason) { SnapshotExhausted = true, RelaunchRejected = !scheduleRepair };
                }
                bool trampoline = RunHelper(backup, validateOnly: true);
                worker.StandardInput.WriteLine(trampoline ? "TRAMPOLINE" : "DIRECT");
                if (ReadStatus() != "READY") throw new IOException("The rollback worker did not accept the handoff.");
                handedOff = true;
                return new(false, "Rollback will proceed after the initiating App exits.");
            }
            finally
            {
                worker.StandardInput.Close();
                // On cancellation the worker releases its lock before SelfRepair is allowed to proceed.
                if (!handedOff && !worker.WaitForExit(5000)) throw new IOException("Rollback worker did not release its lock.");
            }
        }
        catch (Exception ex) { return new(false, ex.Message) { RelaunchRejected = true }; }
    }

    private static bool CheckLauncher(string app)
    {
        var gui = Path.Combine(app, "VPNRouter.GUI.exe");
        bool trampoline = File.Exists(gui) && !IsStandaloneGui(app);
        // The normal stub exits only after CreateProcess(App) succeeds. Its bootstrap
        // and integrity-repair branches can replace files or exit without that handoff.
        if (trampoline && (Directory.Exists(Path.Combine(app, "_bootstrap")) ||
            !InstallHealthCheck.CheckApplicationFiles(app).IsHealthy))
            throw new IOException("The rollback snapshot requires bootstrap or repair before it can launch.");
        return trampoline;
    }

    private static string Resource(string name)
    {
        using var resource = typeof(WindowsRollbackRelaunch).Assembly.GetManifestResourceStream(name) ?? throw new IOException("Missing rollback resource: " + name);
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private static string LauncherScript() => Resource("VPNRouter.Windows.RollbackEnvironment") +
        "\n" + Resource("VPNRouter.Windows.RollbackRelaunch");

    private static ProcessStartInfo SystemShell(string app)
    {
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
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        return start;
    }

    private static bool RunHelper(string app, bool validateOnly)
    {
        var start = SystemShell(app);
        bool trampoline = CheckLauncher(app);
        if (trampoline) start.Environment["VPNROUTER_ROLLBACK_TRAMPOLINE"] = "1";
        if (validateOnly) start.Environment["VPNROUTER_ROLLBACK_PREFLIGHT"] = "1";
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(LauncherScript())));

        using var helper = Process.Start(start) ?? throw new IOException("Could not start the trusted rollback launcher.");
        helper.WaitForExit();
        if (helper.ExitCode != 0) throw new IOException($"Could not securely relaunch the restored application (exit {helper.ExitCode}).");
        return trampoline;
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
