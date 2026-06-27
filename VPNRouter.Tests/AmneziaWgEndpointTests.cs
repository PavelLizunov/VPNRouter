using System.Collections.Generic;
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
/// </summary>
public sealed class AmneziaWgEndpointTests
{
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
