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
///
/// <para>v2.32.1-r2 — switched from <c>const string</c> to <c>static readonly
/// string</c>. Const string values for the declaring assembly are stored in the
/// CLR metadata Constant blob, which may end up at odd byte alignment that
/// <c>verify-release-integrity.yml</c>'s python UTF-16-LE full-buffer decoder
/// misses (the decoder only reads even-offset pairs). A static readonly
/// initializer forces the literal into the <c>#US</c> (UserString) heap
/// via <c>ldstr</c>, which is always 2-byte aligned and reliably picked up
/// by the CI scan. Consumers inline the value identically (compile-time
/// for const vs runtime field load for readonly — both produce a string
/// reference that compares with <c>==</c>).</para>
/// </summary>
public static class AppVersion
{
    public static readonly string Version = "2.40.0-r8";
}
