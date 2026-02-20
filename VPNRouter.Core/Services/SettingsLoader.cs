using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SettingsLoader
{
    private static readonly string DefaultConfigPath =
        Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config.yaml");

    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

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

        // Ensure routing mode has a valid value
        if (string.IsNullOrWhiteSpace(settings.App.RoutingMode))
            settings.App.RoutingMode = "split";

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

    // ─── Defaults / Example ───────────────────────────────────────────────────

    private static AppSettings CreateDefaults() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            LogFile = @"%ProgramData%\VPNRouter\logs\vpnrouter.log"
        },
        ProfileSources = new List<ProfileSource>
        {
            new()
            {
                Type = "github",
                Url = "https://raw.githubusercontent.com/username/vpn-profiles/main/profiles.json",
                UpdateInterval = 3600
            },
            new()
            {
                Type = "local",
                Path = @"%ProgramData%\VPNRouter\profiles\custom.json"
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
            ExecutablePath = @"%ProgramData%\VPNRouter\bin\sing-box.exe",
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
