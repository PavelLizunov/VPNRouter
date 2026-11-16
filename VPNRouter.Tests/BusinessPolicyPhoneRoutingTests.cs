using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection(Fakes.SafeModeStateCollection.Name)]
public sealed class BusinessPolicyPhoneRoutingTests
{
    [Fact]
    public void AcceptedPhone_PrecedesManagedSitesAndEmbeddedIpRules()
    {
        var phone = Phone(new("domain_suffix", "calls.example.com", "proxy", "phone"),
            new("ip_cidr", BusinessPhonePolicy.LegacySipCidr, "proxy", "phone IP"));
        var composed = BusinessPolicyRouting.Compose(Seed(), Main().WithPhone(phone));
        Assert.Equal(BusinessPolicyDomains.PortalDomain, composed[0].Value);
        Assert.Equal(new[] { "calls.example.com", BusinessPhonePolicy.LegacySipCidr }, composed.Skip(1).Take(2).Select(r => r.Value));
        Assert.Equal("proxy", Assert.Single(composed, r => r.Value == BusinessPhonePolicy.LegacySipCidr).Action);
        Assert.True(composed.FindIndex(r => r.Value == "example.com") > 2);
        Assert.InRange(composed.FindIndex(r => r.Value == "198.51.100.0/24"), 3, composed.FindIndex(r => r.Value == "example.com") - 1);
        Assert.Equal("proxy", Assert.Single(composed, r => r.Type == "process_name").Action);
        Assert.DoesNotContain(composed, r => BusinessPolicyDomains.ProtectedDirectDomains.Contains(r.Value));
    }

    [Fact]
    public void AcceptedEmpty_RemovesExactlyPhoneDefaultsAndKeepsUnrelatedRules()
    {
        var absent = BusinessPolicyRouting.Compose(Seed(), Main());
        var empty = BusinessPolicyRouting.Compose(Seed(), Main().WithPhone(Phone()));
        Assert.All(BusinessPolicyDomains.ProtectedDirectDomains, domain => Assert.Contains(absent, r => r.Value == domain));
        Assert.Contains(absent, r => r.Value == BusinessPhonePolicy.LegacySipCidr);
        Assert.DoesNotContain(empty, r => BusinessPolicyDomains.ProtectedDirectDomains.Contains(r.Value) || r.Value == BusinessPhonePolicy.LegacySipCidr);
        Assert.Contains(empty, r => r.Type == "ip_cidr" && r.Value == "198.51.100.0/24");
        Assert.Contains(empty, r => r.Type == "ip_cidr" && r.Value == "195.211.120.0/24");
        Assert.Contains(empty, r => r.Value == "example.com");
        Assert.Contains(empty, r => r.Value == "App.exe");
        Assert.Equal("proxy", Assert.Single(empty, r => r.Value == BusinessPolicyDomains.PortalDomain).Action);
        Assert.DoesNotContain(empty, r => r.Value == "seed.example.net");
    }

