using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VPNRouter.Core;

/// <summary>
/// Cross-platform path resolution for VPNRouter data directories.
/// Windows: %ProgramData%\VPNRouter\
/// macOS:   ~/Library/Application Support/VPNRouter/
/// Linux:   ~/.config/vpnrouter/
/// </summary>
public static class AppPaths
{
    internal const UnixFileMode PrivateUnixDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static string? _dataDir;

    /// <summary>Root data directory (config, logs, cache, state).</summary>
    public static string DataDir => _dataDir ??= ResolveDataDir();

    /// <summary>
    /// Override the resolved data directory. Required on Android, where the
    /// Linux fallback (<c>$HOME/.config/vpnrouter</c>) does not map onto the
    /// per-app sandbox. Call as early as possible — before any code reads
    /// <see cref="DataDir"/> or its derivatives — passing the result of
    /// <c>Context.getFilesDir()</c>. Subsequent calls update the value
    /// (this is a static field, so a stale reader could see the previous
    /// path; callers must order initialisation accordingly).
    /// </summary>
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

    // v2.32.2 W-2 (2026-05-12) — wgturn-cli on-demand download via
    // WgturnUpdater. Moved out of shared bin/ into dedicated wgturn/
    // directory (parallel to zapret/, tg-proxy/). v2.32.1 had path in
    // shared bin/ — see SettingsMigrator.Migrate_3_to_4 for one-shot
    // move of any pre-existing binary.
    public static string WgturnDir => Path.Combine(DataDir, "wgturn");
    public static string WgturnBinDir => Path.Combine(WgturnDir, "bin");
    public static string WgturnCliExePath => Path.Combine(WgturnBinDir,
        OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli");
    public static string WgturnVersionPath => Path.Combine(WgturnDir, "version.txt");
    public static string WgturnVariantPath => Path.Combine(WgturnDir, "variant.txt");
    public static string WgturnCliLogPath => Path.Combine(LogsDir, "wgturn-cli.log");

    // DNS-tunnel (slipstream) — last-resort transport sidecar. Dedicated dir
    // parallel to wgturn/, zapret/, tg-proxy/. The binary is BUNDLED in the
    // installer (app/, like sing-box) — NOT pulled from GitHub, because
    // slipstream is reached precisely when GitHub is blocked (circular dep).
    // SlipstreamManager promotes the bundled copy to SlipstreamExePath on first
    // use. SlipstreamUpdater (on-demand refresh via a non-GitHub channel) is a
    // deferred follow-up. See plans/dns-tunnel-slipstream-integration-2026-06-10.md.
    public static string SlipstreamDir => Path.Combine(DataDir, "slipstream");
    public static string SlipstreamBinDir => Path.Combine(SlipstreamDir, "bin");
    public static string SlipstreamExePath => Path.Combine(SlipstreamBinDir,
        OperatingSystem.IsWindows() ? "slipstream-client.exe" : "slipstream-client");
    /// <summary>The slipstream-client binary as shipped inside the install
    /// payload (app/ root = <see cref="AppContext.BaseDirectory"/>), or null if
    /// not bundled. This is the SOURCE; <see cref="SlipstreamExePath"/> stays the
    /// canonical RUNTIME location (matches sing-box: bundled in app/, executed
    /// from a settings/data path). Mirrors how SingBoxManager co-locates
    /// libcronet from AppContext.BaseDirectory.</summary>
    public static string? SlipstreamBundledExePath
    {
        get
        {
            var p = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "slipstream-client.exe" : "slipstream-client");
            return File.Exists(p) ? p : null;
        }
    }
    // Active leaf cert (PEM) for the currently-selected dns-tunnel server. NOT a
    // bundled asset — the PEM travels in the dns-tunnel:// profile and is written
    // here by SlipstreamManager at launch, then passed to slipstream-client via
    // --cert. Overwritten each launch, removed on Stop. (A leaf cert is public.)
    public static string SlipstreamActiveCertPath => Path.Combine(SlipstreamDir, "active-leaf.pem");
    public static string SlipstreamVersionPath => Path.Combine(SlipstreamDir, "version.txt");
    public static string SlipstreamLogPath => Path.Combine(LogsDir, "slipstream.log");

    /// <summary>Ensure all required directories exist.</summary>
    public static void EnsureDirectories()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            EnsurePrivateUnixDirectory(DataDir);
            EnsurePrivateUnixDirectory(ConfigDir);
            EnsurePrivateUnixDirectory(LogsDir);
            EnsurePrivateUnixDirectory(CacheDir);
            EnsurePrivateUnixDirectory(BinDir);
            EnsurePrivateUnixDirectory(WgturnBinDir);
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
        Directory.CreateDirectory(WgturnBinDir);
        Directory.CreateDirectory(SlipstreamBinDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(GeoDir);

        // SEC-2: tighten %ProgramData% ACL on first run without installer.
        if (OperatingSystem.IsWindows())
            TryRestrictWindowsDataDirAcl(DataDir);
    }

    /// <summary>Create and verify an owner-only Linux/macOS directory.</summary>
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

    /// <summary>Verify and restrict an existing secret-bearing Unix file.</summary>
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

    /// <summary>Create/truncate a file without exposing new Unix content through the process umask.</summary>
    internal static FileStream CreatePrivateFile(string path)
    {
        var unix = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        if (unix) EnsurePrivateUnixFile(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
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

    /// <summary>Write UTF-8 text with owner-only creation semantics on Unix.</summary>
    internal static void WritePrivateText(string path, string content)
    {
        using var stream = CreatePrivateFile(path);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// Windows-only, best-effort: replace the inherited BUILTIN\Users read with
    /// explicit SYSTEM + Administrators (FullControl) and current-user (Modify).
    /// Uses well-known SIDs so the logic is locale-independent. Idempotent:
    /// re-runs are no-ops once the Users ACE is gone.
    /// </summary>
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

            // Well-known SIDs are locale-independent (unlike NTAccount names).
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid  = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            // Re-assert allowed identities BEFORE removing Users.
            security.SetAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.Modify,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            // RemoveAccessRuleAll drops EVERY Allow ACE for BUILTIN\Users
            // regardless of rights/inheritance/propagation flags.
            security.RemoveAccessRuleAll(new FileSystemAccessRule(
                usersSid, FileSystemRights.ReadAndExecute,
                inherit, PropagationFlags.None, AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // Best-effort: never throw from the startup path.
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

        // Linux / other
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(
            !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "vpnrouter");
    }
}
