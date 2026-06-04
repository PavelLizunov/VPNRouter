using VPNRouter.App.ViewModels;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Headless coverage for the tri-state theme preference normalizer (Fix #7,
/// macOS deep-audit 2026-06-04). NormalizeThemePref is the load/save coercion
/// that decides whether a persisted/raw theme string is "light", "dark", or
/// "system" — the default-to-"system" branch is what makes a fresh install
/// follow the OS appearance and fixes Olga's symptom #4 (app launched light
/// while macOS was in Dark). The live NSAppearance read + OS flip can only be
/// verified on a real Mac; this pins the pure string mapping.
/// </summary>
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
    [InlineData("auto")]      // legacy/unknown token
    [InlineData("nonsense")]
    public void Unknown_or_missing_defaults_to_system(string? raw)
    {
        // Default-to-system is the heart of Fix #7: a brand-new install (no
        // persisted theme) and any corrupted value both follow the OS.
        Assert.Equal("system", MainWindowViewModel.NormalizeThemePref(raw));
    }
}
