namespace VPNRouter.Core;

/// <summary>
/// Single source of truth for VPNRouter version across all platforms.
/// Both Windows GUI (AppBranding) and Mac GUI (MainWindowViewModel) read from here.
/// Update this constant before every release.
/// </summary>
public static class AppVersion
{
    public const string Version = "2.13.4";
}
