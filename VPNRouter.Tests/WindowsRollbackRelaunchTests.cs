#if PLATFORM_WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class WindowsRollbackRelaunchTests
{
    [Theory]
    [InlineData("accepted")]
    [InlineData("gui-accepted")]
    [InlineData("gui-bootstrap")]
    [InlineData("gui-exit-2")]
    [InlineData("gui-exit-3")]
    [InlineData("ambiguous-stage")]
    [InlineData("live-acl")]
    [InlineData("publisher-lock")]
    [InlineData("file-acl")]
    [InlineData("old-scratch")]
    [InlineData("child-start")]
    public void RestoreAndStartChecksBackupBeforePublicationAndRestoresOnRelaunchFailure(string scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator ACL fixture");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Rollback publication " + Guid.NewGuid().ToString("N"));
        try
        {
            ZapretRuntimeDirectory.PrepareWindowsRoot(root);
            var live = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "app.bak")).FullName;
            File.WriteAllText(Path.Combine(live, "version.txt"), "current");
            File.WriteAllText(Path.Combine(backup, "version.txt"), "snapshot");
            for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(backup, $"fixture-{i}"), "inert");
            File.WriteAllText(Path.Combine(backup, ".update-failed"), "diagnostic");
            var exe = new FileInfo(Path.Combine(backup, "VPNRouter.App.exe"));
            if (scenario is "child-start" or "gui-exit-3" or "ambiguous-stage") File.WriteAllText(exe.FullName, "inert fixture");
            else if (scenario != "gui-exit-2") File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), exe.FullName);
            if (scenario.StartsWith("gui-", StringComparison.Ordinal))
            {
                var repo = new DirectoryInfo(AppContext.BaseDirectory);
                while (repo != null && !File.Exists(Path.Combine(repo.FullName, "VPNRouter.sln"))) repo = repo.Parent;
                Assert.NotNull(repo);
                File.Copy(Path.Combine(repo.FullName, "VPNRouter.GUI", "VPNRouter.GUI.exe"), Path.Combine(backup, "VPNRouter.GUI.exe"));
            }
            if (scenario == "gui-bootstrap") Directory.CreateDirectory(Path.Combine(backup, "_bootstrap"));
            var ambiguous = Path.Combine(root, "app.bak.tmp");
            if (scenario == "ambiguous-stage")
            {
                Directory.Move(live, ambiguous);
                File.WriteAllText(Path.Combine(ambiguous, "partial.txt"), "preserve");
            }
            if (scenario is "file-acl" or "live-acl")
            {
                if (scenario == "live-acl") exe = new FileInfo(Path.Combine(live, "version.txt"));
                var acl = exe.GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
                    FileSystemRights.WriteData, AccessControlType.Allow));
                exe.SetAccessControl(acl);
            }
            if (scenario == "old-scratch") Directory.CreateDirectory(Path.Combine(backup, ".repair-temp"));

            using var held = scenario == "publisher-lock" ? File.Open(Path.Combine(root, UpdateBackup.OperationLockName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
            var result = WindowsRollbackRelaunch.RestoreAndStart(live);
            if (scenario is "accepted" or "gui-accepted")
            {
                Assert.True(result.Restored, result.Reason);
                Assert.Equal("snapshot", File.ReadAllText(Path.Combine(live, "version.txt")));
                Assert.False(File.Exists(Path.Combine(live, ".update-failed")));
                Assert.Single(Directory.GetDirectories(Path.Combine(live, ".repair-temp")));
            }
            else
            {
                if (scenario == "publisher-lock") Assert.True(result.OperationInProgress, result.Reason);
                else Assert.True(result.RelaunchRejected, result.Reason);
                if (scenario.StartsWith("gui-exit-", StringComparison.Ordinal)) Assert.Contains("exit " + scenario[^1], result.Reason);
                Assert.False(result.Restored);
                if (scenario == "ambiguous-stage")
                {
                    Assert.False(Directory.Exists(live));
                    Assert.Equal("preserve", File.ReadAllText(Path.Combine(ambiguous, "partial.txt")));
                }
                else Assert.Equal("current", File.ReadAllText(Path.Combine(live, "version.txt")));
                Assert.Equal("snapshot", File.ReadAllText(Path.Combine(backup, "version.txt")));
                Assert.Equal("diagnostic", File.ReadAllText(Path.Combine(backup, ".update-failed")));
            }
        }
        finally
        {
            // A just-launched benign fixture may still have its image mapped.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); return true; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void InstalledAppRollsBackAfterItReleasesItsWorkingDirectoryAndImages()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator entry-point fixture");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "VPNRouter.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        // Use the actual apphost and matching build output from this test run (including the compiled edition).
        var loaded = File.ReadAllBytes(typeof(WindowsRollbackRelaunch).Assembly.Location);
        var executable = Directory.EnumerateFiles(Path.Combine(repo.FullName, "VPNRouter.App", "bin"),
            "VPNRouter.App.exe", SearchOption.AllDirectories).First(path =>
            File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")) &&
            File.ReadAllBytes(Path.ChangeExtension(path, ".dll")).AsSpan().SequenceEqual(loaded));
        var source = Path.GetDirectoryName(executable)!;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rollback apphost " + Guid.NewGuid().ToString("N"));
        try
        {
            ZapretRuntimeDirectory.PrepareWindowsRoot(root);
            var live = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(live, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                File.SetAttributes(target, FileAttributes.Normal);
            }
            File.WriteAllText(Path.Combine(live, UpdateBackup.FailureMarkerName), "offline repair fixture");
            var backup = Directory.CreateDirectory(Path.Combine(root, "app.bak")).FullName;
            for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(backup, "restored-" + i), "snapshot");
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(backup, "VPNRouter.App.exe"));
            using var app = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(live, "VPNRouter.App.exe"))
            {
                WorkingDirectory = live, UseShellExecute = false, CreateNoWindow = true
            });
            Assert.NotNull(app);
            Assert.True(app.WaitForExit(90_000), "Initiating installed App did not exit after handoff.");
            Assert.Equal(0, app.ExitCode);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(live, "restored-0")) &&
                !Directory.Exists(backup) && Directory.GetDirectories(root, "app.displaced-*").Length == 0, TimeSpan.FromSeconds(90)),
                "External worker did not finish publication after the old App unloaded.");
            Assert.False(File.Exists(Path.Combine(live, "VPNRouter.Core.dll")));
            Assert.False(File.Exists(Path.Combine(live, UpdateBackup.FailureMarkerName)));
            Assert.True(Directory.Exists(Path.Combine(live, ".repair-temp")));
            Assert.Empty(Directory.GetFiles(root, "rollback-*.log"));
        }
        finally
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); return true; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }, TimeSpan.FromSeconds(8)));
        }
    }

    [Fact]
    public void RepeatedCompensatedLaunchesReachEditionRecoveryOnlyOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator rollback fixture");
        const string childId = "VPNROUTER_TEST_ROLLBACK_ID";
        var child = Guid.TryParseExact(Environment.GetEnvironmentVariable(childId), "N", out var id);
        if (!child) id = Guid.NewGuid();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rollback attempts " + id.ToString("N"));
        var live = Path.Combine(root, "app");
        var backup = Path.Combine(root, "app.bak");
        var data = Path.Combine(root, "fixture-data");
        var repairCalls = Path.Combine(data, "repair-calls.txt");
        if (child)
        {
            // A separate process runs the production startup decision and actually exits after
            // handoff. Redirect only application data and the network installer dispatch.
            VPNRouter.Core.AppPaths.OverrideDataDir(data);
            Assert.False(VPNRouter.App.Program.RunInstallHealthCheck(live, () => File.AppendAllText(repairCalls, "repair\n")));
            File.WriteAllText(Path.Combine(data, "result"), Environment.ExitCode.ToString());
            Environment.ExitCode = 0;
            return;
        }

        try
        {
            ZapretRuntimeDirectory.PrepareWindowsRoot(root);
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(live, UpdateBackup.FailureMarkerName), "original");
            for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(backup, $"fixture-{i}"), "inert");
            File.WriteAllText(Path.Combine(backup, "VPNRouter.App.exe"), "inert non-executable fixture");
            var runner = Path.ChangeExtension(typeof(WindowsRollbackRelaunchTests).Assembly.Location, ".exe");
            for (var launch = 1; launch <= 5; launch++)
            {
                var start = new System.Diagnostics.ProcessStartInfo(runner) { UseShellExecute = false, WorkingDirectory = live };
                start.ArgumentList.Add("-method");
                start.ArgumentList.Add(typeof(WindowsRollbackRelaunchTests).FullName + "." + nameof(RepeatedCompensatedLaunchesReachEditionRecoveryOnlyOnce));
                start.Environment[childId] = id.ToString("N");
                using var process = System.Diagnostics.Process.Start(start);
                Assert.NotNull(process);
                Assert.True(process.WaitForExit(90_000), $"Fixture launch {launch} did not exit.");
                Assert.Equal(0, process.ExitCode);
                var expectedReceipts = Math.Min(launch, LaunchFailureCounter.SelfRepairThreshold);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    using var operationLock = TryOpenFixtureLock(root);
                    return operationLock != null && Directory.GetFiles(root, "rollback-*.log").Length == expectedReceipts;
                }, TimeSpan.FromSeconds(45)), $"Worker did not finish launch {launch}.");
                var strikes = LaunchFailureCounter.Read(Path.Combine(data, "launch-counter.json")).ConsecutiveFailures;
                Assert.Equal(Math.Min(launch - 1, LaunchFailureCounter.SelfRepairThreshold), strikes);
                Assert.Equal("original", File.ReadAllText(Path.Combine(live, UpdateBackup.FailureMarkerName)));
                Assert.True(File.Exists(Path.Combine(backup, "VPNRouter.App.exe")));
