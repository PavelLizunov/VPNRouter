using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// BR-8 (brat 2026-05-20) — pin the TUN-CIDR normalisation helper that
/// derives the allow-rule scope for <see cref="FirewallManager.EnableDnsLockdownAsync"/>.
///
/// <para>Why: r11 deferred the Wave 39 lockdown until TUN warm-up
/// succeeded, but once on the lockdown still blocked UDP/53 to
/// sing-box's own TUN DNS endpoint (172.19.0.2:53) because the rule was
/// unscoped. r12 adds an explicit allow rule scoped to the TUN /30 range,
/// derived from <c>settings.Tun.Ipv4Address</c>. This pins the parsing
/// edge cases — caller-side defaults, malformed input, IPv6, and the
/// canonical bundled "172.19.0.1/30" → "172.19.0.0/30" reduction.</para>
/// </summary>
public class FirewallManagerTunAllowTests
{
    [Theory]
    [InlineData("172.19.0.1/30",   "172.19.0.0/30")]
    [InlineData("172.19.0.0/30",   "172.19.0.0/30")]
    [InlineData("172.19.0.2/30",   "172.19.0.0/30")]
    [InlineData("172.20.0.1/24",   "172.20.0.0/24")]
    [InlineData("10.8.0.1/24",     "10.8.0.0/24")]
    [InlineData("192.168.5.5/16",  "192.168.0.0/16")]
    public void NormalizeTunAllowIp_ProducesNetworkCidr(string input, string expected)
    {
        var result = FirewallManager.NormalizeTunAllowIp(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999/30")]
    [InlineData("::1/128")]              // IPv6 — bundled config is IPv4 only.
    public void NormalizeTunAllowIp_InvalidInput_ReturnsNull(string? input)
    {
        var result = FirewallManager.NormalizeTunAllowIp(input);
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeTunAllowIp_BareIp_AssumesSlash30()
    {
        // No prefix means "treat as /30" (the bundled default). This
        // gives us a safe fallback if a caller forgot to include the
        // CIDR suffix.
        var result = FirewallManager.NormalizeTunAllowIp("172.19.0.1");
        Assert.Equal("172.19.0.0/30", result);
    }

    // ─── BR-9 r17: ComputeBlockExclusionRange ───────────────────────────────

    [Theory]
    [InlineData("172.19.0.1/30", "0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255")] // bundled default
    [InlineData("10.8.0.1/24",   "0.0.0.0-10.7.255.255,10.8.1.0-255.255.255.255")]
    [InlineData("192.168.5.5/16", "0.0.0.0-192.167.255.255,192.169.0.0-255.255.255.255")]
    public void ComputeBlockExclusionRange_ProducesCorrectComplement(string tunCidr, string expected)
    {
        var result = FirewallManager.ComputeBlockExclusionRange(tunCidr);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("::1/128")]
    public void ComputeBlockExclusionRange_InvalidInput_ReturnsNull(string? tunCidr)
    {
        var result = FirewallManager.ComputeBlockExclusionRange(tunCidr);
        Assert.Null(result);
    }
}
