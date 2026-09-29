using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

public class PerAppFilterModeTests
{
    [Theory]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("   ", "off")]
    [InlineData("off", "off")]
    [InlineData("OFF", "off")]
    [InlineData("include", "include")]
    [InlineData("Include", "include")]
    [InlineData("INCLUDE", "include")]
    [InlineData("exclude", "exclude")]
    [InlineData("Exclude", "exclude")]
    [InlineData("EXCLUDE", "exclude")]
    [InlineData("garbage", "off")]
    [InlineData("split", "off")]
    [InlineData("on", "off")]
    public void Normalize_CanonicalisesAllInputs(string? input, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.Normalize(input));
    }

    [Theory]
    [InlineData(null, "include")]
    [InlineData("", "include")]
    [InlineData("   ", "include")]
    [InlineData("off", "include")]
    [InlineData("include", "include")]
    [InlineData("INCLUDE", "include")]
    [InlineData("exclude", "exclude")]
    [InlineData("Exclude", "exclude")]
    [InlineData("EXCLUDE", "exclude")]
    [InlineData("garbage", "include")]
    public void ResolveLastMode_OnlyExcludeSticks(string? input, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.ResolveLastMode(input));
    }

    [Theory]
    [InlineData("include", true)]
    [InlineData("exclude", true)]
    [InlineData("INCLUDE", true)]
    [InlineData("Exclude", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void IsSplit_CoversIncludeAndExcludeOnly(string? input, bool expected)
    {
        Assert.Equal(expected, PerAppFilterMode.IsSplit(input));
    }

    [Theory]
    [InlineData("off", "full")]
    [InlineData("OFF", "full")]
    [InlineData(null, "full")]
    [InlineData("", "full")]
    [InlineData("garbage", "full")]
    [InlineData("include", "split")]
    [InlineData("Include", "split")]
    [InlineData("exclude", "split")]
    [InlineData("EXCLUDE", "split")]
    public void RoutingModeFor_ProjectsPerAppModeToSplitFull(string? perAppMode, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.RoutingModeFor(perAppMode));
    }

    [Theory]
    [InlineData("full", "include", "include", "off")]
    [InlineData("full", "exclude", "exclude", "off")]
    [InlineData("full", "off", "include", null)]
    [InlineData("split", "off", "include", "include")]
    [InlineData("split", "off", "exclude", "exclude")]
    [InlineData("split", "off", null, "include")]
    [InlineData("split", "off", "garbage", "include")]
    [InlineData("split", "include", "exclude", null)]
    [InlineData("split", "exclude", "include", null)]
    [InlineData("SPLIT", "off", "include", "include")]
    public void PerAppModeForRoutingChange_TranslatesVerbToPerAppMode(
        string routingMode, string currentPerAppMode, string? lastMode, string? expected)
    {
        Assert.Equal(expected,
            PerAppFilterMode.PerAppModeForRoutingChange(routingMode, currentPerAppMode, lastMode));
    }

    [Theory]
    [InlineData("off", "full", "full")]
    [InlineData("include", "full", "full")]
    [InlineData("exclude", "full", "full")]
    [InlineData("off", "split", "split")]
    [InlineData("include", "split", "split")]
    [InlineData("exclude", "split", "split")]
    public void RoutingChange_ThenProject_RoundTrips(
        string startPerAppMode, string applyRoutingMode, string expectedRoutingMode)
    {
        var lastMode = PerAppFilterMode.ResolveLastMode(startPerAppMode);
        var next = PerAppFilterMode.PerAppModeForRoutingChange(
            applyRoutingMode, startPerAppMode, lastMode);
        var effectivePerAppMode = next ?? startPerAppMode;

        Assert.Equal(expectedRoutingMode, PerAppFilterMode.RoutingModeFor(effectivePerAppMode));
    }
}
