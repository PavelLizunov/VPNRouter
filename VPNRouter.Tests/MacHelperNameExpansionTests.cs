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
    public void Parent_name_comes_before_its_helpers()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Discord" });

        Assert.Equal("Discord", result[0]);
        Assert.True(result.IndexOf("Discord Helper") > 0);
    }

    [Fact]
    public void Does_not_re_expand_a_name_that_is_already_a_helper()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Google Chrome Helper (GPU)" });

        Assert.Equal(new[] { "Google Chrome Helper (GPU)" }, result);
        Assert.DoesNotContain("Google Chrome Helper (GPU) Helper", result);
    }

    [Fact]
    public void Preserves_case_and_dedups_case_sensitively()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Chrome", "chrome" });

        Assert.Contains("Chrome", result);
        Assert.Contains("chrome", result);
        Assert.Contains("Chrome Helper", result);
        Assert.Contains("chrome Helper", result);
    }

    [Fact]
    public void Expands_each_app_in_a_multi_app_list()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Slack", "Code" });

        Assert.Contains("Slack Helper (Renderer)", result);
        Assert.Contains("Code Helper (Renderer)", result);
    }

    [Fact]
    public void Skips_blank_entries_and_dedups_repeats()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Brave", "", "  ", "Brave" });

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain("", result);
    }

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        Assert.Empty(ConfigGenerator.ExpandMacHelperNames(System.Array.Empty<string>()));
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

    [Fact]
    public void Chromium_apps_still_get_suffix_expansion_not_webkit()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Brave Browser" });

        Assert.Contains("Brave Browser Helper", result);
        Assert.Contains("Brave Browser Helper (Renderer)", result);
        Assert.DoesNotContain("com.apple.WebKit.Networking", result);
    }
}
