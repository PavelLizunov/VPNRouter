namespace VPNRouter.Core.Services;

/// <summary>
/// Global flag flipped on by Program.cs when the app was launched with
/// <c>--safe</c>. Consumed by SettingsLoader (skip parsing user yaml),
/// VpnEngine (ignore CustomCategories / CustomGroupApps / CustomApps /
/// ActiveProfile and run in FullTunnel mode with bundled catalogue only).
///
/// This is a static singleton because the flag is process-wide and
/// immutable after Main runs. Threading a parameter through every
/// service that touches config would balloon the diff for no real gain.
/// </summary>
public static class SafeMode
{
    /// <summary>
    /// True if the current process was started with <c>--safe</c>.
    /// Set by Program.cs before any other code runs; read everywhere
    /// config is loaded.
    /// </summary>
    public static bool Enabled { get; set; }
}
