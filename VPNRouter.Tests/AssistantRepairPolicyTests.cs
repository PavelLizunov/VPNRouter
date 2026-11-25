using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AssistantRepairPolicyTests
{
    [Theory]
    [InlineData(575, 1420)]
    [InlineData(1501, 1420)]
    [InlineData(0, 1420)]
    [InlineData(-1, 1420)]
    [InlineData(int.MinValue, 1420)]
    [InlineData(int.MaxValue, 1420)]
    public void SuggestMtu_normalizes_an_invalid_stored_mtu_to_the_default(int storedMtu, int expected)
    {
        var tun = new TunSettings { Mtu = storedMtu };

        AssertSuggests(expected, tun, probe: null);
    }

    [Theory]
    [InlineData(1420, 1380, 1380)]
    [InlineData(1420, 1332, 1332)]
    [InlineData(1400, 1399, 1399)]
    [InlineData(1500, 1420, 1420)]
    [InlineData(1500, 1332, 1332)]
    public void SuggestMtu_lowers_a_valid_manual_mtu_to_a_trustworthy_smaller_payload(
        int storedMtu, int payload, int expected)
    {
        var tun = new TunSettings { Mtu = storedMtu };

        AssertSuggests(expected, tun, Probe(payload));
    }

    [Theory]
    [InlineData(1420, 1331)]
    [InlineData(1420, 1501)]
    [InlineData(1400, 1400)]
    [InlineData(1400, 1450)]
    [InlineData(1500, 1500)]
    [InlineData(576, 1332)]
    public void SuggestMtu_keeps_a_valid_manual_mtu_without_a_lower_trustworthy_payload(
        int storedMtu, int payload)
    {
        var tun = new TunSettings { Mtu = storedMtu };

        Assert.Null(AssistantRepairPolicy.SuggestMtu(tun, Probe(payload)));
    }

    [Theory]
    [InlineData(576)]
    [InlineData(1400)]
    [InlineData(1500)]
    public void SuggestMtu_keeps_a_valid_manual_mtu_without_a_probe(int storedMtu)
    {
        var tun = new TunSettings { Mtu = storedMtu };

        Assert.Null(AssistantRepairPolicy.SuggestMtu(tun, null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SuggestMtu_ignores_a_probe_with_an_untrusted_signal(
        bool plainPingBlocked, bool skippedTunnelUp)
    {
        var tun = new TunSettings { Mtu = 1420 };

        Assert.Null(AssistantRepairPolicy.SuggestMtu(tun, Probe(1332, plainPingBlocked, skippedTunnelUp)));
    }

    [Fact]
    public void SuggestMtu_ignores_a_probe_without_a_payload()
    {
        var tun = new TunSettings { Mtu = 1420 };

        Assert.Null(AssistantRepairPolicy.SuggestMtu(tun, Probe(null)));
    }

    [Theory]
    [InlineData(575, 1380, 1380)]
    [InlineData(1501, 1332, 1332)]
    [InlineData(0, 1400, 1400)]
    [InlineData(575, 1420, 1420)]
    [InlineData(575, 1440, 1420)]
    [InlineData(575, 1500, 1420)]
    [InlineData(int.MaxValue, 1420, 1420)]
    public void SuggestMtu_normalizes_before_applying_a_trustworthy_payload(
        int storedMtu, int payload, int expected)
    {
        AssertSuggests(expected, new TunSettings { Mtu = storedMtu }, Probe(payload));
    }

    [Theory]
    [InlineData(575, 1331, false, false)]
    [InlineData(575, 1501, false, false)]
    [InlineData(575, null, false, false)]
    [InlineData(575, 1332, true, false)]
    [InlineData(1501, 1332, false, true)]
    [InlineData(0, null, true, true)]
    public void SuggestMtu_falls_back_to_default_for_an_invalid_mtu_without_a_trustworthy_payload(
        int storedMtu, int? payload, bool plainPingBlocked, bool skippedTunnelUp)
    {
        var tun = new TunSettings { Mtu = storedMtu };

        AssertSuggests(TunSettings.DefaultMtu, tun, Probe(payload, plainPingBlocked, skippedTunnelUp));
    }

    [Fact]
    public void SuggestMtu_does_not_mutate_an_invalid_stored_mtu()
    {
        var tun = new TunSettings { Mtu = 500, InterfaceName = "VPNRouter-Test", Ipv6Enabled = true };

        AssertSuggests(1332, tun, Probe(1332));

        Assert.Equal(500, tun.Mtu);
        Assert.Equal("VPNRouter-Test", tun.InterfaceName);
        Assert.True(tun.Ipv6Enabled);
    }

    [Fact]
    public void SuggestMtu_does_not_mutate_a_valid_manual_mtu()
    {
        var tun = new TunSettings { Mtu = 1400 };

        AssertSuggests(1332, tun, Probe(1332));

        Assert.Equal(1400, tun.Mtu);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("manual")]
    public void FindRoutingIssue_treats_unsupported_config_modes_as_custom_configuration(string configMode)
    {
        var settings = Settings(configMode: configMode);

        Assert.Equal(
            AssistantRepairRoutingIssue.CustomConfiguration,
            AssistantRepairPolicy.FindRoutingIssue(settings, hasEffectiveInclude: false));
    }

    [Theory]
    [InlineData("generated", "split", "include", true, false, (int)AssistantRepairRoutingIssue.EmptyInclude)]
    [InlineData("subscribe", "split", "include", true, false, (int)AssistantRepairRoutingIssue.EmptyInclude)]
    [InlineData("generated", "split", "include", true, true, (int)AssistantRepairRoutingIssue.None)]
    [InlineData("generated", "full", "include", true, false, (int)AssistantRepairRoutingIssue.None)]
    [InlineData("generated", "full", "include", true, true, (int)AssistantRepairRoutingIssue.None)]
    [InlineData("generated", "split", "exclude", true, false, (int)AssistantRepairRoutingIssue.None)]
    [InlineData("generated", "split", "exclude", true, true, (int)AssistantRepairRoutingIssue.None)]
    [InlineData("generated", "split", "include", false, false, (int)AssistantRepairRoutingIssue.AutoRouteDisabled)]
    [InlineData("generated", "split", "include", false, true, (int)AssistantRepairRoutingIssue.AutoRouteDisabled)]
    [InlineData("subscribe", "full", "include", false, false, (int)AssistantRepairRoutingIssue.AutoRouteDisabled)]
    [InlineData("custom", "split", "include", true, false, (int)AssistantRepairRoutingIssue.CustomConfiguration)]
    [InlineData("custom", "split", "include", false, false, (int)AssistantRepairRoutingIssue.CustomConfiguration)]
    [InlineData("manual", "full", "exclude", false, false, (int)AssistantRepairRoutingIssue.CustomConfiguration)]
    public void FindRoutingIssue_applies_mode_auto_route_and_include_priority(
        string configMode, string routingMode, string routingAppsMode, bool autoRoute,
        bool hasEffectiveInclude, int expected)
    {
        var settings = Settings(configMode, routingMode, routingAppsMode, autoRoute);

        Assert.Equal((AssistantRepairRoutingIssue)expected, AssistantRepairPolicy.FindRoutingIssue(settings, hasEffectiveInclude));
    }

    [Fact]
    public void FindRoutingIssue_uses_the_generated_split_include_defaults()
    {
        var settings = new AppSettings();

        Assert.Equal(
            AssistantRepairRoutingIssue.EmptyInclude,
            AssistantRepairPolicy.FindRoutingIssue(settings, hasEffectiveInclude: false));
        Assert.Equal(
            AssistantRepairRoutingIssue.None,
            AssistantRepairPolicy.FindRoutingIssue(settings, hasEffectiveInclude: true));
    }

    [Fact]
    public void FindRoutingIssue_does_not_mutate_the_settings()
    {
        var settings = Settings(configMode: "custom", autoRoute: false);

        _ = AssistantRepairPolicy.FindRoutingIssue(settings, hasEffectiveInclude: false);

        Assert.Equal("custom", settings.App.ConfigMode);
        Assert.Equal("split", settings.App.RoutingMode);
        Assert.Equal("include", settings.App.RoutingAppsMode);
        Assert.False(settings.Tun.AutoRoute);
    }

    private static AppSettings Settings(
        string configMode = "generated",
        string routingMode = "split",
        string routingAppsMode = "include",
        bool autoRoute = true)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = configMode;
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = routingAppsMode;
        settings.Tun.AutoRoute = autoRoute;
        return settings;
    }

    private static HealthCheck.PathMtuProbeResult Probe(
        int? bestPayload, bool plainPingBlocked = false, bool skippedTunnelUp = false)
        => new(bestPayload, plainPingBlocked, skippedTunnelUp);

    private static void AssertSuggests(
        int expected, TunSettings tun, HealthCheck.PathMtuProbeResult? probe)
    {
        var suggestion = AssistantRepairPolicy.SuggestMtu(tun, probe);
        Assert.NotNull(suggestion);
        Assert.Equal(expected, suggestion.Value);
    }
}
