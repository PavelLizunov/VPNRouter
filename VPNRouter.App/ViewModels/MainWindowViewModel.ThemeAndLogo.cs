#nullable enable
using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogoSource))]
    private bool _isDarkTheme;

    // v2.40.x (Fix #7): the user's theme PREFERENCE — "light" | "dark" |
    // "system". Distinct from IsDarkTheme, which is the EFFECTIVE variant
    // currently showing (resolved in ApplyTheme; "system" derives it from the
    // OS appearance). The three derived bools drive the segmented control's
    // active-state in the ⋯ menu.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemThemePref))]
    [NotifyPropertyChangedFor(nameof(IsLightThemePref))]
    [NotifyPropertyChangedFor(nameof(IsDarkThemePref))]
    private string _themePreference = "system";

    public bool IsSystemThemePref => string.Equals(ThemePreference, "system", StringComparison.OrdinalIgnoreCase);
    public bool IsLightThemePref  => string.Equals(ThemePreference, "light",  StringComparison.OrdinalIgnoreCase);
    public bool IsDarkThemePref   => string.Equals(ThemePreference, "dark",   StringComparison.OrdinalIgnoreCase);

    // Astra icon refresh: both variants are generated from committed SVG
    // masters. Loading the dark-surface asset directly preserves the amber
    // beak; RGB inversion would turn that accent blue.
    private static readonly Bitmap _logoLight = LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot.png");
    private static readonly Bitmap _logoDark  = LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot_white.png");

    /// <summary>Header mascot selected for the effective theme.</summary>
    public Bitmap LogoSource => IsDarkTheme ? _logoDark : _logoLight;
    private static Bitmap LoadAsset(string uri) => new(AssetLoader.Open(new System.Uri(uri)));
    [ObservableProperty] private string _themeToggleText = Strings.ThemeDark;
}
