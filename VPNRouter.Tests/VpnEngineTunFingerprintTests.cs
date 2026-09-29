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
}
