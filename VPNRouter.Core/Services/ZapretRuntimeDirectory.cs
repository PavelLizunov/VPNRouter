using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
            return Path.Combine(programFiles, ProductEdition.DataDirectoryName + " Zapret Runtime");
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

internal sealed class ZapretLegacyFilterDirectory(SafeFileHandle directory) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public void Dispose() => directory.Dispose();

    internal static ZapretLegacyFilterDirectory? Open(string legacyDir)
    {
        if (!Path.IsPathFullyQualified(legacyDir)) return null;
        var root = Path.GetPathRoot(legacyDir)!;
        if (OperatingSystem.IsWindows())
        {
            if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
                GetDriveTypeW(root) is not (2 or 3 or 6)) return null;
        }
        else if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ||
                 RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return null;

        var parts = Components(legacyDir[root.Length..]);
        if (parts is null) return null;
        try
        {
            using var volume = OperatingSystem.IsWindows()
                ? CreateFileW(root, Traverse | ReadAttributes, 7, 0, 3, 0x02200000, 0)
                : new SafeFileHandle((nint)OpenUnixRoot("/", UnixFlags(directory: true)), true);
            if (volume.IsInvalid || !IsSafeHandle(volume, isDirectory: true)) return null;
            var opened = Walk(volume, parts, lastIsDirectory: true, out _);
            return opened is null ? null : new ZapretLegacyFilterDirectory(opened);
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    internal string? ReadText(string relative, int maxBytes, out bool missing)
    {
        missing = false;
        var parts = Components(relative);
        if (parts is null || maxBytes < 0) return null;
        try
        {
            using var file = Walk(directory, parts, lastIsDirectory: false, out missing);
            if (file is null) return null;
            using var input = new FileStream(file, FileAccess.Read, bufferSize: 1, isAsync: false);
            var length = input.Length;
            if (length < 0 || length > maxBytes) return null;
            var bytes = new byte[(int)length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || !IsSafeHandle(file, isDirectory: false)) return null;
            var skip = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            return StrictUtf8.GetString(bytes, skip, bytes.Length - skip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or EntryPointNotFoundException)
        {
            missing = false;
            return null;
        }
    }

    private static string[]? Components(string path)
    {
        if (!OperatingSystem.IsWindows() && path.Contains('\\')) return null;
        var parts = path.Split(['/', '\\']);
        return parts.Any(name => name.Length is 0 or > 255 || name is "." or ".." ||
            name.IndexOfAny([':', '\0']) >= 0) ? null : parts;
    }

    // Release each parent only after opening and checking the next component relative to its handle.
    private static SafeFileHandle? Walk(SafeFileHandle anchor, string[] parts, bool lastIsDirectory, out bool missing)
    {
        missing = false;
        SafeFileHandle? current = null;
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                var isDirectory = lastIsDirectory || i < parts.Length - 1;
                var next = OperatingSystem.IsWindows()
                    ? OpenWindowsChild(current ?? anchor, parts[i], isDirectory, out missing)
                    : OpenUnixChild(current ?? anchor, parts[i], isDirectory, out missing);
                current?.Dispose();
                current = next;
                if (current is null || !IsSafeHandle(current, isDirectory)) return null;
            }
            var result = current;
            current = null;
            return result;
        }
        finally { current?.Dispose(); }
    }

    private const uint ReadAttributes = 0x80;
    private const uint Traverse = 0x20;

    private static SafeFileHandle? OpenWindowsChild(SafeFileHandle parent, string name, bool directory, out bool missing)
    {
        missing = false;
        int headerSize = Marshal.SizeOf<UnicodeString>();
        ushort bytes = checked((ushort)(name.Length * sizeof(char)));
        var storage = Marshal.AllocHGlobal(headerSize + bytes);
        bool retained = false;
        try
        {
            var chars = storage + headerSize;
            Marshal.Copy(name.ToCharArray(), 0, chars, name.Length);
            Marshal.StructureToPtr(new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = chars }, storage, false);
            parent.DangerousAddRef(ref retained);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = storage, Attributes = 0x1040 // OBJ_DONT_REPARSE | OBJ_CASE_INSENSITIVE
            };
            const uint synchronize = 0x100000;
            const uint openReparsePoint = 0x200000, synchronous = 0x20, completeIfOplocked = 0x100;
            // FILE_DIRECTORY_FILE restricts the other open options. Check the opened parent
            // handle's directory bit in Walk before resolving a child against it.
            var options = openReparsePoint | synchronous | completeIfOplocked;
            if (!directory) options |= 0x40; // FILE_NON_DIRECTORY_FILE
            var status = NtOpenFile(out var handle, synchronize | ReadAttributes | (directory ? Traverse : 1u),
                ref attributes, out _, directory ? 7u : 1u, options);
            if (status == 0) return handle;
            handle?.Dispose();
            missing = status is 0xc0000034 or 0xc000000f; // NAME_NOT_FOUND or NO_SUCH_FILE on this component
            return null;
        }
        finally
        {
            if (retained) parent.DangerousRelease();
            Marshal.FreeHGlobal(storage);
        }
    }

    private static bool IsSafeHandle(SafeFileHandle handle, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var file) ||
                NtQueryVolumeInformationFile(handle, out _, out var device, 8, 4) != 0 ||
                (device.Characteristics & 0x10) != 0) return false; // FILE_REMOTE_DEVICE
            return (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                file.Attributes.HasFlag(FileAttributes.Directory) == isDirectory && (isDirectory || file.Links == 1);
        }

        if (OperatingSystem.IsMacOS())
        {
            // Darwin's 64-bit inode ABI is the default on arm64 and a suffixed symbol on x64.
            var intel = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            DarwinStat file;
            if ((intel ? MacStat64(handle, out file) : MacStat(handle, out file)) != 0) return false;
            return IsUnixType(file.Mode, file.Links, isDirectory);
        }

        const uint typeAndLinks = 1 | 4; // STATX_TYPE | STATX_NLINK
        return Statx(handle, "", 0x1000, typeAndLinks, out var stat) == 0 && // AT_EMPTY_PATH
            (stat.Mask & typeAndLinks) == typeAndLinks && IsUnixType(stat.Mode, stat.Links, isDirectory);
    }

    private static bool IsUnixType(ushort mode, uint links, bool directory) =>
        (mode & 0xf000) == (directory ? 0x4000 : 0x8000) && (directory || links == 1);

    private static int UnixFlags(bool directory)
    {
        // O_RDONLY | O_NONBLOCK | O_NOCTTY | O_NOFOLLOW | O_CLOEXEC, plus O_DIRECTORY for ancestors.
        if (OperatingSystem.IsMacOS()) return 0x01020104 | (directory ? 0x100000 : 0);
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        return 0x80900 | (arm ? 0x8000 : 0x20000) | (directory ? (arm ? 0x4000 : 0x10000) : 0);
    }

    private static SafeFileHandle? OpenUnixChild(SafeFileHandle parent, string name, bool directory, out bool missing)
    {
        int fd = OpenAt(parent, name, UnixFlags(directory));
        missing = fd < 0 && Marshal.GetLastPInvokeError() == 2; // ENOENT; dangling symlinks fail O_NOFOLLOW
        return fd < 0 ? null : new SafeFileHandle((nint)fd, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory, ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status, Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint DeviceType, Characteristics; }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WinFileInfo
    {
        [FieldOffset(0)] public FileAttributes Attributes;
        [FieldOffset(40)] public uint Links;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Links;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("ntdll.dll")]
    private static extern uint NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, uint share, uint options);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryVolumeInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        out DeviceInfo device, uint length, int infoClass);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WinFileInfo info);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixRoot(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string name, int flags);

    [DllImport("libc", EntryPoint = "statx")]
    private static extern int Statx(SafeFileHandle fd, string path, int flags, uint mask, out LinuxStatx stat);

    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int MacStat(SafeFileHandle fd, out DarwinStat stat);

    [DllImport("libc", EntryPoint = "fstat$INODE64")]
    private static extern int MacStat64(SafeFileHandle fd, out DarwinStat stat);
}
