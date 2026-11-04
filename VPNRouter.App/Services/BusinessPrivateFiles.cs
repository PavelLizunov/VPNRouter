using System.Security.AccessControl;
using System.Security.Principal;

namespace VPNRouter.App.Services;

internal static class BusinessPrivateFiles
{
    internal static void RefuseLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked diagnostic path refused.");
    }

    internal static void CreateDirectory(string path)
    {
        RefuseLinks(path);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        RefuseLinks(path);
    }
}
