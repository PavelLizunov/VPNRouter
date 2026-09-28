#nullable enable

using System.Net;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class NetworkInterfaceDetectorTests
{
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
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(name, description));
    }

    [Fact]
    public void IsWireGuardName_IsCaseInsensitive()
    {
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("wg-private", "wireguard tunnel"));
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("AMNEZIAWG-TUNNEL", "Some adapter"));
        Assert.True(NetworkInterfaceDetector.IsWireGuardName("awg0", "ALL-CAPS DESC"));
    }

    [Fact]
    public void IsWireGuardName_NullsAreTreatedAsNoMatch()
    {
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(null, null));
        Assert.False(NetworkInterfaceDetector.IsWireGuardName(null, "Ethernet"));
        Assert.False(NetworkInterfaceDetector.IsWireGuardName("eth0", null));
    }

    [Fact]
    public void IsWireGuardName_MatchesAcrossEitherFieldSurface()
    {
        Assert.True(NetworkInterfaceDetector.IsWireGuardName(
            name: "my-custom-renamed-vpn",
            description: "WireGuard Tunnel"));

        Assert.True(NetworkInterfaceDetector.IsWireGuardName(
            name: "my-WireGuard-iface",
            description: "Unknown Network Adapter"));
    }

    [Theory]
    [InlineData("100.64.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("100.116.97.112")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    public void IsTailscaleCgnat_AcceptsCgnatRange(string ip)
    {
        Assert.True(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("100.0.0.1")]
    [InlineData("99.64.0.1")]
    [InlineData("10.9.1.2")]
    [InlineData("192.168.1.1")]
    [InlineData("104.194.156.93")]
    public void IsTailscaleCgnat_RejectsOutsideRange(string ip)
    {
        Assert.False(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsTailscaleCgnat_IPv6_ReturnsFalse()
    {
        Assert.False(NetworkInterfaceDetector.IsTailscaleCgnat(IPAddress.Parse("fd7a:115c:a1e0::1")));
    }

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
        Assert.False(NetworkInterfaceDetector.IsTailscaleName(name, description));
    }

    [Fact]
    public void IsTailscaleName_NullsAreNoMatch()
        => Assert.False(NetworkInterfaceDetector.IsTailscaleName(null, null));

    [Fact]
    public void CalculateSubnet_RegularSlash24_ComputesNetworkAddress()
    {
        var addr = IPAddress.Parse("192.168.1.42");
        var mask = IPAddress.Parse("255.255.255.0");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("192.168.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_RegularSlash16_ComputesWiderNetwork()
    {
        var addr = IPAddress.Parse("10.0.5.50");
        var mask = IPAddress.Parse("255.255.0.0");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.0.0.0/16", result);
    }

    [Fact]
    public void CalculateSubnet_PointToPointSlash32_WidensToSlash24()
    {
        var addr = IPAddress.Parse("10.9.1.2");
        var mask = IPAddress.Parse("255.255.255.255");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.9.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_NearPointToPointSlash31_AlsoWidensToSlash24()
    {
        var addr = IPAddress.Parse("10.9.1.3");
        var mask = IPAddress.Parse("255.255.255.254");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Equal("10.9.1.0/24", result);
    }

    [Fact]
    public void CalculateSubnet_IPv6Input_ReturnsNullSafely()
    {
        var addr = IPAddress.Parse("fe80::1");
        var mask = IPAddress.Parse("::1");

        var result = NetworkInterfaceDetector.CalculateSubnet(addr, mask);

        Assert.Null(result);
    }

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
        Assert.Equal(expected, NetworkInterfaceDetector.CountBits(mask));
    }

    [Fact]
    public void DetectWireGuardSubnets_EmptyHost_ReturnsNonNullList()
    {
        var result = NetworkInterfaceDetector.DetectWireGuardSubnets(
            ownTunName: "VPNRouter-TUN",
            logger: null);

        Assert.NotNull(result);
    }

    [Fact]
    public void DetectWireGuardSubnets_OwnTunNameFilter_IsRespected()
    {
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
        var result = NetworkInterfaceDetector.DetectWireGuardSubnets(
            ownTunName: "VPNRouter-TUN",
            logger: null);

        Assert.NotNull(result);
    }
}
