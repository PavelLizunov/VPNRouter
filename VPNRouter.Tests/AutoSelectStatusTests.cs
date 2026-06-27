using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

/// <summary>
/// G2 (2026-06-27): when AutoSelectBestServer is on, the connected-status label
/// must show the REAL urltest-selected node (resolved from clash_api), not the
/// user's nominal pick — fixing the "Iceland · German-IP" mismatch. Pins the
/// pure decision in <see cref="AutoSelectStatus.ResolveSubscribeLabel"/>.
/// </summary>
public class AutoSelectStatusTests
{
    [Fact]
    public void AutoOff_UsesNominalPick()
    {
        var (name, ip) = AutoSelectStatus.ResolveSubscribeLabel(
            autoSelectOn: false, hasAutoNode: false,
            autoName: null, autoIp: null, autoLabel: "AUTO",
            nominalName: "Iceland VLESS", nominalIp: "93.95.226.167");

        Assert.Equal("Iceland VLESS", name);
        Assert.Equal("93.95.226.167", ip);
    }

    [Fact]
    public void AutoOn_NodeKnown_ShowsRealNode_NotNominal()
    {
        // User picked Iceland; sing-box urltest actually routes via Germany.
        var (name, ip) = AutoSelectStatus.ResolveSubscribeLabel(
            autoSelectOn: true, hasAutoNode: true,
            autoName: "Germany VLESS", autoIp: "104.194.156.93", autoLabel: "AUTO",
            nominalName: "Iceland VLESS", nominalIp: "93.95.226.167");

        Assert.Equal("Germany VLESS", name);     // real node, not "Iceland"
        Assert.Equal("104.194.156.93", ip);      // and the matching IP
    }

    [Fact]
    public void AutoOn_NodeNotYetResolved_ShowsAutoLabel_NoStaleIp()
    {
        var (name, ip) = AutoSelectStatus.ResolveSubscribeLabel(
            autoSelectOn: true, hasAutoNode: false,
            autoName: null, autoIp: null, autoLabel: "Авто-выбор",
            nominalName: "Iceland VLESS", nominalIp: "93.95.226.167");

        Assert.Equal("Авто-выбор", name);
        Assert.Null(ip); // never assert a stale server IP before the node is known
    }
}
