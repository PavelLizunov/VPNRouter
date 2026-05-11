namespace VPNRouter.Core;

/// <summary>
/// Cross-platform path resolution for VPNRouter data directories.
/// Windows: %ProgramData%\VPNRouter\
/// macOS:   ~/Library/Application Support/VPNRouter/
/// Linux:   ~/.config/vpnrouter/
/// </summary>
public static class AppPaths
{
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

    // r9 Phase 2 — wgturn-core emergency fallback channel binary +
    // dedicated log. Phase 1 (separate chip) drops the binary into
    // BinDir at install time; this is just the path resolver.
    public static string WgturnCliExePath => Path.Combine(BinDir,
        OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli");
    public static string WgturnCliLogPath => Path.Combine(LogsDir, "wgturn-cli.log");

    /// <summary>Ensure all required directories exist.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(GeoDir);
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
