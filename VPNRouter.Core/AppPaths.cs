using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VPNRouter.Core;

public static class AppPaths
{
    internal const UnixFileMode PrivateUnixDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();

    public static void OverrideDataDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must be non-empty", nameof(path));
        _dataDir = path;
    }

    public static string ConfigDir => Path.Combine(DataDir, "config");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string BinDir => Path.Combine(DataDir, "bin");
    public static string ProfilesDir => Path.Combine(DataDir, "profiles");
    public static string GeoDir => Path.Combine(DataDir, "geo");

    public static string GeoIpRuPath => Path.Combine(GeoDir, "geoip-ru.srs");
    public static string GeoSiteRuPath => Path.Combine(GeoDir, "geosite-ru.srs");

    public static string CurrentConfigPath => Path.Combine(ConfigDir, "current.json");
    public static string SingBoxLogPath => Path.Combine(LogsDir, "singbox.log");
    public static string StatePath => Path.Combine(DataDir, "state.json");
    public static string ConfigYamlPath => Path.Combine(DataDir, "config.yaml");
    public static string SingBoxExePath => Path.Combine(BinDir,
        OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");

    public static string SlipstreamDir => Path.Combine(DataDir, "slipstream");
    public static string SlipstreamBinDir => Path.Combine(SlipstreamDir, "bin");
    public static string SlipstreamExePath => Path.Combine(SlipstreamBinDir,
        OperatingSystem.IsWindows() ? "slipstream-client.exe" : "slipstream-client");
    public static string? SlipstreamBundledExePath
    {
        get
        {
            var p = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "slipstream-client.exe" : "slipstream-client");
            return File.Exists(p) ? p : null;
        }
    }
    public static string SlipstreamActiveCertPath => Path.Combine(SlipstreamDir, "active-leaf.pem");
    public static string SlipstreamVersionPath => Path.Combine(SlipstreamDir, "version.txt");
    public static string SlipstreamLogPath => Path.Combine(LogsDir, "slipstream.log");

    public static void EnsureDirectories()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            EnsurePrivateUnixDirectory(DataDir);
            EnsurePrivateUnixDirectory(ConfigDir);
            EnsurePrivateUnixDirectory(LogsDir);
            EnsurePrivateUnixDirectory(CacheDir);
            EnsurePrivateUnixDirectory(BinDir);
            EnsurePrivateUnixDirectory(SlipstreamBinDir);
            EnsurePrivateUnixDirectory(ProfilesDir);
            EnsurePrivateUnixDirectory(GeoDir);
            EnsurePrivateUnixFile(ConfigYamlPath);
            EnsurePrivateUnixFile(CurrentConfigPath);
            return;
        }

        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(SlipstreamBinDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(GeoDir);

        if (OperatingSystem.IsWindows())
        {
            TryRestrictWindowsDataDirAcl(DataDir);
            RestrictWindowsBinDirAcl(BinDir);
        }
    }

    internal static void EnsurePrivateUnixDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException($"Refusing symbolic-link data directory: {path}");

        Directory.CreateDirectory(path, PrivateUnixDirectoryMode);
        info.Refresh();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing symbolic-link data directory: {path}");

        File.SetUnixFileMode(path, PrivateUnixDirectoryMode);
        if (File.GetUnixFileMode(path) != PrivateUnixDirectoryMode)
            throw new IOException($"Could not enforce owner-only directory permissions: {path}");
    }

    internal static void EnsurePrivateUnixFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException($"Refusing symbolic-link configuration file: {path}");
        if (!info.Exists) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing symbolic-link configuration file: {path}");

        File.SetUnixFileMode(path, PrivateUnixFileMode);
        if (File.GetUnixFileMode(path) != PrivateUnixFileMode)
            throw new IOException($"Could not enforce owner-only file permissions: {path}");
    }

    internal static FileStream CreatePrivateFile(string path, FileMode mode = FileMode.Create)
    {
        var unix = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        if (unix) EnsurePrivateUnixFile(path);
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (unix)
            options.UnixCreateMode = PrivateUnixFileMode;
        var stream = new FileStream(path, options);
        if (!unix) return stream;

        try
        {
            File.SetUnixFileMode(stream.SafeFileHandle, PrivateUnixFileMode);
            if (File.GetUnixFileMode(stream.SafeFileHandle) != PrivateUnixFileMode)
                throw new IOException($"Could not enforce owner-only file permissions: {path}");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void WritePrivateText(string path, string content)
    {
        using var stream = CreatePrivateFile(path);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    [SupportedOSPlatform("windows")]
    private static void TryRestrictWindowsDataDirAcl(string dir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            if (!dirInfo.Exists) return;

            var security = dirInfo.GetAccessControl();
            if (!HasBuiltinUsersReadAccess(security))
                return;

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);

            const InheritanceFlags inherit =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            security.SetAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.Modify,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            security.RemoveAccessRuleAll(new FileSystemAccessRule(
                usersSid, FileSystemRights.ReadAndExecute,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void RestrictWindowsBinDirAcl(string binDir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(binDir);
            if (!dirInfo.Exists) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inherit =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            security.AddAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid, FileSystemRights.ReadAndExecute,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasBuiltinUsersReadAccess(DirectorySecurity security)
    {
        try
        {
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            foreach (FileSystemAccessRule rule in
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!usersSid.Equals(rule.IdentityReference)) continue;
                if ((rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static string ResolveDataDir()
    {
        if (OperatingSystem.IsWindows())
            return Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "VPNRouter");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(
            !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "vpnrouter");
    }
}
