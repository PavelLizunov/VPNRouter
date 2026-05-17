using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// AppSettings — EmergencyChannel section defaults / null-safety
// ═══════════════════════════════════════════════════════════════════════════════

public class AppSettingsEmergencyChannelTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.EmergencyChannel);
        Assert.False(settings.EmergencyChannel.Enabled);
        Assert.Null(settings.EmergencyChannel.WgturnUrl);
        Assert.Null(settings.EmergencyChannel.VkLink);
    }

    [Fact]
    public void EnsureSane_PopulatesNullSection()
    {
        var settings = new AppSettings();
        settings.EmergencyChannel = null!; // simulate YamlDotNet returning null for `emergency_channel:` with no body
        var fixedUp = settings.EnsureSane();
        Assert.NotNull(fixedUp.EmergencyChannel);
        Assert.False(fixedUp.EmergencyChannel.Enabled);
    }

    [Fact]
    public void Parse_YamlWithEmergencyChannel_RoundTrips()
    {
        const string yaml = """
            schema_version: 2
            emergency_channel:
              enabled: true
              wgturn_url: "wgturn://eyJ2IjoxfQ#brat"
              vk_link: "https://vk.com/call/join/abc"
            """;
        var settings = SettingsLoader.Parse(yaml);
        Assert.True(settings.EmergencyChannel.Enabled);
        Assert.Equal("wgturn://eyJ2IjoxfQ#brat", settings.EmergencyChannel.WgturnUrl);
        Assert.Equal("https://vk.com/call/join/abc", settings.EmergencyChannel.VkLink);
    }
}
