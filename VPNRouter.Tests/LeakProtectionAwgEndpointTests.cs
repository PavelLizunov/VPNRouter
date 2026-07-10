using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P2 (2026-07-10): the leak gate accepts an AWG "proxy" ENDPOINT as the proxy
/// (sing-box-lx emits AmneziaWG as a top-level wireguard endpoint, not an
/// outbound), but pre-fix never validated its CONTENTS — an empty
/// private_key / no peers / a peer missing its public_key or endpoint
/// address:port FATALs sing-box at startup. ValidateConfig runs in both
/// StartAsync + Apply, so it should surface an actionable error instead of a
/// bare "sing-box exited". Defense-in-depth: the awg:// parser already requires
/// these fields; this catches a custom config / future codegen path.
/// </summary>
public sealed class LeakProtectionAwgEndpointTests
{
    private static SingBoxConfig AwgConfig(SingBoxEndpoint proxy)
        => new()
        {
            Dns = new SingBoxDns
            {
                Strategy = "ipv4_only",
                Final = "local-dns",
                Servers = new List<DnsServer>
                {
                    new() { Tag = "vpn-dns", Type = "https", Server = "1.1.1.1", Detour = "proxy" },
                    new() { Tag = "local-dns", Type = "local" },
                },
            },
            Inbounds = new List<SingBoxInbound>
            {
                new() { Type = "tun", Tag = "tun-in", Address = new() { "172.19.0.1/30" } },
            },
            // proxy is the ENDPOINT; only "direct" lives in outbounds
            Outbounds = new List<SingBoxOutbound> { new() { Type = "direct", Tag = "direct" } },
            Endpoints = new List<SingBoxEndpoint> { proxy },
            Route = new SingBoxRoute
            {
                Final = "direct",
                Rules = new List<RouteRule> { new() { Action = "hijack-dns" } },
            },
        };

    private static SingBoxEndpoint HealthyProxy() => new()
    {
        Type = "wireguard",
        Tag = "proxy",
        Address = new() { "10.66.0.2/32" },
        PrivateKey = "aPrivateKeyBase64==",
        Peers = new()
        {
            new WireGuardPeer { Address = "93.95.226.167", Port = 51822, PublicKey = "peerPubKey==" },
        },
    };

    [Fact]
    public void HealthyAwgEndpoint_passes()
    {
        var result = LeakProtection.ValidateConfig(AwgConfig(HealthyProxy()));
        Assert.DoesNotContain(result.Errors, e => e.Contains("AWG 'proxy' endpoint"));
        // hasProxy is satisfied by the endpoint → no "No 'proxy' outbound defined".
        Assert.DoesNotContain(result.Errors, e => e.Contains("No 'proxy' outbound"));
    }

    [Fact]
    public void EmptyPrivateKey_isError()
    {
        var ep = HealthyProxy(); ep.PrivateKey = "";
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("private_key is empty"));
    }

    [Fact]
    public void NoLocalAddress_isError()
    {
        var ep = HealthyProxy(); ep.Address = new();
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("no local tunnel address"));
    }

    [Fact]
    public void NoPeers_isError()
    {
        var ep = HealthyProxy(); ep.Peers = new();
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("no peers"));
    }

    [Fact]
    public void PeerMissingPublicKey_isError()
    {
        var ep = HealthyProxy(); ep.Peers[0].PublicKey = "";
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("peer[0]: public_key is empty"));
    }

    [Fact]
    public void PeerMissingAddress_isError()
    {
        var ep = HealthyProxy(); ep.Peers[0].Address = "";
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("endpoint address is empty"));
    }

    [Fact]
    public void PeerInvalidPort_isError()
    {
        var ep = HealthyProxy(); ep.Peers[0].Port = 0;
        var result = LeakProtection.ValidateConfig(AwgConfig(ep));
        Assert.Contains(result.Errors, e => e.Contains("invalid port 0"));
    }
}
