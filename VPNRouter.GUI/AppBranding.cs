using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VPNRouter.GUI;

/// <summary>
/// Centralized branding constants and logo/icon utilities for Virtual Penguin Network.
/// Logo is embedded as a resource and converted to Icon at runtime.
/// </summary>
internal static class AppBranding
{
    // ── Brand strings ──
    public const string AppName     = "Virtual Penguin Network";
    public const string ShortName   = "VPN";
    public const string Publisher   = "NiniTux";
    public const string Version     = "1.23.0";
    public static string WindowTitle => $"Virtual Penguin Network  v{Version}";
    public static string TrayTooltip => $"Virtual Penguin Network v{Version}";

    // ── Embedded resource ──
    private const string LogoResourceName = "VPNRouter.GUI.Resources.penguin_logo.png";

    private static Image? _logoCached;
    private static readonly Dictionary<int, Icon> _iconCache = new();

    /// <summary>Load the embedded penguin logo PNG (cached).</summary>
    public static Image GetLogo()
    {
        if (_logoCached != null) return _logoCached;

        var asm = typeof(AppBranding).Assembly;
        using var stream = asm.GetManifestResourceStream(LogoResourceName);

        if (stream == null)
        {
            // Fallback: list available resources for debugging
            var names = asm.GetManifestResourceNames();
            throw new InvalidOperationException(
                $"Embedded logo not found: '{LogoResourceName}'. " +
                $"Available: [{string.Join(", ", names)}]");
        }

        _logoCached = Image.FromStream(stream);
        return _logoCached;
    }

    /// <summary>
    /// Convert the embedded logo to a System.Drawing.Icon at the given size.
    /// Results are cached per size. Handles HICON cleanup properly.
    /// </summary>
    public static Icon GetIcon(int size = 32)
    {
        if (_iconCache.TryGetValue(size, out var cached))
            return cached;

        var logo = GetLogo();
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(logo, 0, 0, size, size);
        }

        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        // Clone to take ownership so we can free the native handle
        var result = (Icon)icon.Clone();
        DestroyIcon(hIcon);

        _iconCache[size] = result;
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
