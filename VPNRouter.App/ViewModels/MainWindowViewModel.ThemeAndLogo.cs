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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemThemePref))]
    [NotifyPropertyChangedFor(nameof(IsLightThemePref))]
    [NotifyPropertyChangedFor(nameof(IsDarkThemePref))]
    private string _themePreference = "system";

    public bool IsSystemThemePref => string.Equals(ThemePreference, "system", StringComparison.OrdinalIgnoreCase);
    public bool IsLightThemePref  => string.Equals(ThemePreference, "light",  StringComparison.OrdinalIgnoreCase);
    public bool IsDarkThemePref   => string.Equals(ThemePreference, "dark",   StringComparison.OrdinalIgnoreCase);

    private static readonly Bitmap _logoLight = LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot.png");
    private static readonly Bitmap _logoDark  = TryBuildInvertedLogo(_logoLight) ?? _logoLight;
    public Bitmap LogoSource => IsDarkTheme ? _logoDark : _logoLight;
    private static Bitmap LoadAsset(string uri) => new(AssetLoader.Open(new System.Uri(uri)));

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
