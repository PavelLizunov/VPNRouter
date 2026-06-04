using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Headless coverage for the macOS routed-app helper-name expansion (Fix #2,
/// deep-audit 2026-06-04). Chromium/Electron apps do their network I/O under
/// child "Helper" processes; sing-box matches process_name exactly, so routing
/// only the parent name ("Google Chrome") silently leaks the helpers that
/// actually connect ("Google Chrome Helper (Renderer)"). ConfigGenerator
/// expands the routed list on macOS via this helper.
///
/// The OS gate lives at the call site; the helper itself is pure, so these run
/// on the Windows test build.
/// </summary>
public class MacHelperNameExpansionTests
{
    [Fact]
    public void Expands_chromium_parent_to_helper_variants()
    {
        var result = ConfigGenerator.ExpandMacHelperNames(new[] { "Google Chrome" });

        Assert.Contains("Google Chrome", result);                       // parent kept
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
        // process_name is case-sensitive (golden rule #7): "Chrome" and "chrome"
        // are distinct and BOTH must survive with original casing.
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

        // "Brave" + 4 helpers = 5 entries, no dupes, no blank.
        Assert.Equal(5, result.Count);
        Assert.DoesNotContain("", result);
    }

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        Assert.Empty(ConfigGenerator.ExpandMacHelperNames(System.Array.Empty<string>()));
    }
}
