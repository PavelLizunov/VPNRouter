using System;
using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

[CollectionDefinition("SingBoxFeaturesSerial", DisableParallelization = true)]
public sealed class SingBoxFeaturesSerialCollection { }

[Collection("SingBoxFeaturesSerial")]
public sealed class SingBoxFeaturesGateTests : IDisposable
{
    public SingBoxFeaturesGateTests()
    {
        SingBoxFeatures.OverrideAwg = false;
        SingBoxFeatures.OverrideXhttp = false;
    }

    public void Dispose() => SingBoxFeatures.ResetForTests();

    [Theory]
    [InlineData("awg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32")]
    [InlineData("amneziawg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32")]
    public void Parse_AwgUri_Rejected_WhenForkUnavailable(string uri)
    {
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
        Assert.Contains("sing-box-lx", ex.Message);
    }

    [Fact]
    public void IsSupportedScheme_Awg_FalseWhenForkUnavailable()
    {
        Assert.False(ServerUriParser.IsSupportedScheme("awg://x@1.2.3.4:51820"));
        Assert.False(ServerUriParser.IsSupportedScheme("amneziawg://x@1.2.3.4:51820"));
        Assert.True(ServerUriParser.IsSupportedScheme("vless://x@1.2.3.4:443"));
        Assert.True(ServerUriParser.IsSupportedScheme("hysteria2://x@1.2.3.4:443"));
    }

    [Fact]
    public void Parse_VlessXhttp_Rejected_WhenForkUnavailable()
    {
        var ex = Assert.Throws<FormatException>(() => VlessUriParser.Parse(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality" +
            "&pbk=KEY&sid=01ab&type=xhttp&path=%2Fp&sni=example.com#X"));
        Assert.Contains("sing-box-lx", ex.Message);
    }

    [Fact]
    public void Parse_PlainVless_StillWorks_WhenForkUnavailable()
    {
        var e = VlessUriParser.Parse(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality" +
            "&pbk=KEY&sid=01ab&type=ws&path=%2Fws&host=cdn.example.com&sni=example.com#X");
        Assert.Equal("ws", e.Transport.Type);
        Assert.Equal("/ws", e.Transport.Path);
    }

    [Fact]
    public void Generate_PlainVlessConfig_HasNoForkArtifacts()
    {
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), PlainVlessSettings());
        Assert.Null(cfg.Endpoints);
        var json = JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("\"endpoints\"", json);
        Assert.DoesNotContain("xhttp", json);
        Assert.DoesNotContain("x_padding_bytes", json);
        Assert.DoesNotContain("no_grpc_header", json);
    }

    [Fact]
    public void Prewarm_WithOverridesSet_NoOps_AndDoesNotThrow()
    {
        var ex = Record.Exception(() => SingBoxFeatures.Prewarm());
        Assert.Null(ex);
        Assert.False(SingBoxFeatures.AwgAvailable);
        Assert.False(SingBoxFeatures.XhttpAvailable);
    }

    [Fact]
    public void ScrubSecrets_CollapsesAwgUri_HidingPrivateKey()
    {
        var scrubbed = CrashReporter.ScrubSecrets(
            "active server awg://PEER@1.2.3.4:51820?private_key=shortpriv99&address=10.0.0.2/32");
        Assert.DoesNotContain("shortpriv99", scrubbed);
    }

    [Fact]
    public void RedactLogText_RedactsPresharedKey()
    {
        var redacted = DiagnosticsRedactor.RedactLogText("peer preshared_key=shortpsk42 configured");
        Assert.DoesNotContain("shortpsk42", redacted);
    }

    [Fact]
    public void Generate_PersistedAwgServer_Refused_WhenForkUnavailable()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), AwgOnlySettings()));
    }

    [Fact]
    public void Generate_PersistedXhttpServer_Refused_WhenForkUnavailable()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), XhttpOnlySettings()));
    }

    private static AppSettings AwgOnlySettings() => SettingsWith(new VlessServerEntry
    {
        Name = "awg", Protocol = "amneziawg", Server = "1.2.3.4", Port = 51820,
        Awg = new AwgConfig
        {
            PrivateKey = "PRIV", Address = new() { "10.13.13.2/32" },
            PeerPublicKey = "PUB", Jc = 4, H1 = "1234567890",
        },
    }, "awg");

    private static AppSettings XhttpOnlySettings() => SettingsWith(new VlessServerEntry
    {
        Name = "x", Protocol = "vless", Server = "example.com", Port = 443,
        Uuid = "11111111-1111-1111-1111-111111111111", Security = "reality",
        Reality = new VlessRealityConfig { PublicKey = "KEY", ShortId = "01ab" },
        Transport = new VlessTransportConfig { Type = "xhttp", Path = "/p", Host = "cdn.example.com" },
    }, "x");

    private static AppSettings SettingsWith(VlessServerEntry entry, string active) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig { ActiveServer = active, Servers = new List<VlessServerEntry> { entry } },
    };

    private static AppSettings PlainVlessSettings() => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "p",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "p", Protocol = "vless", Server = "example.com", Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Security = "reality", Flow = "xtls-rprx-vision",
                    Reality = new VlessRealityConfig { PublicKey = "KEY", ShortId = "01ab" },
                    Transport = new VlessTransportConfig
                    {
                        Type = "ws", Path = "/ws",
                        Headers = new Dictionary<string, string> { ["Host"] = "cdn.example.com" },
                    },
                },
            },
        },
    };
}
