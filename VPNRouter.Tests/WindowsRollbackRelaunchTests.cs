#if PLATFORM_WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class WindowsRollbackRelaunchTests
{
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
