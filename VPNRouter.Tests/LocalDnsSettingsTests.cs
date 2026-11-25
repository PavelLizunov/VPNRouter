using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class LocalDnsSettingsTests
{
    private static AppSettings CreateSettings(string localDns) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "split" },
        Tun = new TunSettings(),
        Dns = new DnsSettings
        {
            VpnDns = "https://1.1.1.1/dns-query",
            LocalDns = localDns,
        },
        SingBox = new SingBoxSettings(),
        Vless = new VlessConfig
        {
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main",
                    Server = "198.51.100.1",
                    Port = 443,
                    Uuid = "00000000-0000-0000-0000-000000000001",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        Enabled = true,
                        ServerName = "www.apple.com",
                        Fingerprint = "chrome",
                        PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                        ShortId = "0123456789abcdef",
                    },
                },
            },
        },
    };

    private static Profile SplitProfile() => new()
    {
        Name = "Split",
        DnsMode = "vpn_only",
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("local")]
    [InlineData("LOCAL")]
    public void LegacyDefaultAndBlankLocalDns_KeepsEncryptedGoogleDohOverDnsDirect(string value)
    {
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, CreateSettings(value));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("8.8.8.8", local.Server);
        Assert.Null(local.ServerPort);
        Assert.Equal("/dns-query", local.Path);
        Assert.Equal("dns-direct", local.Detour);
        Assert.Null(local.DomainResolver);
        Assert.DoesNotContain(config.Dns.Servers, s => s.Tag == ConfigGenerator.LocalBootstrapDnsTag);
    }

    [Fact]
    public void ExplicitIpDohLocalDns_HonorsConfiguredServerAndPath()
    {
        var config = ConfigGenerator.Generate(
            SplitProfile(),
            new[] { "Discord.exe" },
            CreateSettings("https://77.88.8.8/dns-query"));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("77.88.8.8", local.Server);
        Assert.Null(local.ServerPort);
        Assert.Equal("/dns-query", local.Path);
        Assert.Equal("dns-direct", local.Detour);
        Assert.Null(local.DomainResolver);
        Assert.DoesNotContain(config.Dns.Servers, s => s.Tag == ConfigGenerator.LocalBootstrapDnsTag);
    }

    [Fact]
    public void ExplicitDohCustomPortAndPath_ArePreserved()
    {
        var config = ConfigGenerator.Generate(
            SplitProfile(),
            new[] { "Discord.exe" },
            CreateSettings("https://77.88.8.8:8443/custom-dns"));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("77.88.8.8", local.Server);
        Assert.Equal(8443, local.ServerPort);
        Assert.Equal("/custom-dns", local.Path);
        Assert.Equal("dns-direct", local.Detour);
    }

    [Fact]
    public void ExplicitHostnameDohLocalDns_AttachesLocalBootstrapResolver()
    {
        var settings = CreateSettings("https://common.dot.dns.yandex.net/dns-query");
        settings.App.StrictDns = true;
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, settings);

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("common.dot.dns.yandex.net", local.Server);
        Assert.Equal("/dns-query", local.Path);
        Assert.Equal("dns-direct", local.Detour);
        Assert.NotNull(local.DomainResolver);
        Assert.Equal(ConfigGenerator.LocalBootstrapDnsTag, local.DomainResolver!.Server);

        var bootstrap = Assert.Single(config.Dns.Servers, s => s.Tag == ConfigGenerator.LocalBootstrapDnsTag);
        Assert.Equal("local", bootstrap.Type);
        Assert.Null(bootstrap.Detour);

        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.SingBoxConfig);
        using var doc = JsonDocument.Parse(json);
        var serializedLocal = doc.RootElement.GetProperty("dns").GetProperty("servers")
            .EnumerateArray().Single(s => s.GetProperty("tag").GetString() == "local-dns");
        Assert.Equal(ConfigGenerator.LocalBootstrapDnsTag, serializedLocal.GetProperty("domain_resolver").GetString());
    }

    [Theory]
    [InlineData("tls://77.88.8.8", "77.88.8.8", null)]
    [InlineData("tls://77.88.8.8:853", "77.88.8.8", null)]
    [InlineData("tls://77.88.8.8:8853", "77.88.8.8", 8853)]
    public void ExplicitDotLocalDns_EmitsTypedTlsServer(string value, string expectedHost, int? expectedPort)
    {
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, CreateSettings(value));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("tls", local.Type);
        Assert.Equal(expectedHost, local.Server);
        Assert.Equal(expectedPort, local.ServerPort);
        Assert.Null(local.Path);
        Assert.Equal("dns-direct", local.Detour);
    }

    [Theory]
    [InlineData("77.88.8.8", "77.88.8.8", null)]
    [InlineData("77.88.8.8:53", "77.88.8.8", null)]
    [InlineData("77.88.8.8:5353", "77.88.8.8", 5353)]
    [InlineData("udp://77.88.8.8", "77.88.8.8", null)]
    [InlineData("udp://77.88.8.8:5353", "77.88.8.8", 5353)]
    [InlineData("[2a02:6b8::feed:ff]:5353", "2a02:6b8::feed:ff", 5353)]
    [InlineData("2a02:6b8::feed:ff", "2a02:6b8::feed:ff", null)]
    public void ExplicitUdpOrBareIpLocalDns_EmitsTypedUdpOverDnsDirect(string value, string expectedHost, int? expectedPort)
    {
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, CreateSettings(value));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("udp", local.Type);
        Assert.Equal(expectedHost, local.Server);
        Assert.Equal(expectedPort, local.ServerPort);
        Assert.Null(local.Path);
        Assert.Equal("dns-direct", local.Detour);
        Assert.Null(local.DomainResolver);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData("os")]
    public void ExplicitSystemOptIn_EmitsLocalResolverWithoutDetour(string value)
    {
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, CreateSettings(value));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("local", local.Type);
        Assert.Null(local.Server);
        Assert.Null(local.Path);
        Assert.Null(local.Detour);
    }

    [Theory]
    [InlineData("ftp://77.88.8.8")]
    [InlineData("not a valid host")]
    [InlineData("77.88.8.8:0")]
    [InlineData("77.88.8.8:99999")]
    [InlineData("https://77.88.8.8:0/dns-query")]
    [InlineData("https://192.168.1/dns-query")]
    [InlineData("tls://010.0.0.1")]
    [InlineData("udp://53")]
    [InlineData("https://user:pass@77.88.8.8/dns-query")]
    [InlineData("https://77.88.8.8/dns query")]
    [InlineData("tls://77.88.8.8:0")]
    [InlineData("udp://77.88.8.8:0")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1:5353")]
    [InlineData("53")]
    [InlineData("192.168.1.999")]
    [InlineData("8.8.8.8.8")]
    [InlineData("systm")]
    [InlineData("dhcp")]
    [InlineData("[127.0.0.1]:53")]
    [InlineData("https://[127.0.0.1]/dns-query")]
    [InlineData("2a02:6b8::feed:ff%eth0")]
    [InlineData(":53")]
    [InlineData("[::1]:0")]
    public void InvalidLocalDnsValue_FallsBackSafelyToEncryptedDefault(string value)
    {
        var config = ConfigGenerator.Generate(SplitProfile(), new[] { "Discord.exe" }, CreateSettings(value));

        var local = Assert.Single(config.Dns.Servers, s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("8.8.8.8", local.Server);
        Assert.Equal("/dns-query", local.Path);
        Assert.Equal("dns-direct", local.Detour);
        Assert.Null(local.DomainResolver);
        Assert.DoesNotContain(config.Dns.Servers, s => s.Tag == ConfigGenerator.LocalBootstrapDnsTag);
    }

    [Fact]
    public void CustomConfigInjector_StripsStaleDhcpAndLocalFields_AndAvoidsBootstrapTagCollision()
    {
        const string customWithDhcpAndCollision = """
        {
          "dns": {
            "servers": [
              { "tag": "vpn-dns", "type": "https", "server": "1.1.1.1", "path": "/dns-query", "detour": "proxy" },
              { "tag": "local-bootstrap", "type": "https", "server": "9.9.9.9", "path": "/dns-query", "detour": "proxy" },
              { "tag": "dhcp-dns", "type": "dhcp", "interface": "eth0", "prefer_go": true }
            ],
            "final": "vpn-dns"
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "198.51.100.1", "server_port": 443, "uuid": "00000000-0000-0000-0000-000000000001" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "proxy" }
        }
        """;

        var settings = CreateSettings("https://common.dot.dns.yandex.net/dns-query");
        var injected = CustomConfigInjector.Inject(customWithDhcpAndCollision, new[] { "Discord.exe" }, settings);
        var doc = (JsonNode.Parse(injected) as JsonObject)!;
        var servers = (doc["dns"]!["servers"] as JsonArray)!;

        var rewritten = servers.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == "dhcp-dns");
        Assert.Equal("https", rewritten["type"]?.ToString());
        Assert.Equal("common.dot.dns.yandex.net", rewritten["server"]?.ToString());
        Assert.Null(rewritten["interface"]);
        Assert.Null(rewritten["prefer_go"]);
        Assert.Equal("local-bootstrap-2", rewritten["domain_resolver"]?.ToString());

        var bootstrap = servers.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == "local-bootstrap-2");
        Assert.Equal("local", bootstrap["type"]?.ToString());
        Assert.Null(bootstrap["detour"]);

        // Existing user server named "local-bootstrap" stays intact.
        var existing = servers.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == "local-bootstrap");
        Assert.Equal("9.9.9.9", existing["server"]?.ToString());
    }

    [Fact]
    public void CustomConfigInjector_HonorsExplicitLocalDnsOverride_ForBothLocalReplacementAndBootstrap()
    {
        const string customWithLocal = """
        {
          "dns": {
            "servers": [
              { "tag": "vpn-dns", "type": "https", "server": "1.1.1.1", "path": "/dns-query", "detour": "proxy" },
              { "tag": "local-dns", "type": "local" }
            ],
            "final": "vpn-dns"
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "198.51.100.1", "server_port": 443, "uuid": "00000000-0000-0000-0000-000000000001" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "proxy", "default_domain_resolver": "local-dns" }
        }
        """;

        var settings = CreateSettings("https://77.88.8.8/dns-query");
        var injected = CustomConfigInjector.Inject(customWithLocal, new[] { "Discord.exe" }, settings);
        var doc = (JsonNode.Parse(injected) as JsonObject)!;
        var servers = (doc["dns"]!["servers"] as JsonArray)!;
        var local = servers.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == "local-dns");
        Assert.Equal("https", local["type"]?.ToString());
        Assert.Equal("77.88.8.8", local["server"]?.ToString());
        Assert.Equal("/dns-query", local["path"]?.ToString());
        Assert.Equal("dns-direct", local["detour"]?.ToString());

        const string customNoDns = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "vpn.example.com", "server_port": 443, "uuid": "00000000-0000-0000-0000-000000000001" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "direct" }
        }
        """;

        settings.App.RoutingMode = "full";
        settings.Dns.LocalDns = "77.88.8.8";
        var injectedNoDns = CustomConfigInjector.Inject(customNoDns, Array.Empty<string>(), settings);
        var docNoDns = (JsonNode.Parse(injectedNoDns) as JsonObject)!;
        var ddrTag = docNoDns["route"]!["default_domain_resolver"]?.ToString();
        Assert.Equal("vpnrouter-dns-direct", ddrTag);
        var ddrServer = (docNoDns["dns"]!["servers"] as JsonArray)!.OfType<JsonObject>()
            .Single(s => s["tag"]?.ToString() == ddrTag);
        Assert.Equal("udp", ddrServer["type"]?.ToString());
        Assert.Equal("77.88.8.8", ddrServer["server"]?.ToString());
        Assert.Equal("dns-direct", ddrServer["detour"]?.ToString());
    }

    [Fact]
    public void CustomConfigInjector_ExplicitSystemLocalDns_PreservesLocalTypeWithoutDetour()
    {
        const string customWithLocal = """
        {
          "dns": {
            "servers": [
              { "tag": "vpn-dns", "type": "https", "server": "1.1.1.1", "path": "/dns-query", "detour": "proxy" },
              { "tag": "local-dns", "type": "local" }
            ],
            "final": "vpn-dns"
          },
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "198.51.100.1", "server_port": 443, "uuid": "00000000-0000-0000-0000-000000000001" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "proxy", "default_domain_resolver": "local-dns" }
        }
        """;

        var settings = CreateSettings("system");
        var injected = CustomConfigInjector.Inject(customWithLocal, new[] { "Discord.exe" }, settings);
        var doc = (JsonNode.Parse(injected) as JsonObject)!;
        var local = (doc["dns"]!["servers"] as JsonArray)!.OfType<JsonObject>()
            .Single(s => s["tag"]?.ToString() == "local-dns");
        Assert.Equal("local", local["type"]?.ToString());
        Assert.Null(local["server"]);
        Assert.Null(local["detour"]);
        Assert.Equal("local-dns", doc["dns"]!["final"]?.ToString());
        Assert.Equal("local-dns", doc["route"]!["default_domain_resolver"]?.ToString());
    }
}
