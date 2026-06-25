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

    // v2.20.3: single transparent-background mascot (penguin_mascot.png,
    // 640×640, black lineart on alpha). Previous b_icon/w_icon pair had
    // SOLID backgrounds (not transparent) and I had them swapped to boot —
    // on light theme we were showing the black-rectangle variant, on dark
    // the white-rectangle one, both as visible rectangles inside the
    // accent-subtle container. User provided the clean transparent
    // version; we use it directly for light theme and RGB-invert it for
    // dark theme so the black lineart becomes white. Alpha channel is
    // preserved through the invert so edges stay anti-aliased.
    private static readonly Bitmap _logoLight = LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot.png");
    private static readonly Bitmap _logoDark  = TryBuildInvertedLogo(_logoLight) ?? _logoLight;
    /// <summary>
    /// Header mascot. Light theme uses the source image as-is (black
    /// lineart on transparent). Dark theme uses an RGB-inverted copy
    /// (white lineart on transparent) so it remains visible against the
    /// dark subheader background.
    /// </summary>
    public Bitmap LogoSource => IsDarkTheme ? _logoDark : _logoLight;
    private static Bitmap LoadAsset(string uri) => new(AssetLoader.Open(new System.Uri(uri)));

    /// <summary>
    /// Produce an RGB-inverted copy that preserves alpha. Uses
    /// WriteableBitmap in Bgra8888/Unpremul so inverting the RGB channels
    /// doesn't interact with premultiplied-alpha edges (no fringing).
    /// Returns null on any failure — caller falls back to the original
    /// bitmap, which just renders invisibly on dark theme but at least
    /// doesn't crash the window.
    /// </summary>
    private static Bitmap? TryBuildInvertedLogo(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            var wb = new Avalonia.Media.Imaging.WriteableBitmap(
                size,
                source.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Unpremul);

            using (var fb = wb.Lock())
            {
                int byteCount = fb.RowBytes * size.Height;
                source.CopyPixels(new Avalonia.PixelRect(size), fb.Address, byteCount, fb.RowBytes);

                var bytes = new byte[byteCount];
                System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, byteCount);

                // BGRA: invert B, G, R; keep A. Source may be indexed-palette
                // PNG — CopyPixels normalises to Bgra8888 regardless.
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    bytes[i]     = (byte)(255 - bytes[i]);
                    bytes[i + 1] = (byte)(255 - bytes[i + 1]);
                    bytes[i + 2] = (byte)(255 - bytes[i + 2]);
                }

                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, byteCount);
            }

            return wb;
        }
        catch
        {
            return null;
        }
    }
    [ObservableProperty] private string _themeToggleText = Strings.ThemeDark;
}