    [Theory]
    [InlineData("domain_suffix", "uiscom.ru")]
    [InlineData("ip_cidr", "195.211.120.35/32")]
    [InlineData("ip_cidr", "2001:db8::/32")]
    public void ProxyPhoneDestination_UsesUdpSiblingAndKeepsTcpFallback(string type, string value)
    {
        var config = new SingBoxConfig();
        config.Route.Rules = [ConfigGenerator.BuildCustomRouteRule(new CustomRule
            { Type = type, Value = value, Action = "proxy", Enabled = true })!];
        BusinessDefaults.ApplyDomainTransports(config, true, false, true);
        Assert.Equal(2, config.Route.Rules.Count);
        var udp = config.Route.Rules[0];
        Assert.Equal("udp", udp.Network);
        Assert.Equal("proxy-udp", udp.Outbound);
        Assert.Equal(value, Assert.Single(type == "ip_cidr" ? udp.IpCidr! : udp.DomainSuffix!));
        Assert.Equal("proxy", config.Route.Rules[1].Outbound);
        Assert.Null(config.Route.Rules[1].Network);
        Assert.DoesNotContain(config.Route.Rules, r => r.Action == "reject");
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 3)]
    public void ProxyPhoneIp_RespectsNativeUdpAndQuicFallback(bool nativeUdp, bool blockQuic, int expectedRules)
    {
        var config = new SingBoxConfig();
        config.Route.Rules = [new RouteRule { IpCidr = ["195.211.120.35/32"], Action = "route", Outbound = "proxy" }];
        BusinessDefaults.ApplyDomainTransports(config, false, nativeUdp, blockQuic);
        Assert.Equal(expectedRules, config.Route.Rules.Count);
        Assert.Equal("proxy", config.Route.Rules[^1].Outbound);
        if (expectedRules == 3)
        {
            Assert.Equal("reject", config.Route.Rules[0].Action);
            Assert.Equal("udp", config.Route.Rules[0].Network);
            Assert.Equal(new[] { 443 }, config.Route.Rules[0].Port);
            Assert.Equal("quic", config.Route.Rules[1].Protocol);
            Assert.All(config.Route.Rules, r => Assert.Equal(new[] { "195.211.120.35/32" }, r.IpCidr));
        }
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void AcceptedPhoneIp_GeneratesProxyRoutesWithoutAddingLegacyPhysicalExclusion(string mode)
    {
        var settings = Settings(Phone(new("ip_cidr", BusinessPhonePolicy.LegacySipCidr, "proxy", "")));
        settings.App.RoutingMode = mode;
        settings.Tun.RouteExcludeAddress.Add("198.51.100.10/32");
        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        if (!ProductEdition.IsBusiness)
            BusinessDefaults.ApplyDomainTransports(config, true, false, true);
        var rules = config.Route.Rules.Where(r => r.IpCidr?.Contains(BusinessPhonePolicy.LegacySipCidr) == true).ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Equal("proxy-udp", rules[0].Outbound);
        Assert.Equal("proxy", rules[1].Outbound);
        Assert.DoesNotContain(BusinessPhonePolicy.LegacySipCidr, config.Inbounds[0].RouteExcludeAddress!);
        Assert.Contains("198.51.100.10/32", config.Inbounds[0].RouteExcludeAddress!);
    }

    [Theory]
    [InlineData("192.168.1.0/24", false)]
    [InlineData("192.0.2.0/24", true)]
    public void ProxyPhone_IntersectingMandatoryOrNewlyDetectedPhysicalExclusionFailsBeforeLaunch(string value, bool detected)
    {
        var settings = Settings(Phone(new("ip_cidr", value, "proxy", "")));
        if (detected) settings.Tun.AutoDetectedExcludeAddress.Add("192.0.2.23");
        var error = Assert.Throws<InvalidDataException>(() => ConfigGenerator.Generate(new Profile(), [], settings));
        Assert.Equal("Business telephony proxy conflicts with a required physical route.", error.Message);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("proxy")]
    public void Ipv6Phone_FailsExplicitlyUntilTunnelCanCaptureIpv6(string action)
    {
        var settings = Settings(Phone(new("ip_cidr", "2001:db8::/32", action, "")));
        settings.Tun.Ipv6Enabled = true;
        var error = Assert.Throws<InvalidDataException>(() => ConfigGenerator.Generate(new Profile(), [], settings));
        Assert.Equal("Business telephony IPv6 routes require IPv6 tunnel support.", error.Message);
    }

    private static AppSettings Settings(BusinessPhonePolicySnapshot phone)
    {
        var policy = Main().WithPhone(phone);
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SiblingConnection, Seed(), policy);
        settings.BusinessPolicy = policy;
        settings.App.BypassRussianTraffic = false;
        return settings;
    }

    private static BusinessPolicySnapshot Main() => new("corp-test", "key", 7, 1, false,
        [new("example.com", "direct", "managed")], "main-digest");

    private static BusinessPhonePolicySnapshot Phone(params BusinessPhonePolicyRule[] rules) =>
        new("corp-test", "key", 1, 1, true, rules, "phone-digest");

    private static List<CustomRule> Seed() =>
    [
        .. BusinessPolicyDomains.ProtectedDirectDomains.Select(domain => new CustomRule
            { Type = "domain_suffix", Value = domain, Action = "direct", Enabled = true }),
        new() { Type = "domain_suffix", Value = "seed.example.net", Action = "proxy", Enabled = true },
        new() { Type = "ip_cidr", Value = BusinessPhonePolicy.LegacySipCidr, Action = "direct", Enabled = true },
        new() { Type = "ip_cidr", Value = "198.51.100.0/24", Action = "direct", Enabled = true },
        new() { Type = "ip_cidr", Value = "195.211.120.0/24", Action = "direct", Enabled = true },
        new() { Type = "process_name", Value = "App.exe", Action = "proxy", Enabled = true }
    ];
}
