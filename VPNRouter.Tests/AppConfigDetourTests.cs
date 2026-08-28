#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Spec-derived tests for VPNRouter app-config client detour handling.
/// Pins:
/// 1. VLESS URI parser preserves outbound/detour query metadata.
/// 2. Subscription fetch sends X-VPNRouter-Capabilities: detour-v1.
/// 3. GetActiveServers includes the upstream entry when a chained target is active.
/// 4. ConfigGenerator emits upstream outbound + target proxy outbound with exact detour.
/// 5. Missing upstream for a chained target fails closed with an exception.
/// 6. Ordinary single/no-detour outbound has no detour property set.
/// </summary>
[Collection(SubscriptionFetcherCollection.Name)]
public class AppConfigDetourTests
{
    private const string ChainedTargetUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4&sid=deadbeef" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision" +
        "&outbound=target-node-1&detour=upstream-node-1#ChainedTarget";

    private const string OrdinaryUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4&sid=deadbeef" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision#Ordinary";

    private static AppSettings CreateSettingsWithServers(params VlessServerEntry[] servers)
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>(servers)
            }
        };

        if (servers.Length > 0)
        {
            settings.Vless.ActiveServer = servers[0].Name;
        }

        return settings;
    }

    private static Profile CreateTestProfile()
    {
        return new Profile
        {
            Name = "DetourTestProfile",
            DnsMode = "vpn_only",
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
            }
        };
    }

    [Fact]
    public void Parse_VlessUriWithOutboundAndDetour_PreservesMetadataOnEntry()
    {
        // Arrange & Act
        var entry = VlessUriParser.Parse(ChainedTargetUri);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal("target-node-1", entry.OutboundId);
        Assert.Equal("upstream-node-1", entry.DetourVia);
    }

    [Fact]
    public async Task FetchAsync_SubscriptionFetch_SendsDetourCapabilityHeader()
    {
        // Arrange
        const string subUrl = "https://provider.example/api/v1/app/config/test-device";
        var fake = new FakeHttpClient().Setup(subUrl, $"{OrdinaryUri}\n");
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;

        try
        {
            // Act
            var servers = await SubscriptionFetcher.FetchAsync(subUrl);

            // Assert
            Assert.NotEmpty(servers);
            var requests = fake.SentRequests;
            Assert.Single(requests);

            var sentRequest = requests[0];
            Assert.NotNull(sentRequest.Headers);

            var capHeader = sentRequest.Headers.FirstOrDefault(h =>
                string.Equals(h.Key, "X-VPNRouter-Capabilities", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(capHeader.Key);
            Assert.Equal("detour-v1", capHeader.Value);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    [Fact]
    public void GetActiveServers_ChainedTargetSelected_IncludesUpstreamServer()
    {
        // Arrange
        var upstream = new VlessServerEntry
        {
            Name = "Upstream-Server",
            Server = "100.64.0.1",
            Port = 443,
            Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "upstream-node-1"
        };

        var target = new VlessServerEntry
        {
            Name = "Chained-Target",
            Server = "194.87.222.111",
            Port = 443,
            Uuid = "3e65553e-269f-50f3-c336-78cb2b6c88f5",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "target-node-1",
            DetourVia = "upstream-node-1"
        };

        var vlessConfig = new VlessConfig
        {
            ActiveServer = "Chained-Target",
            Servers = new List<VlessServerEntry> { upstream, target }
        };

        // Act
        var activeServers = vlessConfig.GetActiveServers();

        // Assert
        Assert.Equal(2, activeServers.Count);
        Assert.Contains(activeServers, s => s.Name == "Upstream-Server" && s.OutboundId == "upstream-node-1");
        Assert.Contains(activeServers, s => s.Name == "Chained-Target" && s.OutboundId == "target-node-1");
    }

    [Fact]
    public void GetActiveServers_AutoSelectWithOrdinaryActive_ExcludesChainedTargets()
    {
        var ordinary = new VlessServerEntry
        {
            Name = "Ordinary-Active",
            Server = "1.1.1.1",
            Protocol = "vless"
        };
        var secondOrdinary = new VlessServerEntry
        {
            Name = "Ordinary-Pool",
            Server = "2.2.2.2",
            Protocol = "vless"
        };
        var chained = new VlessServerEntry
        {
            Name = "Inactive-Chained",
            Server = "3.3.3.3",
            Protocol = "vless",
            OutboundId = "target-node-1",
            DetourVia = "upstream-node-1"
        };
        var config = new VlessConfig
        {
            ActiveServer = ordinary.Name,
            AutoSelectBestServer = true,
            Servers = new List<VlessServerEntry> { ordinary, secondOrdinary, chained }
        };

        var activeServers = config.GetActiveServers();

        Assert.Equal(2, activeServers.Count);
        Assert.DoesNotContain(activeServers, server => !string.IsNullOrEmpty(server.DetourVia));
    }

    [Fact]
    public void Generate_ChainedTarget_EmitsUpstreamAndTargetProxyWithExactDetour()
    {
        // Arrange
        var upstream = new VlessServerEntry
        {
            Name = "Upstream-Node",
            Server = "100.64.0.1",
            Port = 443,
            Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "upstream-node-1"
        };

        var target = new VlessServerEntry
        {
            Name = "Target-Chained",
            Server = "194.87.222.111",
            Port = 443,
            Uuid = "3e65553e-269f-50f3-c336-78cb2b6c88f5",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "target-node-1",
            DetourVia = "upstream-node-1"
        };

        var settings = CreateSettingsWithServers(upstream, target);
        settings.Vless.ActiveServer = "Target-Chained";

        var profile = CreateTestProfile();
        var processes = new[] { "Discord.exe" };

        // Act
        var config = ConfigGenerator.Generate(profile, processes, settings);

        // Assert
        var proxy = Assert.Single(config.Outbounds.Where(o => o.Tag == "proxy"));
        Assert.Equal("proxy", config.Outbounds[0].Tag);
        Assert.Equal("chain-entry", proxy.Detour);

        using var document = System.Text.Json.JsonDocument.Parse(ConfigGenerator.Serialize(config));
        var proxyJson = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(outbound => outbound.GetProperty("tag").GetString() == "proxy");
        Assert.Equal("chain-entry", proxyJson.GetProperty("detour").GetString());

        var upstreamOutbound = Assert.Single(config.Outbounds.Where(o => o.Tag == "chain-entry"));
        Assert.Equal("vless", upstreamOutbound.Type);
        Assert.Equal("100.64.0.1", upstreamOutbound.Server);
    }

    [Fact]
    public void Generate_MissingUpstream_FailsClosedWithException()
    {
        // Arrange
        var target = new VlessServerEntry
        {
            Name = "Orphan-Target",
            Server = "194.87.222.111",
            Port = 443,
            Uuid = "3e65553e-269f-50f3-c336-78cb2b6c88f5",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "target-node-1",
            DetourVia = "missing-upstream-node-id"
        };

        var settings = CreateSettingsWithServers(target);
        settings.Vless.ActiveServer = "Orphan-Target";

        var profile = CreateTestProfile();
        var processes = new[] { "Discord.exe" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Generate(profile, processes, settings));
    }

    [Fact]
    public void Generate_ChainedXhttpTarget_FailsClosedInsteadOfUsingUpstreamDirectly()
    {
        var upstream = new VlessServerEntry
        {
            Name = "Upstream-Node",
            Server = "100.64.0.1",
            Port = 443,
            Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4",
            Protocol = "vless",
            OutboundId = "upstream-node-1"
        };
        var target = new VlessServerEntry
        {
            Name = "Unsupported-Chained-Target",
            Server = "194.87.222.111",
            Port = 443,
            Uuid = "3e65553e-269f-50f3-c336-78cb2b6c88f5",
            Protocol = "vless",
            OutboundId = "target-node-1",
            DetourVia = "upstream-node-1",
            Transport = new VlessTransportConfig { Type = "xhttp" }
        };
        var settings = CreateSettingsWithServers(upstream, target);
        settings.Vless.ActiveServer = target.Name;

        Assert.Throws<InvalidOperationException>(() =>
            ConfigGenerator.Generate(CreateTestProfile(), new[] { "Discord.exe" }, settings));
    }

    [Fact]
    public void Generate_OrdinaryNoDetourServer_ProxyOutboundHasNoDetour()
    {
        // Arrange
        var ordinaryServer = new VlessServerEntry
        {
            Name = "Ordinary-Server",
            Server = "1.2.3.4",
            Port = 443,
            Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4",
            Security = "reality",
            Reality = new VlessRealityConfig { PublicKey = "vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", ShortId = "deadbeef" },
            OutboundId = "ordinary-node-1"
        };

        var settings = CreateSettingsWithServers(ordinaryServer);
        settings.Vless.ActiveServer = "Ordinary-Server";

        var profile = CreateTestProfile();
        var processes = new[] { "Discord.exe" };

        // Act
        var config = ConfigGenerator.Generate(profile, processes, settings);

        // Assert
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        Assert.True(string.IsNullOrEmpty(proxy.Detour), "Ordinary no-detour generated outbound proxy must not have detour set");
        Assert.DoesNotContain("\"detour\"", ConfigGenerator.Serialize(config));
    }
}
