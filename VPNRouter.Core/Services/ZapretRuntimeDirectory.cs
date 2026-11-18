using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VPNRouter.Core.Services;

internal static class ZapretRuntimeDirectory
{
    internal static Func<string>? TestRoot { get; set; }

    internal static string Root
    {
        get
        {
            if (TestRoot is not null) return TestRoot();
            if (!OperatingSystem.IsWindows()) return AppPaths.DataDir;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!Path.IsPathFullyQualified(programFiles) || programFiles.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("The local Program Files directory could not be resolved.");
            return Path.Combine(programFiles, "VPNRouter Zapret Runtime");
        }
    }

    // A probe, service migration or download can pin this edition across awaits. Short
    // package operations share its file handle and serialize here, including the two renames.
    private static readonly HashSet<string> PinnedRoots = new(StringComparer.Ordinal);
    internal static Action? TestLockContended { get; set; }

    internal static string Package => WithPackage(package => package);

    internal static void WithPackage(Action<string> operation) =>
        WithPackage(package => { operation(package); return true; });

    internal static T WithPackage<T>(Func<string, T> operation)
    {
        var root = Root;
        while (true)
        {
            if (!Monitor.TryEnter(PinnedRoots))
            {
                TestLockContended?.Invoke();
                Monitor.Enter(PinnedRoots);
            }
            try
            {
                if (PinnedRoots.Contains(root))
                    return operation(PreparePackage(root));

                InstallationPin? pin;
                try { pin = PinInstallation(root, FileAccess.Read); }
                catch (IOException ex) when (IsLockContention(ex)) { pin = null; }
                if (pin != null)
                {
                    using (pin) return operation(PreparePackage(root));
                }
            }
            finally { Monitor.Exit(PinnedRoots); }

            // FileShare.None also covers another process. Retry outside the local gate.
            TestLockContended?.Invoke();
            Thread.Sleep(25);
        }
    }

    internal static IDisposable LockInstallation(string root, bool repair = false)
    {
        lock (PinnedRoots)
        {
            var pin = PinInstallation(root, FileAccess.ReadWrite);
            try
            {
                PreparePackage(root, repair);
                return pin;
            }
            catch { pin.Dispose(); throw; }
        }
    }

