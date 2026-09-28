using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class SettingsValidatorTests
{
    [Fact]
    public void HappyPath_FreshDefaults_ValidatesOk()
    {
        var s = NewValid();

        var result = SettingsValidator.Validate(s);

        Assert.True(result.IsValid, "fresh defaults should validate");
        Assert.Empty(result.Reasons);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ConfigMode_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.ConfigMode = "nonsense";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("config_mode"));
    }

    [Fact]
    public void ConfigMode_Empty_IsInvalid()
    {
        var s = NewValid();
        s.App.ConfigMode = "";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("config_mode"));
    }

    [Fact]
    public void RoutingMode_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.RoutingMode = "diagonal";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("routing_mode"));
    }

    [Fact]
    public void Theme_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.Theme = "neon";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("theme"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void TgProxyPort_OutOfRange_IsInvalid(int port)
    {
        var s = NewValid();
        s.App.TgProxyPort = port;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("tg_proxy_port"));
    }

    [Fact]
    public void VlessLegacyPort_OutOfRange_IsInvalid()
    {
        var s = NewValid();
        s.Vless.Port = 0;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("vless.port"));
    }

    [Fact]
    public void VlessServerEntryPort_OutOfRange_IsInvalid()
    {
        var s = NewValid();
        s.Vless.Servers.Add(new VlessServerEntry
        {
            Name = "bad",
            Server = "1.2.3.4",
            Port = 99999,
            Uuid = "u",
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("vless.servers[0].port"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1501)]
    [InlineData(70000)]
    public void TunMtu_OutOfRange_IsInvalid(int mtu)
    {
        var s = NewValid();
        s.Tun.Mtu = mtu;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("tun.mtu"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TunMtu_BelowIpv6Minimum_IsInvalidOnlyWhenIpv6Enabled(
        bool ipv6Enabled,
        bool expectedValid)
    {
        var s = NewValid();
        s.Tun.Mtu = TunSettings.MinimumIpv6Mtu - 1;
        s.Tun.Ipv6Enabled = ipv6Enabled;

        var result = SettingsValidator.Validate(s);

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(!expectedValid, result.Reasons.Any(r => r.Contains("when IPv6 is enabled")));
    }

    [Theory]
    [InlineData(1200, "Steam Datagram Relay")]
    [InlineData(1500, "PMTU")]
    public void TunMtu_RiskyValues_WarnButStayValid(int mtu, string expectedWarning)
    {
        var s = NewValid();
        s.Tun.Mtu = mtu;

        var result = SettingsValidator.Validate(s);

        Assert.True(result.IsValid);
        Assert.Empty(result.Reasons);
        Assert.Contains(result.Warnings, w => w.Contains(expectedWarning));
    }

    [Fact]
    public void HealthCheckInterval_NonPositive_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.HealthCheckInterval = 0;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("health_check_interval"));
    }

    [Fact]
    public void ProcessScanInterval_NonPositive_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.ProcessScanInterval = -5;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("process_scan_interval"));
    }

    [Fact]
    public void MaxRestartAttempts_Negative_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.MaxRestartAttempts = -1;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("max_restart_attempts"));
    }

    [Fact]
    public void DnsStrategy_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.Dns.Strategy = "ipv5_only";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("dns.strategy"));
    }

    [Fact]
    public void UpdateChannel_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.Update.Channel = "nightly";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("update.channel"));
    }

    [Fact]
    public void SubscriptionUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.App.SubscriptionUrl = "not a url";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("subscription_url"));
    }

    [Fact]
    public void SubscriptionsListUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "test",
            Url = "this is not://valid uri because of the space",
            Enabled = true,
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("subscriptions[0].url"));
    }

    [Fact]
    public void ProfileSourcesUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.ProfileSources.Add(new ProfileSource
        {
            Type = "github",
            Url = "not a uri at all",
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("profile_sources[0].url"));
    }

    [Fact]
    public void CustomConfigPathMissing_AddsWarning_StillValid()
    {
        var s = NewValid();
        s.App.ConfigMode = "custom";
        s.App.CustomConfigs.Add(new CustomConfigEntry
        {
            Name = "ghost",
            Path = Path.Combine(Path.GetTempPath(), "vpnrouter-validator-test-missing-" + Guid.NewGuid().ToString("N") + ".json"),
        });
        s.App.ActiveCustomConfig = "ghost";

        var result = SettingsValidator.Validate(s);

        Assert.True(result.IsValid, "missing custom path is a soft warning, not fatal");
        Assert.Contains(result.Warnings, w => w.Contains("missing on disk"));
    }

    [Fact]
    public void Load_RoutesInvalidConfig_ToBackupAndDefaults_AndPopulatesNotice()
    {
        SettingsLoader.ConsumeRecoveryNotice();

        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-validator-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.yaml");

        try
        {
            File.WriteAllText(configPath,
                "schema_version: 2\n" +
                "app:\n" +
                "  config_mode: nonsense\n" +
                "vless: {}\n" +
                "tun:\n" +
                "  mtu: 9000\n" +
                "monitoring:\n" +
                "  health_check_interval: 30\n" +
                "  process_scan_interval: 60\n");

            var loaded = SettingsLoader.Load(configPath);

            Assert.Equal("generated", loaded.App.ConfigMode);

            var siblings = Directory.GetFiles(dir, "config.yaml.invalid-*");
            Assert.Single(siblings);

            Assert.NotNull(SettingsLoader.LastRecoveryNotice);
            var consumed = SettingsLoader.ConsumeRecoveryNotice();
            Assert.NotNull(consumed);
            Assert.Contains("config_mode", consumed!);
            Assert.Null(SettingsLoader.LastRecoveryNotice);

            var reloaded = SettingsLoader.Load(configPath);
            Assert.Equal("generated", reloaded.App.ConfigMode);
            SettingsLoader.ConsumeRecoveryNotice();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static AppSettings NewValid()
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                Theme = "light",
                TgProxyPort = 1443,
                LogLevel = "info",
            },
            Vless = new VlessConfig
            {
                Port = 443,
                Servers = new List<VlessServerEntry>(),
            },
            Tun = new TunSettings
            {
                Mtu = TunSettings.DefaultMtu,
                InterfaceName = "VPNRouter-TUN",
                Ipv4Address = "172.19.0.1/30",
            },
            Dns = new DnsSettings
            {
                Strategy = "ipv4_only",
            },
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = 30,
                ProcessScanInterval = 60,
                MaxRestartAttempts = 5,
            },
            Update = new UpdateSettings
            {
                Channel = "stable",
            },
            ProfileSources = new List<ProfileSource>(),
        };
    }
}
