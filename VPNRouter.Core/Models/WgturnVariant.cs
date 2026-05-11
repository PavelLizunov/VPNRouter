namespace VPNRouter.Core.Models;

/// <summary>
/// Distribution variant of <c>wgturn-cli</c> published by
/// <c>PavelLizunov/wgturn-core</c>. The CLI uses an embedded Chromium
/// instance for VK Calls TURN signalling; small/slim builds require a
/// system Chromium to be installed, large/embedded builds ship the
/// browser bundle in-process.
/// </summary>
public enum WgturnVariant
{
    /// <summary>
    /// Slim build (~10 MB). Requires a system Chromium / Chrome /
    /// Edge install. Default for desktop platforms where the user
    /// already has a browser. Falls back to <see cref="Embedded"/>
    /// at launch time if no browser is found.
    /// </summary>
    Slim,

    /// <summary>
    /// Embedded build (~110-130 MB) that bundles a Chromium runtime
    /// in-process. Used when no system browser is available, or by
    /// kiosk / minimal Linux installs that lack a desktop GUI.
    /// </summary>
    Embedded,
}
