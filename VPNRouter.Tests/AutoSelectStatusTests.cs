using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

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
        var (name, ip) = AutoSelectStatus.ResolveSubscribeLabel(
            autoSelectOn: true, hasAutoNode: true,
            autoName: "Germany VLESS", autoIp: "104.194.156.93", autoLabel: "AUTO",
            nominalName: "Iceland VLESS", nominalIp: "93.95.226.167");

        Assert.Equal("Germany VLESS", name);
        Assert.Equal("104.194.156.93", ip);
    }

    [Fact]
    public void AutoOn_NodeNotYetResolved_ShowsAutoLabel_NoStaleIp()
    {
        var (name, ip) = AutoSelectStatus.ResolveSubscribeLabel(
            autoSelectOn: true, hasAutoNode: false,
            autoName: null, autoIp: null, autoLabel: "Авто-выбор",
            nominalName: "Iceland VLESS", nominalIp: "93.95.226.167");

        Assert.Equal("Авто-выбор", name);
        Assert.Null(ip);
    }
}
