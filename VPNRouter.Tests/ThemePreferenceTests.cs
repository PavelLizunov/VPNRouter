using VPNRouter.App.ViewModels;
using Xunit;

namespace VPNRouter.Tests;

public class ThemePreferenceTests
{
    [Theory]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("system", "system")]
    public void Canonical_values_pass_through(string raw, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.NormalizeThemePref(raw));
    }

    [Theory]
    [InlineData("Light", "light")]
    [InlineData("DARK", "dark")]
    [InlineData("System", "system")]
    public void Casing_is_normalized(string raw, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.NormalizeThemePref(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("nonsense")]
    public void Unknown_or_missing_defaults_to_system(string? raw)
    {
        Assert.Equal("system", MainWindowViewModel.NormalizeThemePref(raw));
    }
}
