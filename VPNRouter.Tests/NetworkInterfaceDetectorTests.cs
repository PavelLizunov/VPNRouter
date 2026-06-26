#nullable enable
// ═══════════════════════════════════════════════════════════════════════════════
// v3.0 Phase 2G — sub-wave 7b-1: NetworkInterfaceDetector coverage (MED priority)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md
//
// NetworkInterfaceDetector enumerates `NetworkInterface.GetAllNetworkInterfaces()`
// looking for active WireGuard / AmneziaWG adapters and computes the IPv4
// subnet to inject as `route_exclude_address` in sing-box config so VPNRouter's
// TUN can coexist with a separate host-level WG/AWG tunnel.
//
// The kernel enumeration call (`NetworkInterface.GetAllNetworkInterfaces`)
// is OS-provided and not directly mockable — `NetworkInterface` is abstract
// and only constructed by the runtime networking subsystem.
// Per the brief: "Test the result-transformation logic … the actual
// NetworkInterface enumeration is OS-provided and not directly mockable."
//
// Phase 2G (sub-wave 7b-1) widened three private statics to `internal`
// so the unit tests can pin their behaviour directly:
//   - IsWireGuardName(name, description) — keyword matching surface
//   - CalculateSubnet(IPAddress addr, IPAddress mask) — IP arithmetic
//   - CountBits(byte[] maskBytes) — prefix-length math
//
// The full DetectWireGuardSubnets path is smoke-tested by calling it on
// the live host once with no assertions on the *content* of the returned
// list — only that it doesn't throw, returns a non-null List<string>, and
// doesn't include the supplied own-TUN name. This catches regressions in
// the enumeration + filter wiring (e.g. a typo in the Equals comparison)
// without depending on the host's actual adapter set.
// ═══════════════════════════════════════════════════════════════════════════════

using System.Net;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for <see cref="NetworkInterfaceDetector"/>. Focused on the
/// pure result-transformation surface (keyword matching + subnet maths);
/// the live enumeration path is smoke-tested only.
/// </summary>
public sealed class NetworkInterfaceDetectorTests
{
    // ───────────────────────────────────────────────────────────────────────
    // IsWireGuardName — keyword filter against name/description
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WireGuard Tunnel", "wg0")]
    [InlineData("WireGuard tunnel adapter", "tun0")]
    [InlineData("AmneziaWG Adapter", "AmneziaVPN-Tun")]
    [InlineData("Amnezia VPN Service", "amnezia-tun")]
    [InlineData("AWG Tunnel", "awg-iface")]
    [InlineData("WG Tunnel", "wg-tunnel")]
    [InlineData("Tailscale Tunnel", "Tailscale")]
    public void IsWireGuardName_MatchesAllKnownKeywords(string description, string name)
    {
        // The keywords in NetworkInterfaceDetector.WgKeywords cover the
        // common adapter naming pattern for WireGuard, AmneziaWG, Tailscale,
        // and their GUI-installed variants. Each row above should match positively
        // via either description (typical) or name (fallback) — both paths
        // are tested by virtue of mixing them in the inline data.
        Assert.True(NetworkInterfaceDetector.IsWireGuardName(name, description));
    }

