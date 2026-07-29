using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TunStackPlatformTests
{
    [Theory]
    [InlineData(true, "gvisor")]
    [InlineData(false, "system")]
    public void SelectTunStack_UsesGvisorOnlyOnMacOS(bool isMacOS, string expected)
        => Assert.Equal(expected, ConfigGenerator.SelectTunStack(isMacOS));
}
