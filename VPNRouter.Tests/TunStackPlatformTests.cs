using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TunStackPlatformTests
{
    [Theory]
    [InlineData(true, false, "gvisor")]
    [InlineData(false, true, "gvisor")]
    [InlineData(false, false, "system")]
    public void SelectTunStack_UsesGvisorOnMacOSAndLinux(bool isMacOS, bool isLinux, string expected)
        => Assert.Equal(expected, ConfigGenerator.SelectTunStack(isMacOS, isLinux));
}
