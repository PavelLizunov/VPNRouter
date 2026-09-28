using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class VpnEngineTunFingerprintTests
{
    private static TunSettings Default() => new()
    {
        InterfaceName = "VPNRouter-TUN",
        Ipv4Address = "172.19.0.1/30",
        Ipv6Enabled = false,
        Mtu = TunSettings.DefaultMtu,
        AutoRoute = true,
        StrictRoute = false,
        RouteExcludeAddress = new List<string>()
    };

    [Fact]
    public void SameSettings_IdenticalFingerprint()
    {
        var a = VpnEngine.ComputeTunFingerprint(Default());
        var b = VpnEngine.ComputeTunFingerprint(Default());
        Assert.Equal(a, b);
    }

    [Fact]
    public void InterfaceNameChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.InterfaceName = "Different-TUN";
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void Ipv4AddressChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.Ipv4Address = "10.200.0.1/24";
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void MtuChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.Mtu = 1500;
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void AutoRouteChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.AutoRoute = false;
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void StrictRouteChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.StrictRoute = true;
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void Ipv6EnabledChange_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.Ipv6Enabled = true;
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void RouteExcludeAddressAdded_ChangesFingerprint()
    {
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void RouteExcludeAddress_OrderIndependent()
    {
        var a = Default();
        a.RouteExcludeAddress = new List<string> { "10.9.1.0/24", "192.168.50.0/24" };
        var b = Default();
        b.RouteExcludeAddress = new List<string> { "192.168.50.0/24", "10.9.1.0/24" };

        Assert.Equal(
            VpnEngine.ComputeTunFingerprint(a),
            VpnEngine.ComputeTunFingerprint(b));
    }

    [Fact]
    public void RouteExcludeAddress_IgnoresWhitespaceAndCase()
    {
        var a = Default();
        a.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        var b = Default();
        b.RouteExcludeAddress = new List<string> { "  10.9.1.0/24  " };

        Assert.Equal(
            VpnEngine.ComputeTunFingerprint(a),
            VpnEngine.ComputeTunFingerprint(b));
    }

    [Fact]
    public void NullRouteExcludeAddress_DoesNotThrow()
    {
        var tun = Default();
        tun.RouteExcludeAddress = null!;
        var fp = VpnEngine.ComputeTunFingerprint(tun);
        Assert.NotNull(fp);
        Assert.NotEmpty(fp);
    }

    [Fact]
    public void Fingerprint_IsDeterministic()
    {
        var tun = Default();
        var f1 = VpnEngine.ComputeTunFingerprint(tun);
        var f2 = VpnEngine.ComputeTunFingerprint(tun);
        var f3 = VpnEngine.ComputeTunFingerprint(Default());
        Assert.Equal(f1, f2);
        Assert.Equal(f1, f3);
    }
}