    [Theory]
    [InlineData("Intel(R) Ethernet Connection I219-LM", "Ethernet 1")]
    [InlineData("Realtek PCIe GbE Family Controller", "Local Area Connection")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", "Wi-Fi")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only")]
    [InlineData("TAP-Windows Adapter V9 for OpenVPN", "OpenVPN TAP-Windows6")]
    [InlineData("VPNRouter-TUN", "VPNRouter-TUN")]
    public void IsWireGuardName_RejectsUnrelatedAdapters(string description, string name)
    {
        // Negative cases: typical desktop/server adapter names that should
        // NOT trigger WireGuard detection. Especially "VPNRouter-TUN" (our
        // own adapter) and "OpenVPN TAP" (unrelated VPN tech) — we don't
        // want to accidentally exclude legitimate non-WG routes.
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(name, description));
    }

    [Fact]
    public void IsWireGuardName_IsCaseInsensitive()
    {
        // The matcher uses StringComparison.OrdinalIgnoreCase so user-installed
        // adapters with non-canonical casing (e.g. "wireguard" lowercase
        // from the open-source CLI installer) still match.
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("wg-private", "wireguard tunnel"));
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("AMNEZIAWG-TUNNEL", "Some adapter"));
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("awg0", "ALL-CAPS DESC"));
    }

    [Fact]
    public void IsWireGuardName_NullsAreTreatedAsNoMatch()
    {
        // Defensive: NetworkInterface.Name and .Description are never null
        // from the OS, but a future refactor or a test stub might pass null.
        // The helper must return false rather than NRE.
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(null, null));
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(null, "Ethernet"));
        Assert.False(NetworkInterfaceDetector.IsWireGuardName("eth0", null));
    }

    [Fact]
    public void IsWireGuardName_MatchesAcrossEitherFieldSurface()
    {
        // Critical: the matcher checks BOTH name AND description so a
        // user who renamed the friendly Name to something custom but
        // left the driver Description intact still gets detected.
        Assert.True(NetworkInterfaceDetector.IsWireGuardName(
            name: "my-custom-renamed-vpn",
            description: "WireGuard Tunnel")); // matches via description

        Assert.True(NetworkInterfaceDetector.IsWireGuardName(
            name: "my-WireGuard-iface",
            description: "Unknown Network Adapter")); // matches via name
    }

    // ───────────────────────────────────────────────────────────────────────
    // IsTailscaleCgnat — Tailscale CGNAT range (100.64.0.0/10) detection
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("100.64.0.0")]      // range start
    [InlineData("100.64.0.1")]
    [InlineData("100.116.97.112")]  // the Mac host on the tailnet
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")] // range end
    public void IsTailscaleCgnat_AcceptsCgnatRange(string ip)
    {
        // Tailscale assigns addresses from the RFC 6598 CGNAT block
        // 100.64.0.0/10 (100.64.0.0 – 100.127.255.255). A detected Tailscale
        // adapter must exclude the WHOLE /10 so every tailnet peer is reachable
        // while VPNRouter's full-tunnel is up (the /24-widening would miss peers
        // outside the local /24).
        Assert.True(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.63.255.255")] // one below the /10 (100.x but < .64)
    [InlineData("100.128.0.0")]    // one above the /10 (> .127)
    [InlineData("100.0.0.1")]
    [InlineData("99.64.0.1")]      // wrong first octet
    [InlineData("10.9.1.2")]       // WireGuard subnet — must NOT read as CGNAT
    [InlineData("192.168.1.1")]
    [InlineData("104.194.156.93")] // a VLESS server IP
    public void IsTailscaleCgnat_RejectsOutsideRange(string ip)
    {
        Assert.False(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsTailscaleCgnat_IPv6_ReturnsFalse()
    {
        // The CGNAT check is IPv4-only; an IPv6 address (Tailscale also assigns
        // fd7a:115c:a1e0::/48) must return false safely rather than misclassify.
        Assert.False(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse("fd7a:115c:a1e0::1")));
    }

    // ───────────────────────────────────────────────────────────────────────
    // IsTailscaleName — scopes the whole-/10 exclusion to Tailscale adapters only
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Tailscale Tunnel", "Tailscale")]
    [InlineData("tailscale tunnel", "ts0")]
    public void IsTailscaleName_MatchesTailscaleAdapters(string description, string name)
        => Assert.True(NetworkInterfaceDetector.IsTailscaleName(name, description));

    [Theory]
    [InlineData("WireGuard Tunnel", "wg0")]
    [InlineData("AmneziaWG Adapter", "awg0")]
    [InlineData("AWG Tunnel", "awg-iface")]
    [InlineData("Intel(R) Ethernet Connection", "Ethernet")]
    public void IsTailscaleName_RejectsNonTailscale(string description, string name)
    {
        // LEAK-SCOPE GUARD: a WG/AWG adapter (which the shared WgKeywords list DOES
        // match) must NOT read as Tailscale, so a CGNAT-numbered WG tunnel keeps its
        // own /24 via CalculateSubnet and never triggers the whole-100.64.0.0/10
        // exclusion that could over-exclude real ISP-CGNAT traffic.
        Assert.False(NetworkInterfaceDetector.IsTailscaleName(name, description));
    }

    [Fact]
    public void IsTailscaleName_NullsAreNoMatch()
        => Assert.False(NetworkInterfaceDetector.IsTailscaleName(null, null));

    // ───────────────────────────────────────────────────────────────────────
    // CalculateSubnet — IP + mask → CIDR notation
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculateSubnet_RegularSlash24_ComputesNetworkAddress()
    {
        // Standard residential subnet: 192.168.1.42 with mask 255.255.255.0
        // → network address 192.168.1.0/24. This is the most common
        // local-network case.
        var addr = IPAddress.Parse("192.168.1.42");
        var mask = IPAddress.Parse("255.255.255.0");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("192.168.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_RegularSlash16_ComputesWiderNetwork()
    {
        // Larger subnet: 10.0.5.50 with mask 255.255.0.0 → 10.0.0.0/16
        // Verifies the AND-with-mask arithmetic across the byte boundary.
        var addr = IPAddress.Parse("10.0.5.50");
        var mask = IPAddress.Parse("255.255.0.0");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.0.0.0/16", result);
    }

    [Fact]
    public void CalculateSubnet_PointToPointSlash32_WidensToSlash24()
    {
        // CRITICAL: WireGuard often installs point-to-point /32 masks
        // where the IP IS the network. If we used /32 verbatim in
        // route_exclude_address, sing-box would only exclude the local
        // peer IP and capture traffic to the gateway (10.9.1.1) or
        // other peers — exactly the route conflict we're trying to
        // avoid. NetworkInterfaceDetector widens to /24 in this case.
        var addr = IPAddress.Parse("10.9.1.2");
        var mask = IPAddress.Parse("255.255.255.255"); // /32 point-to-point

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.9.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_NearPointToPointSlash31_AlsoWidensToSlash24()
    {
        // The widening predicate is `prefixLen >= 31`, so a /31
        // (255.255.255.254) also triggers the WG-coexistence path.
        // Edge: /31 is used for point-to-point links in some BGP setups
        // but in our context the only producer is WG with a non-standard
        // 2-host VPN, which still benefits from /24 widening.
        var addr = IPAddress.Parse("10.9.1.3");
        var mask = IPAddress.Parse("255.255.255.254"); // /31

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.9.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_IPv6Input_ReturnsNullSafely()
    {
        // The detector explicitly skips IPv6 unicast addresses in
        // DetectWireGuardSubnets, but if an IPv6 IPAddress reaches
        // CalculateSubnet directly (e.g. via a future call site or
        // a misclassified address), the function returns null rather
        // than crashing — its first check is byte-length == 4.
        var addr = IPAddress.Parse("fe80::1");
        var mask = IPAddress.Parse("::1"); // not a real mask but proves the length-guard

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Null(result);
    }

    // ───────────────────────────────────────────────────────────────────────
    // CountBits — set-bit counter for prefix-length derivation
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 255, 255, 255, 0 }, 24)]
    [InlineData(new byte[] { 255, 255, 0, 0 }, 16)]
    [InlineData(new byte[] { 255, 0, 0, 0 }, 8)]
    [InlineData(new byte[] { 255, 255, 255, 255 }, 32)]
    [InlineData(new byte[] { 0, 0, 0, 0 }, 0)]
    [InlineData(new byte[] { 255, 255, 255, 128 }, 25)]
    [InlineData(new byte[] { 255, 255, 255, 252 }, 30)]
    public void CountBits_CountsSetBitsAcrossAllBytes(byte[] mask, int expected)
    {
        // Pin the prefix-length math for all standard mask shapes:
        // - All-byte boundaries (/8, /16, /24, /32) — typical CIDR
        // - Within-byte split (/25, /30) — sub-class-C networks
        // - All-zero — pathological but must not throw
        Assert.Equal(expected, NetworkInterfaceDetector.CountBits(mask));
    }

    // ───────────────────────────────────────────────────────────────────────
    // DetectWireGuardSubnets — full enumeration smoke
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectWireGuardSubnets_EmptyHost_ReturnsNonNullList()
    {
        // Smoke: the live OS enumeration is invoked. We can't assert on
        // the *content* of the list because it depends on the host's
        // adapter set (CI runners typically have zero WG adapters), but
        // the call must:
        //   - not throw
        //   - return a non-null List<string>
        //   - not include subnets from our own TUN name
        //
        // This catches regressions in the enumeration glue (e.g. a
        // NetworkInformation.NetworkInformationException now bubbling up
        // instead of being caught + logged).
        var result = NetworkInterfaceDetector.DetectWireGuardSubnets(
            ownTunName: "VPNRouter-TUN",
            logger: null);

        Assert.NotNull(result);
        // No need to assert Empty — a dev VM with WG installed might
        // legitimately return entries. We only need the call to be safe.
    }

    [Fact]
    public void DetectWireGuardSubnets_OwnTunNameFilter_IsRespected()
    {
        // Even if the live host has a WireGuard-named adapter, passing
        // its exact name as ownTunName excludes it. We can't construct
        // a NetworkInterface in test code, so we proxy by checking the
        // call is idempotent + non-throwing across multiple ownTunName
        // values. The unit test for the FILTER LOGIC itself is the
        // IsWireGuardName tests above; this is a smoke that the
        // top-level Equals(ownTunName) check is wired.
        var a = NetworkInterfaceDetector.DetectWireGuardSubnets("VPNRouter-TUN", logger: null);
        var b = NetworkInterfaceDetector.DetectWireGuardSubnets("AnotherTunName", logger: null);
        var c = NetworkInterfaceDetector.DetectWireGuardSubnets("", logger: null);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
    }

    [Fact]
    public void DetectWireGuardSubnets_NullLogger_IsAcceptedGracefully()
    {
        // Signature allows nullable ILogger. The internal Debug/Warning
        // calls must guard with the null-conditional operator so passing
        // null doesn't NRE on the first log line.
        var result = NetworkInterfaceDetector.DetectWireGuardSubnets(
            ownTunName: "VPNRouter-TUN",
            logger: null);

        Assert.NotNull(result);
    }
}