#if VPNROUTER_BUSINESS
                Assert.False(File.Exists(repairCalls));
                var refused = launch >= 4;
#else
                Assert.Equal(launch >= 4 ? 1 : 0, File.Exists(repairCalls) ? File.ReadAllLines(repairCalls).Length : 0);
                var refused = launch == 5;
#endif
                Assert.Equal(refused ? "1" : "0", File.ReadAllText(Path.Combine(data, "result")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static FileStream? TryOpenFixtureLock(string root)
    {
        try { return File.Open(Path.Combine(root, UpdateBackup.OperationLockName), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    [Fact]
    public void FlatApplicationCannotRestoreOrCleanANeighbor()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator ACL fixture");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Flat fixture " + Guid.NewGuid().ToString("N"));
        try
        {
            ZapretRuntimeDirectory.PrepareWindowsRoot(root);
            var app = Directory.CreateDirectory(Path.Combine(root, "relocated-flat")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "app.bak")).FullName;
            foreach (var dir in new[] { app, other, backup })
                File.WriteAllText(Path.Combine(dir, UpdateBackup.FailureMarkerName), dir);
            var result = WindowsRollbackRelaunch.RestoreAndStart(app);
            Assert.False(result.Restored);
            Assert.Contains("Flat", result.Reason);
            foreach (var dir in new[] { app, other, backup }) Assert.Equal(dir, UpdateBackup.ReadApplicationFailureMarker(dir));
            Assert.False(File.Exists(Path.Combine(root, UpdateBackup.OperationLockName)));
            Assert.True(File.Exists(Path.Combine(app, UpdateBackup.OperationLockName)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RestoredApplicationRequiresAnOwnedRootAndTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator ACL fixture");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Rollback ACL fixture " + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "app");
        try
        {
            ZapretRuntimeDirectory.PrepareWindowsRoot(root);
            Directory.CreateDirectory(app);
            string exe = Path.Combine(app, "VPNRouter.GUI.exe");
            File.WriteAllText(exe, "inert fixture");
            Assert.Equal(app, WindowsRollbackRelaunch.VerifyRestoredApplication(root));
            var guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
            var change = new FileSystemAccessRule(guests, FileSystemRights.WriteData, AccessControlType.Allow);
            var rootDir = new DirectoryInfo(root);
            var writableRoot = rootDir.GetAccessControl();
            writableRoot.AddAccessRule(change);
            rootDir.SetAccessControl(writableRoot);
            Assert.Throws<UnauthorizedAccessException>(() => WindowsRollbackRelaunch.VerifyRestoredApplication(root));
            writableRoot.RemoveAccessRuleSpecific(change);
            rootDir.SetAccessControl(writableRoot);

            var other = Directory.CreateDirectory(Path.Combine(root, "empty fixture"));
            var scratch = Directory.CreateSymbolicLink(Path.Combine(app, ".repair-temp"), other.FullName);
            Assert.Throws<IOException>(() => WindowsRollbackRelaunch.VerifyRestoredApplication(root));
            scratch.Delete();

            var file = new FileInfo(exe);
            var writableFile = file.GetAccessControl();
            writableFile.AddAccessRule(change);
            file.SetAccessControl(writableFile);
            Assert.Throws<UnauthorizedAccessException>(() => WindowsRollbackRelaunch.VerifyRestoredApplication(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
#endif
