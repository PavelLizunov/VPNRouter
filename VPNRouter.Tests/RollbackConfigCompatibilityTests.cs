#nullable enable

using System;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RollbackConfigCompatibilityTests
{
    private const string LegacyTarget = "2.50.5";

    private static AppSettings Generated(VlessServerEntry active, params VlessServerEntry[] inactive)
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig { ActiveServer = active.Name },
        };
        settings.Vless.Servers.Add(active);
        settings.Vless.Servers.AddRange(inactive);
        return settings;
    }

    private static VlessServerEntry Server(
        string name,
        string host,
        string protocol = "vless") => new()
    {
        Name = name,
        Server = host,
        Port = 443,
        Uuid = "11111111-1111-1111-1111-111111111111",
        Protocol = protocol,
    };

    private static AppSettings Subscription(
        SubscriptionEntry feed,
        string activeName,
        string? activeRow = null)
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionId = feed.Id,
                ActiveSubscriptionServer = activeName,
                ActiveSubscriptionRow = activeRow,
                Subscriptions = [feed],
            },
        };
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void LegacyTelegramAutostartIsCheckedWhenEvaluatingRecovery(bool autostart, bool outdated, bool blocked)
    {
        var settings = new AppSettings();
        settings.App.AutostartTgProxy = autostart;
        var reason = RollbackConfigCompatibility.CheckActiveConfig(settings, LegacyTarget, () => outdated);
        Assert.Equal(blocked, reason == RollbackConfigCompatibility.LegacyTelegramAutostart);
    }

    [Fact]
    public void InactiveDnsttRow_DoesNotBlockACompatibleActiveRow()
    {
        var active = Server("active", "active.example");
        var inactive = Server("dnstt", "dnstt.example");
        inactive.Dnstt = new DnsttProfile { Domain = "d.example" };

        var result = RollbackConfigCompatibility.CheckActiveConfig(
            Generated(active, inactive), LegacyTarget);

        Assert.Null(result);
    }

    [Fact]
    public void DnsttSiblingInTheSameServerPool_IsRefused()
    {
        var active = Server("active", "shared.example");
        var sibling = Server("sibling", "shared.example");
        sibling.Dnstt = new DnsttProfile { Domain = "d.example" };

        Assert.Equal(RollbackConfigCompatibility.ActiveDnstt,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(active, sibling), LegacyTarget));
    }

    [Fact]
    public void ActiveDnstt_IsRefused()
    {
        var active = Server("active", "dnstt.example");
        active.Dnstt = new DnsttProfile { Domain = "d.example" };

        Assert.Equal(RollbackConfigCompatibility.ActiveDnstt,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(active), LegacyTarget));
    }

    [Fact]
    public void ActiveTurnRelayHop_IsRefused()
    {
        var active = Server("active", "relay.example");
        active.TurnRelay = new TurnRelayConfig { TurnServer = "192.0.2.10:3478" };

        Assert.Equal(RollbackConfigCompatibility.ActiveTurnRelay,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(active), LegacyTarget));

        var named = Server("active", "relay.example", "turnrelay");
        Assert.Equal(RollbackConfigCompatibility.ActiveTurnRelay,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(named), LegacyTarget));
    }

    [Fact]
    public void InactiveTurnRelayHop_DoesNotBlockACompatibleActiveRow()
    {
        var active = Server("active", "active.example");
        var hop = Server("hop", "relay.example");
        hop.TurnRelay = new TurnRelayConfig { TurnServer = "192.0.2.10:3478" };

        Assert.Null(RollbackConfigCompatibility.CheckActiveConfig(
            Generated(active, hop), LegacyTarget));
    }

    [Fact]
    public void ActiveVmessAndNewVmessFields_AreRefused()
    {
        Assert.Equal(RollbackConfigCompatibility.ActiveVmess,
            RollbackConfigCompatibility.CheckActiveConfig(
                Generated(Server("active", "vmess.example", "vmess")), LegacyTarget));

        var typed = Server("active", "active.example");
        typed.VmessSecurity = "aes-128-gcm";
        Assert.Equal(RollbackConfigCompatibility.ActiveVmess,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(typed), LegacyTarget));
    }

    [Fact]
    public void ActiveSingleOutboundPin_IsRefused()
    {
        var active = Server("active", "active.example");
        active.UseAsSingleOutbound = true;

        Assert.Equal(RollbackConfigCompatibility.ActiveSingleOutbound,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(active), LegacyTarget));
    }

    [Fact]
    public void SubscriptionExactRowBehindADuplicateName_IsAmbiguousForTheOldBuild()
    {
        var first = Server("same", "one.example");
        var second = Server("same", "two.example");
        var feed = new SubscriptionEntry
        {
            Id = "feed",
            Name = "Feed",
            Enabled = true,
            Servers = [first, second],
        };
        var settings = Subscription(feed, "same", SubscriptionEntry.RowKey(second));

        Assert.Equal(RollbackConfigCompatibility.ActiveRowAmbiguous,
            RollbackConfigCompatibility.CheckActiveConfig(settings, LegacyTarget));
    }

    [Fact]
    public void SubscriptionUniqueActiveRow_IsCompatible()
    {
        var active = Server("active", "active.example");
        var feed = new SubscriptionEntry
        {
            Id = "feed",
            Name = "Feed",
            Enabled = true,
            Servers = [active],
        };
        var settings = Subscription(feed, "active", SubscriptionEntry.RowKey(active));

        Assert.Null(RollbackConfigCompatibility.CheckActiveConfig(settings, LegacyTarget));
    }

    [Fact]
    public void DisabledActiveSubscription_IsRefused()
    {
        var feed = new SubscriptionEntry
        {
            Id = "feed",
            Name = "Feed",
            Enabled = false,
            Servers = [Server("active", "active.example")],
        };
        var settings = Subscription(feed, "active", SubscriptionEntry.RowKey(feed.Servers[0]));

        Assert.Equal(RollbackConfigCompatibility.ActiveSubscriptionDisabled,
            RollbackConfigCompatibility.CheckActiveConfig(settings, LegacyTarget));
    }

    [Fact]
    public void VerifiedDetourUsesItsUpstreamWithoutRequiringAnotherRowAtTheDestination()
    {
        var active = Server("active", "exit.example");
        active.DetourVia = "up";
        var upstream = Server("entry", "entry.example");
        upstream.OutboundId = "up";
        var feed = new SubscriptionEntry { Enabled = true, Servers = [active, upstream] };
        Assert.Null(RollbackConfigCompatibility.CheckActiveConfig(
            Subscription(feed, active.Name, SubscriptionEntry.RowKey(active)), LegacyTarget));
        Assert.Equal(RollbackConfigCompatibility.ActiveChainAmbiguous,
            RollbackConfigCompatibility.CheckActiveConfig(Generated(active), LegacyTarget));
    }

    [Fact]
    public void AmbiguousDetourChain_IsRefused()
    {
        var active = Server("active", "active.example");
        active.DetourVia = "up";
        var upstreamA = Server("up-a", "up-a.example");
        upstreamA.OutboundId = "up";
        var upstreamB = Server("up-b", "up-b.example");
        upstreamB.OutboundId = "up";
        var feed = new SubscriptionEntry
        {
            Id = "feed",
            Name = "Feed",
            Enabled = true,
            Servers = [active, upstreamA, upstreamB],
        };

        Assert.Equal(RollbackConfigCompatibility.ActiveChainAmbiguous,
            RollbackConfigCompatibility.CheckActiveConfig(
                Subscription(feed, "active", SubscriptionEntry.RowKey(active)), LegacyTarget));
    }

    [Fact]
    public void CustomConfig_ChecksOnlyTheActiveFile()
    {
        var directory = Directory.CreateTempSubdirectory("vpnrouter-custom-compat-");
        try
        {
            var vless = Path.Combine(directory.FullName, "vless.json");
            File.WriteAllText(vless, "{\"outbounds\":[{\"type\":\"vless\",\"server\":\"a.example\"}]}");
            Assert.Null(RollbackConfigCompatibility.CheckActiveConfig(
                Custom(vless), LegacyTarget));

            var dnstt = Path.Combine(directory.FullName, "dnstt.json");
            File.WriteAllText(dnstt, "{\"outbounds\":[{\"type\":\"dnstt\"}]}");
            Assert.Equal(RollbackConfigCompatibility.ActiveDnstt,
                RollbackConfigCompatibility.CheckActiveConfig(Custom(dnstt), LegacyTarget));

            var vmess = Path.Combine(directory.FullName, "vmess.json");
            File.WriteAllText(vmess, "{\"outbounds\":[{\"type\":\"vmess\"}]}");
            Assert.Equal(RollbackConfigCompatibility.ActiveVmess,
                RollbackConfigCompatibility.CheckActiveConfig(Custom(vmess), LegacyTarget));

            var broken = Path.Combine(directory.FullName, "broken.json");
            File.WriteAllText(broken, "{not json");
            Assert.Equal(RollbackConfigCompatibility.ActiveCustomUnverified,
                RollbackConfigCompatibility.CheckActiveConfig(Custom(broken), LegacyTarget));

            Assert.Equal(RollbackConfigCompatibility.ActiveCustomMissing,
                RollbackConfigCompatibility.CheckActiveConfig(
                    Custom(Path.Combine(directory.FullName, "missing.json")), LegacyTarget));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void NewerTarget_DoesNotApplyLegacyRestrictions()
    {
        var active = Server("active", "dnstt.example");
        active.Dnstt = new DnsttProfile { Domain = "d.example" };

        Assert.Null(RollbackConfigCompatibility.CheckActiveConfig(
            Generated(active), "2.51.0"));
    }

    private static AppSettings Custom(string path) => new()
    {
        App = new AppConfig
        {
            ConfigMode = "custom",
            ActiveCustomConfig = "active",
            CustomConfigs = [new CustomConfigEntry { Name = "active", Path = path }],
        },
    };
}