    private sealed class InstallationPin(string root, FileStream file) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (PinnedRoots)
            {
                if (_disposed) return;
                _disposed = true;
                PinnedRoots.Remove(root);
                file.Dispose();
            }
        }
    }

    internal static bool IsLockContention(IOException ex) => (ex.HResult & 0xffff) is 11 or 32 or 33 or 35;

    private static InstallationPin PinInstallation(string root, FileAccess access)
    {
        var windows = OperatingSystem.IsWindows() && TestRoot is null;
        if (OperatingSystem.IsWindows() && windows) CheckWindowsParents(root);
        if (!windows) Directory.CreateDirectory(root);
        var file = windows ? root + ".install.lock" : Path.Combine(root, "install.lock");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Zapret installer lock is redirected.");
        var handle = new FileStream(file, FileMode.OpenOrCreate, access, FileShare.None);
        PinnedRoots.Add(root);
        return new InstallationPin(root, handle);
    }

    internal static string PrepareRoot() => Path.GetDirectoryName(Package)!;

    private static string PreparePackage(string root, bool repair = false)
    {
        if (OperatingSystem.IsWindows() && TestRoot is null)
        {
            CheckWindowsParents(root);
            PrepareWindowsInstallation(root, repair);
        }
        else Directory.CreateDirectory(root);

        var package = Path.Combine(root, "zapret");
        var previous = package + ".previous";
        if (!Directory.Exists(package) && Directory.Exists(previous)) Directory.Move(previous, package);
        return package;
    }

    [SupportedOSPlatform("windows")]
    internal static void PrepareWindowsInstallation(string root, bool repair)
    {
        try
        {
            CheckInstallation(root);
        }
        catch (Exception ex) when (repair && (ex is IOException or UnauthorizedAccessException))
        {
            // The update lock is outside the quarantined tree. Keep untrusted files for manual recovery.
            var detached = root + ".quarantine-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(root)) Directory.Move(root, detached);
            else if (File.Exists(root)) File.Move(root, detached);
            else throw;
            Serilog.Log.Warning("[Zapret] Moved the existing runtime to {Quarantine}", detached);
            CheckInstallation(root);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CheckInstallation(string root)
    {
        PrepareWindowsRoot(root);
        foreach (var name in new[] { "zapret", "zapret.previous", "temp" })
        {
            var dir = new DirectoryInfo(Path.Combine(root, name));
            if (dir.LinkTarget != null || File.Exists(dir.FullName))
                throw new IOException("Unexpected object at a Zapret runtime directory path.");
            if (dir.Exists) CheckWindowsTree(dir);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void CheckWindowsParents(string root)
    {
        for (var dir = Directory.GetParent(root); dir is not null; dir = dir.Parent)
            CheckAccess(dir, dir.GetAccessControl(), parent: true);
    }

    [SupportedOSPlatform("windows")]
    internal static void CheckWindowsTree(DirectoryInfo dir, bool allowCreatorOwnerTemplate = false)
    {
        CheckAccess(dir, dir.GetAccessControl(), parent: false, allowCreatorOwnerTemplate);
        foreach (var child in dir.EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A Zapret package entry is redirected.");
            if (child is DirectoryInfo subdirectory) CheckWindowsTree(subdirectory, allowCreatorOwnerTemplate);
            else CheckAccess(child, ((FileInfo)child).GetAccessControl(), parent: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CheckAccess(FileSystemInfo item, FileSystemSecurity acl, bool parent, bool allowCreatorOwnerTemplate = false)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A Zapret runtime path is redirected.");
        const FileSystemRights replace = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var disallowed = parent ? replace : replace | FileSystemRights.Write;
        static bool Trusted(IdentityReference? id) => id?.Value is "S-1-5-18" or "S-1-5-32-544" or
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
        var ownerLimited = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var inheritOnly = (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
            if (rule.IdentityReference.Value == "S-1-3-4" && !inheritOnly) ownerLimited = true;
            if (inheritOnly && (parent ||
                (allowCreatorOwnerTemplate && item is DirectoryInfo && rule.IdentityReference.Value == "S-1-3-0"))) continue;
            if (rule.AccessControlType != AccessControlType.Allow || Trusted(rule.IdentityReference)) continue;
            if ((rule.FileSystemRights & disallowed) != 0) throw new UnauthorizedAccessException("The Zapret runtime path has unsafe write or replacement permissions.");
        }
        if (!ownerLimited && !Trusted(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The Zapret runtime owner can override its access rules.");
    }

    internal static string SystemFile(string relativePath)
    {
        var system = Environment.SystemDirectory;
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(system))
            throw new PlatformNotSupportedException("The Windows system directory could not be resolved.");
        return Path.Combine(system, relativePath);
    }

    internal static Dictionary<string, string> ProcessEnvironment()
    {
        var system = Environment.SystemDirectory;
        var windows = Directory.GetParent(system)!.FullName;
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0");
        var scratch = Directory.CreateDirectory(Path.Combine(PrepareRoot(), "temp")).FullName;
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["ComSpec"] = Path.Combine(system, "cmd.exe"),
            ["PATH"] = string.Join(';', system, windows, Path.Combine(system, "Wbem"), powershell),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PSModulePath"] = Path.Combine(powershell, "Modules"),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["TEMP"] = scratch,
            ["TMP"] = scratch,
        };
    }

    internal static void SetProcessEnvironment(ProcessStartInfo info)
    {
        info.Environment.Clear();
        foreach (var (name, value) in ProcessEnvironment()) info.Environment[name] = value;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity WindowsSecurity()
    {
        var security = new DirectorySecurity();
        // OWNER RIGHTS prevents a file's creating user from changing its DACL with a filtered token.
        security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)(A;OICI;RC;;;OW)");
        return security;
    }

    [SupportedOSPlatform("windows")]
    internal static void PrepareWindowsRoot(string root)
    {
        if (!Directory.Exists(root))
            new DirectoryInfo(root).Create(WindowsSecurity());
        var info = new DirectoryInfo(root);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Zapret runtime directory is redirected.");
        var actual = info.GetAccessControl();
        var expected = WindowsSecurity();
        static string[] Rules(DirectorySecurity acl) =>
            acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(rule => $"{rule.IdentityReference.Value}/{rule.AccessControlType}/{(int)rule.FileSystemRights}/{rule.InheritanceFlags}/{rule.PropagationFlags}/{rule.IsInherited}")
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();

        if (!actual.AreAccessRulesProtected || !Equals(actual.GetOwner(typeof(SecurityIdentifier)), expected.GetOwner(typeof(SecurityIdentifier)))
            || !Rules(actual).SequenceEqual(Rules(expected)))
            throw new UnauthorizedAccessException("Restore the administrator-owned Zapret runtime directory and download Zapret again.");
    }
}
