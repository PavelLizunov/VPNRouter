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
}
