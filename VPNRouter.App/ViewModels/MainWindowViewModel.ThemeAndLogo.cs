#nullable enable
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

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

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            ThemeVariant effective;
            if (IsSystemThemePref)
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Default;
                effective = ReadOsThemeVariant();
            }
            else
            {
                effective = IsDarkThemePref ? ThemeVariant.Dark : ThemeVariant.Light;
                Application.Current.RequestedThemeVariant = effective;
            }
            IsDarkTheme = effective == ThemeVariant.Dark;
        }

        OnPropertyChanged(nameof(VpnBadgeBrush));
        OnPropertyChanged(nameof(ZapretBadgeBrush));
        OnPropertyChanged(nameof(TgProxyBadgeBrush));

        foreach (var s in Servers)             s.NotifyThemeChanged();
        foreach (var s in SubscriptionServers) s.NotifyThemeChanged();
    }

    private static ThemeVariant ReadOsThemeVariant()
    {
        try
        {
            var os = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            return os == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch
        {
            return ThemeVariant.Light;
        }
    }

    internal static string NormalizeThemePref(string? raw)
    {
        if (string.Equals(raw, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
        if (string.Equals(raw, "light", StringComparison.OrdinalIgnoreCase)) return "light";
        return "system";
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!IsSystemThemePref) return;
        Dispatcher.UIThread.Post(ApplyTheme);
    }

    private IPlatformSettings? _wiredPlatformSettings;

    private void WireOsThemeFollow()
    {
        if (_wiredPlatformSettings != null) return;
        try
        {
            var ps = Application.Current?.PlatformSettings;
            if (ps == null) return;
            ps.ColorValuesChanged += OnPlatformColorValuesChanged;
            _wiredPlatformSettings = ps;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] WireOsThemeFollow: could not subscribe to ColorValuesChanged");
        }
    }

    private void UnwireOsThemeFollow()
    {
        try
        {
            if (_wiredPlatformSettings != null)
            {
                _wiredPlatformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
                _wiredPlatformSettings = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] UnwireOsThemeFollow: unsubscribe failed");
        }
    }
}
