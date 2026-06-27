using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// AmneziaWG (AWG2) endpoint for the sing-box-lx fork. The emitted schema was verified
/// against the real `sing-box-lx check` binary (2026-06-27): a `wireguard` endpoint with
/// promoted obfuscation fields + a peer using `persistent_keepalive_interval`. Zero/empty
/// AWG fields are omitted so a plain WireGuard endpoint stays byte-identical to upstream.
/// See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
/// <para>The class forces <see cref="SingBoxFeatures.OverrideAwg"/> = true so the awg://
/// intake gate (which defaults closed on an official build) lets these fork tests run.
/// Shares the serial collection with the other fork tests so the static override can't
/// race a class that asserts the gate is closed.</para>
/// </summary>
[Collection("SingBoxFeaturesSerial")]
public sealed class AmneziaWgEndpointTests : IDisposable
{
    public AmneziaWgEndpointTests() => SingBoxFeatures.OverrideAwg = true;
    public void Dispose() => SingBoxFeatures.ResetForTests();

    private static VlessServerEntry Entry() => new()
    {
        Protocol = "amneziawg", Server = "1.2.3.4", Port = 51820,
        Awg = new AwgConfig
        {
            PrivateKey = "PRIVKEYBASE64", Address = new() { "10.13.13.2/32" },
            PeerPublicKey = "PUBKEYBASE64", Keepalive = 25,
            Jc = 4, Jmin = 40, Jmax = 70, S1 = 86, S2 = 574,
            H1 = "43613244-384550127",
        },
    };

    [Fact]
    public void Build_PopulatesInterfaceAndPeer()
    {
        var ep = ConfigGenerator.BuildAmneziaWgEndpoint(Entry(), "proxy");
        Assert.Equal("wireguard", ep.Type);
        Assert.Equal("proxy", ep.Tag);
        Assert.Equal("PRIVKEYBASE64", ep.PrivateKey);
        Assert.Equal(new[] { "10.13.13.2/32" }, ep.Address);
        var peer = Assert.Single(ep.Peers);
        Assert.Equal("1.2.3.4", peer.Address);     // Server -> peer endpoint host
        Assert.Equal(51820, peer.Port);
        Assert.Equal("PUBKEYBASE64", peer.PublicKey);
        Assert.Equal(25, peer.PersistentKeepaliveInterval);
        Assert.Equal(new[] { "0.0.0.0/0" }, peer.AllowedIps);
    }

    [Fact]
    public void Serialize_MatchesVerifiedSchema_OmitsUnsetFields()
    {
        var json = JsonSerializer.Serialize(ConfigGenerator.BuildAmneziaWgEndpoint(Entry(), "proxy"));
        Assert.Contains("\"type\":\"wireguard\"", json);
        Assert.Contains("\"jc\":4", json);
        Assert.Contains("\"s2\":574", json);
        Assert.Contains("\"h1\":\"43613244-384550127\"", json);
        Assert.Contains("\"persistent_keepalive_interval\":25", json); // NOT persistent_keepalive
        // unset AWG params are omitted (a plain WireGuard endpoint stays byte-identical)
        Assert.DoesNotContain("\"s3\"", json);
        Assert.DoesNotContain("\"h2\"", json);
        Assert.DoesNotContain("\"i1\"", json);
        Assert.DoesNotContain("pre_shared_key", json);
    }

    [Fact]
    public void Build_DefaultsKeepaliveTo25_WhenUnset()
    {
        var e = Entry(); e.Awg!.Keepalive = 0;
        var ep = ConfigGenerator.BuildAmneziaWgEndpoint(e, "proxy");
        Assert.Equal(25, ep.Peers[0].PersistentKeepaliveInterval);
    }

    [Fact]
    public void Generate_AwgActiveServer_EmitsProxyEndpoint_NotVlessOutbound()
    {
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(), AwgSettings());

