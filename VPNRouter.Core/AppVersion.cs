namespace VPNRouter.Core;

/// <summary>
/// Single source of truth for VPNRouter version across all platforms.
/// Both Windows GUI (AppBranding) and Mac GUI (MainWindowViewModel) read from here.
/// Update this constant before every release.
///
/// <para>IMPORTANT — rolling-rN policy: the string here MUST match the release
/// tag exactly, including the <c>-rN</c> suffix. Otherwise <see cref="VPNRouter.Core.Services.UpdateChecker"/>
/// can't distinguish e.g. <c>v2.25.0-r1</c> from <c>v2.25.0-r2</c> because both
/// compile with the same AppVersion, and SemVer treats <c>2.25.0-r2</c> as
/// OLDER than <c>2.25.0</c> stable → update check returns null ("up to date")
/// even though a newer prerelease is published. This bit the v2.25.0-r1 →
/// v2.25.0-r2 cycle; v2.25.1-r1 onwards carries the suffix. Bumping the Core
/// version (2.25.0 → 2.25.1) is a safer alternative when a test cycle has
/// already shipped without the <c>-rN</c> suffix embedded.</para>
/// </summary>
public static class AppVersion
{
    public const string Version = "2.30.1-r6";
}
