using System;
using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// bug-hunt P0 (2026-06-28): fork-only protocols (awg:// / amneziawg:// and a VLESS
/// type=xhttp transport) MUST be refused at intake on an official sing-box build, else a
/// hostile / stale subscription line produces an `endpoints` wireguard block / `xhttp`
/// transport that upstream sing-box FATALs at config load — bricking the user's tunnel.
/// These tests simulate an official build (<see cref="SingBoxFeatures"/> overrides set
/// false) and assert the gate is closed, that ordinary protocols are unaffected, and that
/// a plain config carries NO fork artifacts (the dormancy invariant). Also pins the two P2
/// redaction gaps the hunt found. Shares the serial collection with the fork tests so the
/// static overrides never race.
/// </summary>
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
        // ordinary schemes are unaffected by the AWG gate
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
        // the xhttp gate must not regress ordinary transports
        var e = VlessUriParser.Parse(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality" +
            "&pbk=KEY&sid=01ab&type=ws&path=%2Fws&host=cdn.example.com&sni=example.com#X");
        Assert.Equal("ws", e.Transport.Type);
        Assert.Equal("/ws", e.Transport.Path);
    }

    [Fact]
    public void Generate_PlainVlessConfig_HasNoForkArtifacts()
    {
        // Dormancy invariant: a plain (non-AWG, non-xhttp) config must gain NONE
        // of the lx-only JSON keys, so byte-for-byte it stays an official config.
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
    public void ScrubSecrets_CollapsesAwgUri_HidingPrivateKey()
    {
        // bug-hunt P2: the proxy-URI scrubber was missing amneziawg|awg, so an
        // awg:// URI carrying a (short, non-base64) private_key would survive.
        var scrubbed = CrashReporter.ScrubSecrets(
            "active server awg://PEER@1.2.3.4:51820?private_key=shortpriv99&address=10.0.0.2/32");
        Assert.DoesNotContain("shortpriv99", scrubbed);
    }

    [Fact]
    public void RedactLogText_RedactsPresharedKey()
    {
        // bug-hunt P2: \bpsk\b never matched "preshared_key"; add the alternative.
        var redacted = DiagnosticsRedactor.RedactLogText("peer preshared_key=shortpsk42 configured");
        Assert.DoesNotContain("shortpsk42", redacted);
    }

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
