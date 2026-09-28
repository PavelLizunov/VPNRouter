using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

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
