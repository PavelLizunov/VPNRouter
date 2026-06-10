using VPNRouter.App;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.x — App-layer surface for dns-tunnel (slipstream) servers: a pasted
/// dns-tunnel:// link is recognised as a server URI (not a subscription / junk),
/// and the server list shows a "dns-tunnel" subtitle so the last-resort transport
/// is visually distinct. See plans/dns-tunnel-slipstream-integration-2026-06-10.md.
/// </summary>
public class DnsTunnelUiTests
{
    [Fact]
    public void Classify_DnsTunnelLink_IsServerUri()
        => Assert.Equal(SmpInputKind.ServerUri,
               SimpleInputDetector.Classify("dns-tunnel://eyJ4IjoxfQ#Emergency"));

    [Fact]
    public void Classify_HttpsUrl_StillSubscription()
        => Assert.Equal(SmpInputKind.SubscriptionUrl,
               SimpleInputDetector.Classify("https://example.com/sub"));

    [Fact]
    public void HostSubtitle_DnsTunnelEntry_ShowsDnsTunnel()
    {
        var vm = new ServerViewModel(new VlessServerEntry
        {
            Protocol = "dns-tunnel",
            Server = "tunnel.example.org",
            DnsDomain = "tunnel.example.org",
        });
        Assert.Equal("dns-tunnel", vm.HostSubtitle);
    }
}
