using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SettingsLoader
{
    private static readonly string DefaultConfigPath = AppPaths.ConfigYamlPath;

    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

        // v2.23.0 self-healing: --safe flag skips parsing user yaml
        // and returns the pure defaults. Settings on disk stay intact
        // (so next normal launch picks them up), but the current
        // process sees a clean slate.
        if (SafeMode.Enabled)
            return CreateDefaults();

        if (!File.Exists(configPath))
        {
            // Write example config and return defaults
            var defaults = CreateDefaults();
            WriteExample(configPath, defaults);
            return defaults;
        }

        var yaml = File.ReadAllText(configPath);
        return Parse(yaml);
    }

    public static AppSettings Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var settings = deserializer.Deserialize<AppSettings>(yaml);

        // YamlDotNet returns null for empty/whitespace YAML
        if (settings == null)
            return new AppSettings();

        // YamlDotNet may set subsections to null if YAML has empty keys (e.g. "vless:" with no children)
        settings.App ??= new AppConfig();
        settings.Vless ??= new VlessConfig();
        settings.Tun ??= new TunSettings();
        settings.Dns ??= new DnsSettings();
        settings.SingBox ??= new SingBoxSettings();
        settings.Monitoring ??= new MonitoringSettings();
        settings.ProfileSources ??= new List<ProfileSource>();
        settings.CustomApps ??= new List<string>();

        // Nested objects inside Vless can also be null
        settings.Vless.Reality ??= new VlessRealityConfig();
        settings.Vless.Tls ??= new VlessTlsConfig();
        settings.Vless.Transport ??= new VlessTransportConfig();
        settings.Vless.Servers ??= new List<VlessServerEntry>();

        // Nested objects inside Tun
        settings.Tun.RouteExcludeAddress ??= new List<string>();

        // Update settings
        settings.Update ??= new UpdateSettings();

        // Ensure routing mode has a valid value
        if (string.IsNullOrWhiteSpace(settings.App.RoutingMode))
            settings.App.RoutingMode = "split";

        // Ensure theme has a valid value
        if (string.IsNullOrWhiteSpace(settings.App.Theme))
            settings.App.Theme = "light";

        // v2.24.0 schema migration: advance any older yaml to the current
        // schema version, persisting the upgraded form so the next load
        // starts clean. No-op for configs already at CurrentSchemaVersion.
        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            var old = settings.SchemaVersion;
            settings = SettingsMigrator.Migrate(
                settings,
                from: settings.SchemaVersion,
                to: AppSettings.CurrentSchemaVersion);
            // Persist upgraded form side-effectfully so we only migrate once.
            try { Save(settings); }
            catch { /* migration itself succeeded; re-save failure is non-fatal */ }
        }

        return settings;
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        File.WriteAllText(configPath, serializer.Serialize(settings));
    }

    /// <summary>
    /// v2.23.0 self-healing: reset user configuration to factory defaults.
    /// Current yaml is backed up (timestamped) before being overwritten,
    /// so the user can recover custom values if the reset turns out to
    /// be overkill.
    /// </summary>
    /// <returns>Path of the backup file that was created, or null if no
    /// prior config existed to back up.</returns>
    public static string? ResetToDefaults(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        string? backup = null;

        if (File.Exists(configPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            backup = $"{configPath}.backup-{stamp}";
            File.Copy(configPath, backup, overwrite: false);
        }

        Save(CreateDefaults(), configPath);
        return backup;
    }

    // ─── Defaults / Example ───────────────────────────────────────────────────

    private static AppSettings CreateDefaults() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            LogFile = Path.Combine(AppPaths.LogsDir, "vpnrouter.log")
        },
        ProfileSources = new List<ProfileSource>
        {
            new()
            {
                Type = "local",
                // v2.21.6: Linux gets its own profile (default-linux.json)
                // with bare Unix process names + wildcards (firefox*,
                // chromium-browser, telegram-desktop, etc). Before this the
                // Linux path loaded default.json with Windows-style .exe
                // names — MacProcessScanner stripped the .exe so it mostly
                // worked, but distro-specific names (firefox-bin,
                // firefox-esr) wouldn't match anything.
                Path = Path.Combine(AppPaths.ProfilesDir,
                    OperatingSystem.IsMacOS() ? "default-macos.json"
                    : OperatingSystem.IsLinux() ? "default-linux.json"
                    : "default.json")
            }
        },
        ActiveProfile = "Gaming_Full",
        Vless = new VlessConfig
        {
            Server = "your.server.com",
            Port = 443,
            Uuid = "your-uuid-here",
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "yahoo.com",
                Fingerprint = "firefox",
                PublicKey = "your-public-key-here",
                ShortId = "your-short-id"
            },
            Transport = new VlessTransportConfig
            {
                Type = "tcp",
                Path = "/"
            }
        },
        Tun = new TunSettings
        {
            InterfaceName = "VPNRouter-TUN",
            Ipv4Address = "172.19.0.1/30",
            Ipv6Enabled = false,
            Mtu = 9000,
            AutoRoute = true,
            StrictRoute = false
        },
        Dns = new DnsSettings
        {
            Strategy = "ipv4_only",
            VpnDns = "https://1.1.1.1/dns-query",
            LocalDns = "local"
        },
        SingBox = new SingBoxSettings
        {
            ExecutablePath = AppPaths.SingBoxExePath,
            AutoDownload = true
        },
        Monitoring = new MonitoringSettings
        {
            HealthCheckInterval = 30,
            RestartOnFailure = true,
            MaxRestartAttempts = 5,
            ProcessScanInterval = 60
        }
    };

    private static void WriteExample(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(settings, path);
    }
}
