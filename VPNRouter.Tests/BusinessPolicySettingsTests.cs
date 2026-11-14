using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VPNRouter.Tests;

public sealed class BusinessPolicySettingsTests
{
    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void Capture_IsolatesRunningSessionFromLaterSettingsAndSeedChanges(string mode)
    {
        var source = new AppSettings();
        source.App.RoutingMode = mode;
        source.App.ConfigMode = "subscribe";
        source.App.SubscriptionUrl = "https://example.invalid/subscription";
        source.App.Subscriptions = [new() { Name = "Corp", Servers = [Server()] }];
        source.App.SubscriptionServers = [Server()];
        source.Vless.ActiveServer = "Selected";
        source.Vless.Servers = [Server()];
        source.Vless.Transport.Headers = new(StringComparer.OrdinalIgnoreCase) { ["X-Test"] = "before" };
        source.Tun.Mtu = 1300;
        source.Tun.RouteExcludeAddress = ["198.51.100.0/24"];
        source.Tun.AutoDetectedExcludeAddress = ["203.0.113.0/24"];
        source.CustomGroupApps = new(StringComparer.OrdinalIgnoreCase) { ["Office"] = ["Office.exe"] };
        source.CustomCategories = [new() { Name = "Office", Apps = ["Editor.exe"] }];
        source.ProfileSources = [new() { Type = "local", Path = "profiles-before.json" }];
        source.App.CustomRules = [new() { Value = "stale.example.com", Action = "direct" }];
        var seed = Seed();
        var policy = Policy(new BusinessPolicyRule("example.org", "proxy", "Managed"));

        var captured = BusinessPolicySettings.Capture(source, seed, policy);
        Assert.Same(policy, captured.BusinessPolicy);
        Assert.Equal(mode, captured.App.RoutingMode);
        Assert.Equal("subscribe", captured.App.ConfigMode);
        Assert.Equal("Selected", captured.Vless.ActiveServer);
        Assert.Equal("https://example.invalid/subscription", captured.App.SubscriptionUrl);
        Assert.Equal("stale.example.com", Assert.Single(source.App.CustomRules).Value);
        Assert.NotSame(source.Vless.Servers[0], captured.Vless.Servers[0]);
        Assert.NotSame(source.Vless.Servers[0].Reality, captured.Vless.Servers[0].Reality);

        source.App.RoutingMode = "changed";
        source.Vless.ActiveServer = "Other";
        source.Vless.Servers[0].Reality.ServerName = "changed.invalid";
        source.Vless.Servers[0].Tls.ServerName = "changed.invalid";
        source.Vless.Servers[0].DetourVia = "changed-upstream";
        source.Vless.Servers[0].Transport.Headers["X-Test"] = "changed";
        source.Vless.Servers[0].Awg!.Address[0] = "198.51.100.20/32";
        source.Vless.Servers[0].DnsResolvers[0] = "203.0.113.2:53";
        source.App.Subscriptions[0].Servers[0].Reality.ServerName = "changed.invalid";
        source.App.SubscriptionServers[0].Transport.Headers["X-Test"] = "changed";
        source.Vless.Transport.Headers["X-Test"] = "changed";
        source.Tun.RouteExcludeAddress.Clear();
        source.Tun.AutoDetectedExcludeAddress.Clear();
        source.CustomGroupApps["Office"].Clear();
        source.CustomCategories[0].Apps.Clear();
        source.ProfileSources[0].Path = "profiles-after.json";
        seed[1].Value = "Changed.exe";

        Assert.Equal(mode, captured.App.RoutingMode);
        Assert.Equal("Selected", captured.Vless.ActiveServer);
        Assert.Equal("original.invalid", captured.Vless.Servers[0].Reality.ServerName);
        Assert.Equal("tls.invalid", captured.Vless.Servers[0].Tls.ServerName);
        Assert.Equal("upstream", captured.Vless.Servers[0].DetourVia);
        Assert.Equal("before", captured.Vless.Servers[0].Transport.Headers["x-test"]);
        Assert.Equal("192.0.2.20/32", Assert.Single(captured.Vless.Servers[0].Awg!.Address));
        Assert.Equal("192.0.2.53:53", Assert.Single(captured.Vless.Servers[0].DnsResolvers));
        Assert.True(captured.Vless.Servers[0].IsDnsTunnel);
        Assert.Equal("original.invalid", captured.App.Subscriptions[0].Servers[0].Reality.ServerName);
        Assert.Equal("before", captured.App.SubscriptionServers[0].Transport.Headers["x-test"]);
        Assert.Equal("before", captured.Vless.Transport.Headers["x-test"]);
        Assert.Equal(1300, captured.Tun.Mtu);
        Assert.Equal("198.51.100.0/24", Assert.Single(captured.Tun.RouteExcludeAddress));
        Assert.Equal("203.0.113.0/24", Assert.Single(captured.Tun.AutoDetectedExcludeAddress));
        Assert.Equal("Office.exe", Assert.Single(captured.CustomGroupApps["office"]));
        Assert.Equal("Editor.exe", Assert.Single(captured.CustomCategories[0].Apps));
        Assert.Equal("profiles-before.json", captured.ProfileSources[0].Path);
        Assert.Equal(new[] { "Telegram.exe", "Discord.exe" }, captured.App.RoutingAppsInclude);
        Assert.Equal("proxy", Assert.Single(captured.App.CustomRules, r => r.Value == "example.org").Action);

        var second = BusinessPolicySettings.Capture(captured, Seed(), policy);
        Assert.Single(second.App.CustomRules, r => r.Value == "example.org").Action = "direct";
        second.CustomGroupApps["Office"].Clear();
        Assert.Equal("proxy", Assert.Single(captured.App.CustomRules, r => r.Value == "example.org").Action);
        Assert.Single(captured.CustomGroupApps["Office"]);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void EnforceRules_EmptyPolicyRemovesSitesAndRestoresProtectedProcessRules(string mode)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = mode;
        settings.Vless.ActiveServer = "Selected";
        settings.ActiveProfile = "stale-profile";
        settings.CustomApps = ["Unmanaged.exe"];
        settings.ExcludedApps = ["Telegram.exe"];
        settings.App.RoutingAppsMode = "exclude";
        settings.App.RoutingAppsExclude = ["Telegram.exe"];
        settings.App.RoutingAppsInclude = ["Unmanaged.exe"];
        settings.App.CustomRules = [new() { Value = "example.com" }];
        var empty = Policy();

        BusinessPolicySettings.EnforceRules(settings, Seed(), empty);

        Assert.Same(empty, settings.BusinessPolicy);
        Assert.Equal(mode, settings.App.RoutingMode);
        Assert.Equal("Selected", settings.Vless.ActiveServer);
        Assert.Empty(settings.ActiveProfile);
        Assert.Empty(settings.CustomApps);
        Assert.Empty(settings.ExcludedApps);
        Assert.Equal("include", settings.App.RoutingAppsMode);
        Assert.True(settings.App.RoutingAppsIncludeInitialized);
        Assert.Empty(settings.App.RoutingAppsExclude);
        Assert.Equal(new[] { "Telegram.exe", "Discord.exe" }, settings.App.RoutingAppsInclude);
        Assert.Equal("custom_first", settings.App.CustomRulesPriority);
        Assert.DoesNotContain(settings.App.CustomRules, r => r.Value == "example.com");
        Assert.DoesNotContain(settings.App.CustomRules, r => r.Type == "process_name" && r.Action == "proxy");
        Assert.Contains(settings.App.CustomRules, r => r.Type == "process_name" && r.Value == "SoftPhone.exe" && r.Action == "direct");
        Assert.Contains(settings.App.CustomRules, r => r.Type == "ip_cidr" && r.Value == "192.0.2.10/32" && r.Action == "direct");
        Assert.All(BusinessPolicyDomains.ProtectedDirectDomains, domain =>
            Assert.Equal("direct", Assert.Single(settings.App.CustomRules, r => r.Value == domain).Action));
        Assert.Equal("proxy", Assert.Single(settings.App.CustomRules, r => r.Value == BusinessPolicyDomains.PortalDomain).Action);
    }

