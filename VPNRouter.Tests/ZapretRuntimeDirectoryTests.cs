using System.Security.AccessControl;
using System.Security.Principal;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretRuntimeDirectoryTests
{
    [Fact]
    public void NativeWindowsRootProtectsInheritedObjectsAndRefusesExistingOrdinaryDirectories()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(), "Windows administrator ACL fixture");
        var sandbox = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var safe = Path.Combine(sandbox, "runtime");
            ZapretRuntimeDirectory.PrepareWindowsRoot(safe);
            ZapretRuntimeDirectory.PrepareWindowsRoot(safe);
            var rootAcl = new DirectoryInfo(safe).GetAccessControl();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            Assert.True(rootAcl.AreAccessRulesProtected);
            Assert.Equal(admins, rootAcl.GetOwner(typeof(SecurityIdentifier)));

            var child = Path.Combine(safe, "component.txt");
            File.WriteAllText(child, "fixture");
            var childRules = new FileInfo(child).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Contains(childRules, rule => rule.IdentityReference.Equals(admins) && rule.IsInherited && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            Assert.Contains(childRules, rule => rule.IdentityReference.Equals(system) && rule.IsInherited && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            var writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            Assert.DoesNotContain(childRules, rule => rule.AccessControlType == AccessControlType.Allow &&
                !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system) && (rule.FileSystemRights & writes) != 0);
            Assert.Contains(childRules, rule => rule.IdentityReference.Value == "S-1-3-4" && rule.IsInherited);

            var package = Directory.CreateDirectory(Path.Combine(safe, "zapret"));
            var readme = new FileInfo(Path.Combine(package.FullName, "readme.txt"));
            File.WriteAllText(readme.FullName, "ordinary content");
            var acl = readme.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.WriteData, AccessControlType.Allow));
            readme.SetAccessControl(acl);
            Assert.Throws<UnauthorizedAccessException>(() => ZapretRuntimeDirectory.PrepareWindowsInstallation(safe, repair: false));
            ZapretRuntimeDirectory.PrepareWindowsInstallation(safe, repair: true);
            Assert.False(Directory.Exists(Path.Combine(safe, "zapret")));
            var preserved = Assert.Single(Directory.EnumerateDirectories(sandbox, "runtime.quarantine-*"));
            Assert.Equal("ordinary content", File.ReadAllText(Path.Combine(preserved, "zapret", "readme.txt")));
            ZapretRuntimeDirectory.PrepareWindowsRoot(safe);

            var privateTarget = Directory.CreateDirectory(Path.Combine(sandbox, "other")).FullName;
            var link = Directory.CreateSymbolicLink(Path.Combine(safe, "zapret"), privateTarget);
            Assert.Throws<IOException>(() => ZapretRuntimeDirectory.PrepareWindowsInstallation(safe, repair: false));
            link.Delete();

            var ordinary = Directory.CreateDirectory(Path.Combine(sandbox, "ordinary")).FullName;
            Assert.Throws<UnauthorizedAccessException>(() => ZapretRuntimeDirectory.PrepareWindowsRoot(ordinary));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void LegacyServiceMatchRequiresTheExpectedNameAndThisEditionsExactBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "Standard", "zapret", "bin");
        var old = Path.Combine(root, "winws.exe");
        var driver = Path.Combine(root, "WinDivert64.sys");
        Assert.True(ZapretLegacyServices.MatchesLegacyImage("zapret", "\"" + old + "\" --wf-tcp=443", root));
        Assert.True(ZapretLegacyServices.MatchesLegacyImage("WinDivert", "\"" + driver + "\"", root));
        Assert.False(ZapretLegacyServices.MatchesLegacyImage("another", old, root));
        Assert.False(ZapretLegacyServices.MatchesLegacyImage("WinDivert", old, root));
        Assert.False(ZapretLegacyServices.MatchesLegacyImage("zapret", old + ".other", root));
        Assert.False(ZapretLegacyServices.MatchesLegacyImage("zapret", old, root + "Business"));
    }
}