        // the AWG server becomes a "proxy" ENDPOINT (carries TCP+UDP natively)
        Assert.NotNull(cfg.Endpoints);
        var ep = Assert.Single(cfg.Endpoints!);
        Assert.Equal("wireguard", ep.Type);
        Assert.Equal("proxy", ep.Tag);
        Assert.Equal(4, ep.Jc);
        // no vless/hy2 "proxy" OUTBOUND — only the base direct ones
        Assert.DoesNotContain(cfg.Outbounds, o => o.Tag == "proxy");
        Assert.DoesNotContain(cfg.Outbounds, o => o.Tag == "proxy-udp");
        Assert.Contains(cfg.Outbounds, o => o.Tag == "direct");
        // full-tunnel routes everything at the "proxy" endpoint tag
        Assert.Equal("proxy", cfg.Route.Final);
    }

    [Fact]
    public void Parse_AwgUri_PopulatesEntry()
    {
        var e = ServerUriParser.Parse(
            "awg://PEERPUB@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32&keepalive=25" +
            "&jc=4&jmin=40&jmax=70&s1=86&s2=574&h1=43613244-384550127#Helsinki");
        Assert.Equal("amneziawg", e.Protocol);
        Assert.Equal("1.2.3.4", e.Server);
        Assert.Equal(51820, e.Port);
        Assert.Equal("Helsinki", e.Name);
        Assert.NotNull(e.Awg);
        Assert.Equal("PEERPUB", e.Awg!.PeerPublicKey);
        Assert.Equal("PRIV", e.Awg.PrivateKey);
        Assert.Equal(new[] { "10.13.13.2/32" }, e.Awg.Address);
        Assert.Equal(25, e.Awg.Keepalive);
        Assert.Equal(4, e.Awg.Jc);
        Assert.Equal(574, e.Awg.S2);
        Assert.Equal("43613244-384550127", e.Awg.H1); // range kept as raw string
    }

    [Fact]
    public void IsSupportedScheme_AcceptsAwg()
    {
        Assert.True(ServerUriParser.IsSupportedScheme("awg://x@1.2.3.4:51820"));
        Assert.True(ServerUriParser.IsSupportedScheme("amneziawg://x@1.2.3.4:51820"));
    }

    [Fact]
    public void Parse_AwgUri_PreservesPlusInKeys()
    {
        // bug-hunt (Codex): WireGuard keys are standard base64 ('+' common).
        // HttpUtility.ParseQueryString would corrupt '+' to a space.
        var e = ServerUriParser.Parse(
            "awg://PEER@1.2.3.4:51820?private_key=ab+cd/ef==&preshared_key=gh+ij/kl==&address=10.13.13.2/32#H");
        Assert.Equal("ab+cd/ef==", e.Awg!.PrivateKey);
        Assert.Equal("gh+ij/kl==", e.Awg.PresharedKey);
    }

    [Fact]
    public void Parse_AwgUri_MissingPrivateKey_Throws()
    {
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(
            "awg://PEER@1.2.3.4:51820?address=10.13.13.2/32#H"));
    }

    [Fact]
    public void Parse_AwgUri_MissingAddress_Throws()
    {
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(
            "awg://PEER@1.2.3.4:51820?private_key=PRIV#H"));
    }

    [Fact]
    public void Parse_AwgUri_PeerPubkeyWithSlash_NotTruncated()
    {
        // pre-flight regression (2026-06-28, vpnctl lx-test string): the peer
        // public key is STANDARD base64 and routinely contains '/'. System.Uri
        // treats '/' as the authority terminator and truncates the userinfo, so
        // ParseAmneziaWg must split the authority manually.
        var e = ServerUriParser.Parse(
            "awg://aB/cD+eF/gH0=@104.194.156.93:51820?private_key=PRIV&address=10.66.0.22/32#n");
        Assert.Equal("aB/cD+eF/gH0=", e.Awg!.PeerPublicKey);
        Assert.Equal("104.194.156.93", e.Server);
        Assert.Equal(51820, e.Port);
    }

    [Fact]
    public void ConfigSanityCheck_AwgEndpointConfig_IsNotDead()
    {
        // bug-hunt (Codex): CheckBeforeStart scans only outbounds; for AWG the
        // proxy is an endpoint, so without endpoint-awareness it FATALs every
        // AWG connect before sing-box launches.
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(), AwgSettings());
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(cfg))!.AsObject();
        var result = new ConfigSanityCheck().CheckBeforeStart(node);
        Assert.False(result.IsDead, "AWG endpoint config flagged dead: " + result.Reason);
    }

    [Fact]
    public void Generate_ActiveVless_SameHostAwgSibling_DoesNotEmitEndpoint()
    {
        // bug-hunt (Codex): a same-host AWG sibling must NOT hijack a selected
        // VLESS server. Active = vless -> VLESS proxy outbound, no endpoint.
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(),
            SameHostMixed("v"));
        Assert.Null(cfg.Endpoints);
        Assert.Contains(cfg.Outbounds, o => o.Tag == "proxy" && o.Type == "vless");
    }

    [Fact]
    public void Generate_ActiveAwg_SameHostVlessSibling_EmitsEndpoint()
    {
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(),
            SameHostMixed("awg"));
        Assert.NotNull(cfg.Endpoints);
        Assert.Equal("proxy", Assert.Single(cfg.Endpoints!).Tag);
    }

    private static AppSettings SameHostMixed(string active) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = active,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "v", Protocol = "vless", Server = "1.2.3.4", Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Security = "reality", Flow = "xtls-rprx-vision",
                    Reality = new VlessRealityConfig { PublicKey = "KEY", ShortId = "01ab" },
                },
                new()
                {
                    Name = "awg", Protocol = "amneziawg", Server = "1.2.3.4", Port = 51820,
                    Awg = new AwgConfig
                    {
                        PrivateKey = "PRIV", Address = new() { "10.13.13.2/32" },
                        PeerPublicKey = "PUB", Jc = 4, H1 = "1234567890",
                    },
                },
            },
        },
    };

    [Fact]
    public void Generate_AwgConfig_PassesLeakProtection()
    {
        // bug-hunt P1: LeakProtection.ValidateConfig must recognise the "proxy"
        // ENDPOINT (not just an outbound) or it hard-errors "No proxy outbound
        // defined" and Strict validation aborts every AWG connect.
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(), AwgSettings());
        var result = LeakProtection.ValidateConfig(cfg);
        Assert.True(result.IsValid, "AWG config rejected by LeakProtection: " + string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e => e.Contains("proxy outbound"));
    }

    [Fact]
    public void Generate_AwgFullTunnel_DoesNotRejectQuic()
    {
        // bug-hunt P1: an AmneziaWG tunnel is UDP-native, so the TCP-only-proxy
        // QUIC-reject must NOT fire (it would needlessly force HTTP/3 apps to TCP).
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, System.Array.Empty<string>(), AwgSettings());
        Assert.DoesNotContain(cfg.Route.Rules,
            r => r.Protocol == "quic" && r.Action == "reject");
    }

    private static AppSettings AwgSettings() => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "awg",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "awg", Protocol = "amneziawg", Server = "1.2.3.4", Port = 51820,
                    Awg = new AwgConfig
                    {
                        PrivateKey = "PRIV", Address = new() { "10.13.13.2/32" }, PeerPublicKey = "PUB",
                        Jc = 4, Jmin = 40, Jmax = 70, S1 = 86, S2 = 574, H1 = "1234567890",
                    },
                },
            },
        },
    };
}
