using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AssistantRoutePolicyTests
{
    [Theory]
    [InlineData("""{"route":{"final":"direct","rules":[{"action":"hijack-dns"},{"outbound":"proxy","process_name":["chrome.exe"]}]}}""", true)]
    [InlineData("""{"route":{"final":"proxy","rules":[{"outbound":"proxy","process_name":["chrome.exe"]}]}}""", false)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy","process_name":["chrome.exe","other.exe"]}]}}""", false)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy","process_name":["chrome.exe"]},{"outbound":"proxy"}]}}""", false)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy-udp","process_name":["chrome.exe"]}]}}""", false)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy","process_name":["CHROME.EXE"]}]}}""", false)]
    public void Route_evidence_requires_an_exclusive_primary_proxy_rule(string config, bool expected)
    {
        var tunnel = new AcceptedTunnelInfo(new AppSettings(), config, "", null,
            new Dictionary<string, VlessServerEntry>());

        Assert.Equal(expected, tunnel.RoutesOnlyApp("chrome.exe"));
    }

    [Theory]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy","process_name":["Work.exe"]}]}}""", true)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy","process_name":["Browser.exe"]}]}}""", false)]
    [InlineData("""{"route":{"final":"proxy","rules":[{"outbound":"direct","process_name":["Browser.exe"]}]}}""", true)]
    [InlineData("""{"route":{"final":"proxy","rules":[{"outbound":"direct","process_name":["Browser.exe"],"network":"udp"}]}}""", false)]
    [InlineData("""{"route":{"final":"direct","rules":[{"outbound":"proxy"}]}}""", false)]
    public void Direct_proof_checks_order_scope_and_final_outbound(string config, bool expected)
    {
        var tunnel = new AcceptedTunnelInfo(new AppSettings(), config, "", null, new Dictionary<string, VlessServerEntry>());
        // On macOS a generated per-app policy must cover helper names as well.
        if (OperatingSystem.IsMacOS() && config.Contains("Browser.exe") && expected)
            expected = ConfigGenerator.ExpandMacHelperNames(["Browser.exe"]).Count == 1;
        Assert.Equal(expected, tunnel.RoutesAppDirect("Browser.exe"));
    }

    [Fact]
    public void Direct_route_preserves_unrelated_processes_and_freezes_legacy_include_selection()
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsIncludeInitialized = false;
        settings.App.RoutingAppsExclude = ["Dormant.exe"];
        MainWindowViewModel.ConfigureAssistantRoute(settings, "Browser.exe", true, ["Browser.exe", "Work.exe"]);
        Assert.Equal(new[] { "Work.exe" }, settings.App.RoutingAppsInclude);
        Assert.Equal(new[] { "Dormant.exe" }, settings.App.RoutingAppsExclude);
        Assert.True(settings.App.RoutingAppsIncludeInitialized);
    }

    [Fact]
    public void Direct_route_in_exclude_mode_keeps_existing_bypasses_and_inactive_include_list()
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "exclude";
        settings.App.RoutingAppsExclude = ["Work.exe"];
        settings.App.RoutingAppsInclude = ["Dormant.exe"];
        MainWindowViewModel.ConfigureAssistantRoute(settings, "Browser.exe", true, []);
        MainWindowViewModel.ConfigureAssistantRoute(settings, "Browser.exe", true, []);
        Assert.Equal(new[] { "Work.exe", "Browser.exe" }, settings.App.RoutingAppsExclude);
        Assert.Equal(new[] { "Dormant.exe" }, settings.App.RoutingAppsInclude);
    }
}
