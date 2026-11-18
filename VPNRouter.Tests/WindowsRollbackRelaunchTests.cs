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
            if (scenario == "child-start") File.WriteAllText(exe.FullName, "inert fixture");
            else File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), exe.FullName);

            if (scenario == "file-acl")
            {
                var acl = exe.GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
                    FileSystemRights.WriteData, AccessControlType.Allow));
                exe.SetAccessControl(acl);
            }
            if (scenario == "old-scratch") Directory.CreateDirectory(Path.Combine(backup, ".repair-temp"));

            var result = WindowsRollbackRelaunch.RestoreAndStart(root);
            if (scenario == "accepted")
            {
                Assert.True(result.Restored, result.Reason);
                Assert.Equal("snapshot", File.ReadAllText(Path.Combine(live, "version.txt")));
                Assert.False(File.Exists(Path.Combine(live, ".update-failed")));
                Assert.Single(Directory.GetDirectories(Path.Combine(live, ".repair-temp")));
            }
            else
            {
                Assert.True(result.RelaunchRejected, result.Reason);
                Assert.False(result.Restored);
                Assert.Equal("current", File.ReadAllText(Path.Combine(live, "version.txt")));
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
