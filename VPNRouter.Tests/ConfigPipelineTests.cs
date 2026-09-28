using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConfigPipelineTests
{
    private static VlessServerEntry MakeServer(
        string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "11111111-2222-3333-4444-" + host.GetHashCode().ToString("X").PadLeft(12, '0'),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                ShortId = "d86e92a0c6dd2271"
            }
        };

    private static AppSettings BuildBaseSettings(string configMode = "generated") =>
        new()
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = configMode,
                Subscriptions = new List<SubscriptionEntry>()
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig()
        };

    private static Profile BuildProfile() =>
        new()
        {
            Name = "TestProfile",
            DnsMode = "vpn_only",
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
            }
        };

    [Fact]
    public void Generate_HappyPath_ProducesValidJson()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        Assert.False(string.IsNullOrWhiteSpace(json));
        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        Assert.True(outbounds!.Count > 0,
            "Generated config must have at least one outbound");

        var proxyOutbound = outbounds
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        Assert.Equal("104.194.156.93", proxyOutbound!["server"]?.GetValue<string>());

        Assert.Single(settings.Vless.Servers);
        Assert.Equal("104.194.156.93", settings.Vless.Servers[0].Server);
    }

    [Fact]
    public void Generate_EmptyServers_ThrowsConfigValidationException()
    {
        var settings = BuildBaseSettings(configMode: "subscribe");
        var profile = BuildProfile();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigPipeline.Generate(
                profile,
                new[] { "Discord.exe" },
                settings,
                ConfigPipeline.ValidationMode.Strict));

        Assert.NotNull(ex.Message);
        Assert.True(
            ex.Message.Contains("subscription", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("VLESS", StringComparison.OrdinalIgnoreCase),
            $"Expected user-actionable empty-servers message, got: {ex.Message}");
    }

    [Fact]
    public void Generate_PlaceholderActiveServer_FallsBackToSubscription()
    {
        const string placeholderServer = "195.135.255.216";
        const string placeholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        const string placeholderShortId = "78ca7952";

        var settings = BuildBaseSettings();
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("de-01", "104.194.156.93", 443)
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = placeholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = placeholderPubkey,
                    ShortId = placeholderShortId
                }
            }
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);

        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);

        var server = proxyOutbound!["server"]?.GetValue<string>();
        Assert.NotEqual(placeholderServer, server);
        Assert.Equal("104.194.156.93", server);

        Assert.Equal("de-01", settings.Vless.ActiveServer);
    }

    [Fact]
    public void Generate_SubscriptionMode_AggregatesServers()
    {
        var settings = BuildBaseSettings(configMode: "subscribe");
        settings.App.ActiveSubscriptionServer = "alpha";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("alpha", "10.0.0.1", 443),
                    MakeServer("beta",  "10.0.0.2", 443),
                    MakeServer("gamma", "10.0.0.3", 443)
                }
            }
        };
        Assert.Empty(settings.Vless.Servers);

        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        Assert.Equal(3, settings.Vless.Servers.Count);
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.1");
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.2");
        Assert.Contains(settings.Vless.Servers, s => s.Server == "10.0.0.3");

        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        Assert.Equal("10.0.0.1", proxyOutbound!["server"]?.GetValue<string>());
    }

    [Fact]
    public void Generate_LegacyVlessServers_AppliedToOutput()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("legacy-A", "203.0.113.10", 443),
            MakeServer("legacy-B", "203.0.113.11", 443)
        };
        settings.Vless.ActiveServer = "legacy-A";
        var profile = BuildProfile();

        var json = ConfigPipeline.Generate(
            profile,
            new[] { "Discord.exe" },
            settings,
            ConfigPipeline.ValidationMode.Strict);

        Assert.Equal(2, settings.Vless.Servers.Count);
        Assert.Equal("legacy-A", settings.Vless.ActiveServer);

        var jo = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(jo);
        var outbounds = jo!["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);
        var proxyOutbound = outbounds!
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "vless");
        Assert.NotNull(proxyOutbound);
        Assert.Equal("203.0.113.10", proxyOutbound!["server"]?.GetValue<string>());
    }
}
