using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Regression tests for <see cref="VpnEngine.ComputeTunFingerprint"/> — the
/// helper that captures a stable TUN-settings snapshot used to auto-escalate
/// <c>ApplyAsync(forceRestart: true)</c> when TUN-layer fields change. Without
/// this, sing-box's Clash API <c>PUT /configs</c> would silently accept a
/// config with new TUN properties but keep the live adapter's old kernel
/// state — users saw "toggle does nothing" or "split→full doesn't rewrap VM".
///
/// <para>Pinning semantics here matters more than pinning VpnEngine's branching
/// (which is covered implicitly by the build/integration smoke in release
/// testing): a future refactor that breaks fingerprint stability in a
/// non-obvious way (e.g. by skipping a field, or by making it order-sensitive)
/// would regress the self-heal without breaking any other test.</para>
/// </summary>
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
        // This is the most important field for leak-protection. The whole
        // point of the fingerprint is to catch strict_route flips that
        // hot-reload would otherwise swallow.
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
        // v2.20.0 AmneziaWG coexistence — this field lives inside the TUN
        // adapter's route table and CANNOT be re-applied via hot-reload.
        var baseline = VpnEngine.ComputeTunFingerprint(Default());
        var modified = Default();
        modified.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        Assert.NotEqual(baseline, VpnEngine.ComputeTunFingerprint(modified));
    }

    [Fact]
    public void RouteExcludeAddress_OrderIndependent()
    {
        // Reordering excludes in the UI shouldn't trigger a restart — that
        // would be spurious. Only adding/removing entries counts.
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
        // Users editing yaml directly may leave spaces / mixed case. These
        // are semantically identical subnets — don't force a restart for
        // cosmetic diffs.
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
        // Freshly-deserialized yaml could leave the list null (YamlDotNet
        // nulls an absent collection). Fingerprint must treat it as empty,
        // not crash.
        var tun = Default();
        tun.RouteExcludeAddress = null!;
        var fp = VpnEngine.ComputeTunFingerprint(tun);
        Assert.NotNull(fp);
        Assert.NotEmpty(fp);
    }

    [Fact]
    public void Fingerprint_IsDeterministic()
    {
        // Same inputs → exact same string, even across test runs / threads.
        // Guards against accidental introduction of a non-stable element
        // (e.g. DateTime.Now, hash of object identity).
        var tun = Default();
        var f1 = VpnEngine.ComputeTunFingerprint(tun);
        var f2 = VpnEngine.ComputeTunFingerprint(tun);
        var f3 = VpnEngine.ComputeTunFingerprint(Default());
        Assert.Equal(f1, f2);
        Assert.Equal(f1, f3);
    }
}