    [Fact]
    public void Capture_WithoutAcceptedPolicy_UsesSeedInsteadOfEditableOrStaleMetadata()
    {
        var settings = new AppSettings
        {
            BusinessPolicy = Policy(new BusinessPolicyRule("other.example.org", "proxy", "Stale metadata"))
        };
        settings.App.CustomRules = [new() { Value = "other.example.org", Action = "proxy" }];

        var captured = BusinessPolicySettings.Capture(settings, Seed(), null);

        Assert.Null(captured.BusinessPolicy);
        Assert.Contains(captured.App.CustomRules, r => r.Value == "example.com" && r.Action == "direct");
        Assert.DoesNotContain(captured.App.CustomRules, r => r.Value == "other.example.org");
        Assert.NotNull(settings.BusinessPolicy);
    }

    [Fact]
    public void PolicyMetadata_IsNeverSerializedOrRestoredFromEditableSettings()
    {
        var settings = new AppSettings { BusinessPolicy = Policy() };
        var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        using var parsedJson = JsonDocument.Parse(json);
        Assert.False(parsedJson.RootElement.TryGetProperty("BusinessPolicy", out _));
        Assert.Null(JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!.BusinessPolicy);
        Assert.Null(JsonSerializer.Deserialize("{\"BusinessPolicy\":{\"Revision\":999}}",
            AppJsonContext.Default.AppSettings)!.BusinessPolicy);

        var serializer = new StaticSerializerBuilder(new YamlStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
        var yaml = serializer.Serialize(settings);
        Assert.DoesNotContain("business_policy", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SettingsLoader.Parse(yaml + "\nbusiness_policy:\n  revision: 999\n").BusinessPolicy);
    }

    [Fact]
    public void EnforceRules_InvalidPolicyDoesNotPartiallyMutateSettings()
    {
        var settings = new AppSettings { ActiveProfile = "Existing" };
        var originalRules = settings.App.CustomRules;
        var originalApps = settings.App.RoutingAppsInclude;
        var invalid = Policy(new BusinessPolicyRule(BusinessPolicyDomains.PortalDomain, "direct", "Forbidden"));

        Assert.Throws<InvalidDataException>(() => BusinessPolicySettings.EnforceRules(settings, Seed(), invalid));

        Assert.Equal("Existing", settings.ActiveProfile);
        Assert.Same(originalRules, settings.App.CustomRules);
        Assert.Same(originalApps, settings.App.RoutingAppsInclude);
        Assert.Null(settings.BusinessPolicy);
    }

    private static List<CustomRule> Seed() =>
    [
        new() { Type = "domain_suffix", Value = "example.com", Action = "direct" },
        new() { Type = "process_name", Value = "Telegram.exe, telegram.exe,Discord.exe", Action = "proxy" },
        new() { Type = "process_name", Value = "SoftPhone.exe", Action = "direct" },
        new() { Type = "ip_cidr", Value = "192.0.2.10/32", Action = "direct" }
    ];

    private static VlessServerEntry Server() => new()
    {
        Name = "Selected", Server = "192.0.2.1", OutboundId = "target", DetourVia = "upstream",
        Reality = new() { ServerName = "original.invalid" },
        Tls = new() { ServerName = "tls.invalid" },
        Transport = new() { Headers = new(StringComparer.OrdinalIgnoreCase) { ["X-Test"] = "before" } },
        Awg = new() { Address = ["192.0.2.20/32"] },
        DnsResolvers = ["192.0.2.53:53"]
    };

    private static BusinessPolicySnapshot Policy(params BusinessPolicyRule[] rules) =>
        new("corp-fixture", "fixture-key", 7, 1794657600, false, rules, new string('A', 64));
}
