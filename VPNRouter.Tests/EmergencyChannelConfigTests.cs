using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// EmergencyChannelConfig — r9 Phase 2 (wgturn-core integration)
// ═══════════════════════════════════════════════════════════════════════════════

public class EmergencyChannelConfigTests
{
    [Fact]
    public void TryParse_ValidUrl_Success()
    {
        const string url = "wgturn://eyJ2IjoxLCJzcCI6ImFiYyIsImNwIjoieHl6IiwiZXAiOiJleGFtcGxlLmNvbTo1NjAwMCIsImFkIjoiMTAuNy4wLjUvMjQifQ";
        const string vk = "https://vk.com/call/join/abc-def";

        Assert.True(EmergencyChannelConfig.TryParse(url, vk, out var config));
        Assert.Equal(url, config.WgturnUrl);
        Assert.Equal(vk, config.VkLink);
        Assert.Null(config.Label);
    }

    [Fact]
    public void TryParse_InvalidScheme_Fails()
    {
        Assert.False(EmergencyChannelConfig.TryParse("vless://server.example.com", "https://vk.com/call/join/x", out _));
        Assert.False(EmergencyChannelConfig.TryParse("https://vk.com/call/join/x", "https://vk.com/call/join/x", out _));
        Assert.False(EmergencyChannelConfig.TryParse("not-a-url-at-all", "https://vk.com/call/join/x", out _));
    }

    [Fact]
    public void TryParse_MissingVkLink_Allowed()
    {
        const string url = "wgturn://eyJ2IjoxfQ";

        // VK link is a runtime parameter — TryParse must not require it.
        // Engine validates VK link at StartAsync time instead.
        Assert.True(EmergencyChannelConfig.TryParse(url, out var config));
        Assert.Equal(url, config.WgturnUrl);
        Assert.Equal(string.Empty, config.VkLink);

        // Same with explicit empty string
        Assert.True(EmergencyChannelConfig.TryParse(url, vkLink: "", out var c2));
        Assert.Equal(string.Empty, c2.VkLink);
    }

    [Fact]
    public void TryParse_EmptyPayload_Fails()
    {
        // wgturn:// with no body or only the fragment — meaningless.
        Assert.False(EmergencyChannelConfig.TryParse("wgturn://", "vk", out _));
        Assert.False(EmergencyChannelConfig.TryParse("wgturn://#label-only", "vk", out _));
        Assert.False(EmergencyChannelConfig.TryParse("", "vk", out _));
        Assert.False(EmergencyChannelConfig.TryParse("   ", "vk", out _));
    }

    [Fact]
    public void TryParse_WithLabel_ExtractsLabel()
    {
        const string url = "wgturn://eyJ2IjoxfQ#brat-pc";

        Assert.True(EmergencyChannelConfig.TryParse(url, "vk", out var config));
        Assert.Equal("brat-pc", config.Label);
        // WgturnUrl preserves the full original string (label included)
        // so it can be passed verbatim to wgturn-cli connect-url.
        Assert.Equal(url, config.WgturnUrl);
    }

    [Fact]
    public void TryParse_PercentEncodedLabel_Decoded()
    {
        const string url = "wgturn://eyJ2IjoxfQ#brat%20pc";

        Assert.True(EmergencyChannelConfig.TryParse(url, "vk", out var config));
        Assert.Equal("brat pc", config.Label);
    }

    [Fact]
    public void TryParse_SchemeIsCaseInsensitive()
    {
        // RFC 3986: scheme is case-insensitive. The body is opaque base64
        // so we don't normalize that, but accepting WGTURN:// guards
        // against uppercased URLs from VK Calls clipboard quirks.
        Assert.True(EmergencyChannelConfig.TryParse("WGTURN://eyJ2IjoxfQ", "vk", out _));
        Assert.True(EmergencyChannelConfig.TryParse("WgTuRn://eyJ2IjoxfQ", "vk", out _));
    }
}
