using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class MacHelperNameExpansionTests
{
    [Fact]
    public void Expands_chromium_parent_to_helper_variants()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Google Chrome" });

        Assert.Contains("Google Chrome", result);
        Assert.Contains("Google Chrome Helper", result);
        Assert.Contains("Google Chrome Helper (GPU)", result);
        Assert.Contains("Google Chrome Helper (Renderer)", result);
        Assert.Contains("Google Chrome Helper (Plugin)", result);
    }

    [Fact]
    public void Safari_maps_to_webkit_xpc_io_processes()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Safari" });

        Assert.Contains("Safari", result);
        Assert.Contains("com.apple.WebKit.Networking", result);
        Assert.Contains("com.apple.Safari.SearchHelper", result);
        Assert.Contains("com.apple.WebKit.WebContent", result);
        Assert.DoesNotContain("Safari Helper", result);
        Assert.DoesNotContain("Safari Helper (Renderer)", result);
    }
}
