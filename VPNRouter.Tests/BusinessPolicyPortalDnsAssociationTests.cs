using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection(Fakes.SafeModeStateCollection.Name)]
public sealed class BusinessPolicyPortalDnsAssociationTests
{
    [Theory]
    [InlineData("split", true)]
    [InlineData("full", true)]
    [InlineData("split", false)]
    [InlineData("full", false)]
    public void BusinessPortal_WireRetainsDnsAssociationsAndProtectedTransport(string mode, bool acceptedPolicy)
    {
        var settings = Settings(mode, acceptedPolicy);
        var selected = settings.Vless.Servers[0];
        settings.Vless.Servers.Add(new VlessServerEntry
        {
            Name = "Same host sibling", Server = selected.Server, Port = 8443,
            Protocol = "vless", Uuid = "sibling-test-user", Security = "tls",
            Transport = new() { Type = "ws", Path = "/sibling" }
        });

        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        if (!ProductEdition.IsBusiness)
        {
            BusinessDefaults.ApplyDns(config, settings.App.CustomRules);
            BusinessDefaults.ApplyDomainTransports(config, config.Outbounds.Any(o => o.Tag == "proxy-udp"),
                false, settings.App.BlockQuicOnTcpProxy);
            ConfigGenerator.ApplyBusinessPortal(config, settings);
        }

        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        var root = document.RootElement;
        var dns = root.GetProperty("dns");
        Assert.Equal(JsonValueKind.True, dns.GetProperty("reverse_mapping").ValueKind);

        var outbound = Assert.Single(root.GetProperty("outbounds").EnumerateArray(),
            o => HasString(o, "tag", ConfigGenerator.BusinessPortalOutbound));
        Assert.Equal("vless", outbound.GetProperty("type").GetString());
        Assert.Equal(selected.Server, outbound.GetProperty("server").GetString());
        Assert.Equal(selected.Port, outbound.GetProperty("server_port").GetInt32());
        Assert.Equal(selected.Uuid, outbound.GetProperty("uuid").GetString());
        Assert.False(outbound.TryGetProperty("detour", out _));
        Assert.False(outbound.TryGetProperty("outbounds", out _));

        var portalDns = Assert.Single(dns.GetProperty("servers").EnumerateArray(),
            s => HasString(s, "tag", ConfigGenerator.BusinessPortalDns));
        Assert.Equal("https", portalDns.GetProperty("type").GetString());
        Assert.Equal(ConfigGenerator.BusinessPortalOutbound, portalDns.GetProperty("detour").GetString());
        var bootstrap = portalDns.GetProperty("domain_resolver");
        Assert.Equal("local-dns", bootstrap.ValueKind == JsonValueKind.String
            ? bootstrap.GetString() : bootstrap.GetProperty("server").GetString());
        Assert.Equal(ConfigGenerator.BusinessPortalDns,
            dns.GetProperty("rules").EnumerateArray().First(IsPortalRule).GetProperty("server").GetString());

        var rules = root.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        var guardIndex = Array.FindIndex(rules, r => IsPortalRule(r) && HasString(r, "network", "udp") &&
            HasString(r, "action", "reject"));
        var routeIndex = Array.FindIndex(rules, r => IsPortalRule(r) &&
            HasString(r, "outbound", ConfigGenerator.BusinessPortalOutbound));
        Assert.True(guardIndex >= 0, "Portal DNS associations must feed a portal UDP rejection rule.");
        Assert.True(routeIndex > guardIndex, "Portal UDP rejection must precede the portal transport route.");
        Assert.False(rules[guardIndex].TryGetProperty("port", out _));
        Assert.False(rules[guardIndex].TryGetProperty("outbound", out _));
        Assert.DoesNotContain(rules, r => IsPortalRule(r) &&
            (HasString(r, "outbound", "proxy") || HasString(r, "outbound", "proxy-udp")));
    }

    [Fact]
    public void DefaultDns_WireOmitsUnconfiguredAssociationFlag()
    {
        var config = new SingBoxConfig();
        Assert.Null(config.Dns.ReverseMapping);
        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        Assert.False(document.RootElement.GetProperty("dns").TryGetProperty("reverse_mapping", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DnsAssociationFlag_WirePreservesExplicitBoolean(bool enabled)
    {
        var config = new SingBoxConfig();
        config.Dns.ReverseMapping = enabled;
        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        Assert.Equal(enabled, document.RootElement.GetProperty("dns").GetProperty("reverse_mapping").GetBoolean());
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void StandardGenerator_WireOmitsAssociationFlag(string mode)
    {
        if (ProductEdition.IsBusiness) return;
        var config = ConfigGenerator.Generate(new Profile(), [], Settings(mode, acceptedPolicy: false));
        Assert.Null(config.Dns.ReverseMapping);
        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        Assert.False(document.RootElement.GetProperty("dns").TryGetProperty("reverse_mapping", out _));
    }

    private static AppSettings Settings(string mode, bool acceptedPolicy)
    {
        var settings = new AppSettings();
        if (acceptedPolicy)
            BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules(),
                new BusinessPolicySnapshot("corp-test", "test-key", 1, 1, false,
                    [new BusinessPolicyRule("example.com", "proxy", "")], new string('A', 64)));
        else
            BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules());
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false;
        settings.App.BlockQuicOnTcpProxy = false;
        return settings;
    }

    private static bool HasString(JsonElement element, string name, string value) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() == value;

    private static bool IsPortalRule(JsonElement rule) =>
        rule.TryGetProperty("domain_suffix", out var domains) &&
        domains.EnumerateArray().Any(domain => domain.GetString() == BusinessPolicyDomains.PortalDomain);
}
